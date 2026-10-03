using System.Globalization;
using System.Security.Cryptography;
using FluentValidation;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Abstractions.WhatsApp;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Messaging;

namespace MyHealth.Application.Messaging;

public sealed record WhatsAppStatusResponse(string? PhoneNumber, WhatsAppContactStatus? Status, bool AllowProfessionalContact, DateTimeOffset? VerifiedAt);

public sealed record GetMyWhatsAppQuery : IQuery<WhatsAppStatusResponse>;

internal sealed class GetMyWhatsAppHandler(IUserContext user, IWhatsAppContactRepository contacts) : IQueryHandler<GetMyWhatsAppQuery, WhatsAppStatusResponse>
{
    public async Task<Result<WhatsAppStatusResponse>> Handle(GetMyWhatsAppQuery query, CancellationToken cancellationToken) =>
        WhatsAppMapping.ToResponse(await contacts.GetByUserAsync(user.UserId, cancellationToken));
}

/// <summary>
/// The person opts in to WhatsApp with a number (or changes it): a 6-digit code is sent there and nothing else is
/// sent until it is confirmed. <paramref name="AllowProfessionalContact"/> lets their professionals see the number.
/// </summary>
public sealed record StartWhatsAppOptInCommand(string PhoneNumber, bool AllowProfessionalContact) : ICommand<WhatsAppStatusResponse>;

internal sealed class StartWhatsAppOptInValidator : AbstractValidator<StartWhatsAppOptInCommand>
{
    public StartWhatsAppOptInValidator() => RuleFor(c => c.PhoneNumber).NotEmpty().MaximumLength(30);
}

internal sealed class StartWhatsAppOptInHandler(
    IUserContext user, IWhatsAppContactRepository contacts, IWhatsAppProvider whatsApp, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<StartWhatsAppOptInCommand, WhatsAppStatusResponse>
{
    public async Task<Result<WhatsAppStatusResponse>> Handle(StartWhatsAppOptInCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var contact = await contacts.GetByUserAsync(user.UserId, cancellationToken);
        if (contact is null)
        {
            var created = WhatsAppContact.Start(user.UserId, command.PhoneNumber, command.AllowProfessionalContact, code, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            contact = created.Value;
            contacts.Add(contact);
        }
        else
        {
            var restarted = contact.Restart(command.PhoneNumber, command.AllowProfessionalContact, code, now);
            if (restarted.IsFailure)
            {
                return restarted.Error;
            }
        }

        // Sent before saving: if the number cannot receive it, nothing is recorded.
        var sent = await whatsApp.SendTemplateAsync(
            new WhatsAppTemplateMessage(contact.PhoneNumber, WhatsAppTemplates.VerificationCode, WhatsAppTemplates.Language, [code]), cancellationToken);
        if (!sent.IsSent)
        {
            return WhatsAppErrors.SendFailed;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return WhatsAppMapping.ToResponse(contact);
    }
}

public sealed record ConfirmWhatsAppCommand(string Code) : ICommand<WhatsAppStatusResponse>;

internal sealed class ConfirmWhatsAppValidator : AbstractValidator<ConfirmWhatsAppCommand>
{
    public ConfirmWhatsAppValidator() => RuleFor(c => c.Code).NotEmpty().MaximumLength(10);
}

internal sealed class ConfirmWhatsAppHandler(IUserContext user, IWhatsAppContactRepository contacts, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ConfirmWhatsAppCommand, WhatsAppStatusResponse>
{
    public async Task<Result<WhatsAppStatusResponse>> Handle(ConfirmWhatsAppCommand command, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetByUserAsync(user.UserId, cancellationToken);
        if (contact is null)
        {
            return WhatsAppErrors.NotActive;
        }

        var wasActive = contact.IsActive;
        var confirmed = contact.Confirm(command.Code, clock.GetUtcNow());

        // Wrong attempts are counted even though the request fails.
        if (confirmed.IsSuccess && !wasActive)
        {
            audit.Record(AuditActions.Grant, nameof(WhatsAppContact), contact.Id, subjectUserId: null, tenantId: null);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return confirmed.IsFailure ? confirmed.Error : WhatsAppMapping.ToResponse(contact);
    }
}

public sealed record SetWhatsAppProfessionalContactCommand(bool Allow) : ICommand<WhatsAppStatusResponse>;

internal sealed class SetWhatsAppProfessionalContactHandler(IUserContext user, IWhatsAppContactRepository contacts, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SetWhatsAppProfessionalContactCommand, WhatsAppStatusResponse>
{
    public async Task<Result<WhatsAppStatusResponse>> Handle(SetWhatsAppProfessionalContactCommand command, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetByUserAsync(user.UserId, cancellationToken);
        if (contact is null)
        {
            return WhatsAppErrors.NotActive;
        }

        var result = contact.SetProfessionalContact(command.Allow, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return WhatsAppMapping.ToResponse(contact);
    }
}

/// <summary>The person turns WhatsApp off in the app. The opt-out is kept as a record.</summary>
public sealed record OptOutOfWhatsAppCommand : ICommand;

internal sealed class OptOutOfWhatsAppHandler(IUserContext user, IWhatsAppContactRepository contacts, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<OptOutOfWhatsAppCommand>
{
    public async Task<Result> Handle(OptOutOfWhatsAppCommand command, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetByUserAsync(user.UserId, cancellationToken);
        if (contact is null || contact.Status == WhatsAppContactStatus.OptedOut)
        {
            return Result.Success();
        }

        contact.OptOut("Desativado no app", clock.GetUtcNow());
        audit.Record(AuditActions.Revoke, nameof(WhatsAppContact), contact.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>A link that opens a WhatsApp chat with the client; the professional types the message (nothing automatic).</summary>
public sealed record ProfessionalWhatsAppResponse(string PhoneNumber, string ChatUrl);

/// <summary>
/// The professional of an active relationship, in its tenant, gets the client's WhatsApp number — only when the client
/// confirmed it and allowed professionals to contact them. Every read is audited.
/// </summary>
public sealed record GetClientWhatsAppQuery(Guid RelationshipId) : IQuery<ProfessionalWhatsAppResponse>;

internal sealed class GetClientWhatsAppHandler(
    IUserContext user, ITenantContext tenant, ICareRelationshipRepository relationships, IWhatsAppContactRepository contacts, IAuditLog audit)
    : IQueryHandler<GetClientWhatsAppQuery, ProfessionalWhatsAppResponse>
{
    public async Task<Result<ProfessionalWhatsAppResponse>> Handle(GetClientWhatsAppQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var relationship = await relationships.GetByIdAsync(query.RelationshipId, cancellationToken);
        if (relationship is null || relationship.ProfessionalUserId != user.UserId || relationship.TenantId != membership.Value.TenantId)
        {
            if (relationship is not null)
            {
                await audit.RecordDeniedAsync(nameof(CareRelationship), relationship.Id, membership.Value.TenantId, cancellationToken);
            }

            return CareRelationshipErrors.NotFound;
        }

        if (!relationship.IsActive || relationship.ClientId is not { } clientId)
        {
            return CareRelationshipErrors.NotActive;
        }

        var contact = await contacts.GetByUserAsync(clientId, cancellationToken);
        if (contact is not { IsActive: true, AllowProfessionalContact: true })
        {
            return WhatsAppErrors.ContactNotShared;
        }

        await audit.RecordReadAsync(nameof(WhatsAppContact), contact.Id, clientId, relationship.TenantId, cancellationToken);
        return new ProfessionalWhatsAppResponse(contact.PhoneNumber, $"https://wa.me/{contact.PhoneNumber.TrimStart('+')}");
    }
}

public sealed record WhatsAppWebhookResult(int Messages, int OptOuts);

/// <summary>
/// A message sent to MyHealth's WhatsApp number. Only opt-out words are acted on ("SAIR", "PARAR", "STOP"…); MyHealth
/// does not hold conversations on WhatsApp, so anything else is ignored (the template tells people to use the app).
/// </summary>
public sealed record ProcessWhatsAppWebhookCommand(string Body, IReadOnlyDictionary<string, string> Headers) : ICommand<WhatsAppWebhookResult>;

internal sealed partial class ProcessWhatsAppWebhookHandler(
    IWhatsAppProvider whatsApp, IWhatsAppContactRepository contacts, IUnitOfWork unitOfWork, TimeProvider clock,
    ILogger<ProcessWhatsAppWebhookHandler> logger)
    : ICommandHandler<ProcessWhatsAppWebhookCommand, WhatsAppWebhookResult>
{
    private static readonly HashSet<string> OptOutWords = new(StringComparer.OrdinalIgnoreCase) { "SAIR", "PARAR", "STOP", "CANCELAR", "DESCADASTRAR" };

    public async Task<Result<WhatsAppWebhookResult>> Handle(ProcessWhatsAppWebhookCommand command, CancellationToken cancellationToken)
    {
        var messages = whatsApp.ReadWebhook(command.Body, command.Headers);
        if (messages is null)
        {
            return WhatsAppErrors.InvalidWebhook;
        }

        var optOuts = 0;
        var now = clock.GetUtcNow();
        foreach (var message in messages)
        {
            var word = message.Text.Trim().TrimEnd('.', '!').Trim();
            if (!OptOutWords.Contains(word) || PhoneNumbers.Normalize(message.FromPhoneNumber) is not { } phone)
            {
                continue;
            }

            foreach (var contact in await contacts.ListByPhoneAsync(phone, cancellationToken))
            {
                // Anonymous request (no actor for the audit trail): the opt-out is recorded in the contact's own history.
                contact.OptOut("Respondeu SAIR no WhatsApp", now);
                optOuts++;
            }

            // Confirming the opt-out is allowed (and expected) by WhatsApp's rules; a failure here changes nothing.
            var confirmation = await whatsApp.SendTemplateAsync(
                new WhatsAppTemplateMessage(phone, WhatsAppTemplates.OptOutConfirmation, WhatsAppTemplates.Language, []), cancellationToken);
            if (!confirmation.IsSent)
            {
                LogConfirmationFailed(logger, confirmation.Status);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new WhatsAppWebhookResult(messages.Count, optOuts);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp opt-out confirmation not sent ({Status})")]
    private static partial void LogConfirmationFailed(ILogger logger, WhatsAppSendStatus status);
}

internal static class WhatsAppMapping
{
    public static WhatsAppStatusResponse ToResponse(WhatsAppContact? contact) =>
        contact is null
            ? new WhatsAppStatusResponse(null, null, false, null)
            : new WhatsAppStatusResponse(contact.PhoneNumber, contact.Status, contact.AllowProfessionalContact, contact.VerifiedAt);
}
