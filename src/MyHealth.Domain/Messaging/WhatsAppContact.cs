using System.Security.Cryptography;
using System.Text;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Messaging;

public enum WhatsAppContactStatus
{
    /// <summary>A code was sent to the number; nothing else is sent until the person confirms it.</summary>
    PendingVerification = 1,

    Active = 2,

    /// <summary>The person turned WhatsApp off, in the app or by replying "SAIR". Kept for the opt-out record.</summary>
    OptedOut = 3,
}

public sealed record WhatsAppContactChange(DateTimeOffset At, WhatsAppContactStatus Status, string Reason);

/// <summary>A phone number in E.164 form (<c>+5511987654321</c>). Brazilian numbers may be typed without the country code.</summary>
public static class PhoneNumbers
{
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var international = input.TrimStart().StartsWith('+');
        var digits = new string([.. input.Where(char.IsDigit)]);

        // Brazil: area code + 8 or 9 digits, written without +55.
        if (!international && digits.Length is 10 or 11 && digits[0] != '0')
        {
            digits = "55" + digits;
        }

        return digits.Length is >= 10 and <= 15 && digits[0] != '0' ? "+" + digits : null;
    }

    /// <summary>For screens and logs: <c>+55 •••• ••4321</c>.</summary>
    public static string Mask(string phoneNumber) => phoneNumber.Length <= 4 ? "••••" : $"{phoneNumber[..3]} •••• ••{phoneNumber[^4..]}";
}

/// <summary>
/// A person's WhatsApp channel. Opt-in (LGPD consent) is explicit and needs the number confirmed with a code sent there,
/// so nobody can enroll someone else's phone. Only generic messages with a link back to the app go out on WhatsApp,
/// never clinical content. <see cref="AllowProfessionalContact"/> lets the person's professionals see the number to
/// start a chat themselves (click-to-chat).
/// </summary>
public sealed class WhatsAppContact : AggregateRoot<Guid>
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 5;

    private WhatsAppContact(Guid id) : base(id) { }

    private WhatsAppContact() { }

    public Guid UserId { get; private set; }

    public string PhoneNumber { get; private set; } = null!;

    public WhatsAppContactStatus Status { get; private set; }

    public bool AllowProfessionalContact { get; private set; }

    public string? VerificationCodeHash { get; private set; }

    public DateTimeOffset? VerificationSentAt { get; private set; }

    public int VerificationAttempts { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public List<WhatsAppContactChange> History { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == WhatsAppContactStatus.Active;

    public static Result<WhatsAppContact> Start(Guid userId, string phoneNumber, bool allowProfessionalContact, string code, DateTimeOffset now)
    {
        var contact = new WhatsAppContact(Guid.CreateVersion7(now)) { UserId = userId, CreatedAt = now };
        var started = contact.Restart(phoneNumber, allowProfessionalContact, code, now);
        return started.IsFailure ? started.Error : contact;
    }

    /// <summary>Sends a new code for a (possibly different) number. The previous number stops receiving messages.</summary>
    public Result Restart(string phoneNumber, bool allowProfessionalContact, string code, DateTimeOffset now)
    {
        var normalized = PhoneNumbers.Normalize(phoneNumber);
        if (normalized is null)
        {
            return WhatsAppErrors.InvalidPhoneNumber;
        }

        if (Status == WhatsAppContactStatus.PendingVerification && normalized == PhoneNumber && now - VerificationSentAt < ResendInterval)
        {
            return WhatsAppErrors.WaitBeforeResending;
        }

        PhoneNumber = normalized;
        AllowProfessionalContact = allowProfessionalContact;
        Status = WhatsAppContactStatus.PendingVerification;
        VerificationCodeHash = Hash(code);
        VerificationSentAt = now;
        VerificationAttempts = 0;
        VerifiedAt = null;
        Change(WhatsAppContactStatus.PendingVerification, "Código enviado", now);
        return Result.Success();
    }

    public Result Confirm(string code, DateTimeOffset now)
    {
        if (Status == WhatsAppContactStatus.Active)
        {
            return Result.Success();
        }

        if (Status != WhatsAppContactStatus.PendingVerification || VerificationCodeHash is null || now - VerificationSentAt > CodeLifetime
            || VerificationAttempts >= MaxAttempts)
        {
            return WhatsAppErrors.CodeExpired;
        }

        VerificationAttempts++;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(code.Trim())), Encoding.ASCII.GetBytes(VerificationCodeHash)))
        {
            UpdatedAt = now;
            return WhatsAppErrors.WrongCode;
        }

        Status = WhatsAppContactStatus.Active;
        VerificationCodeHash = null;
        VerifiedAt = now;
        Change(WhatsAppContactStatus.Active, "Número confirmado", now);
        return Result.Success();
    }

    public Result SetProfessionalContact(bool allow, DateTimeOffset now)
    {
        if (Status == WhatsAppContactStatus.OptedOut)
        {
            return WhatsAppErrors.NotActive;
        }

        AllowProfessionalContact = allow;
        UpdatedAt = now;
        return Result.Success();
    }

    public void OptOut(string reason, DateTimeOffset now)
    {
        if (Status == WhatsAppContactStatus.OptedOut)
        {
            return;
        }

        Status = WhatsAppContactStatus.OptedOut;
        AllowProfessionalContact = false;
        VerificationCodeHash = null;
        Change(WhatsAppContactStatus.OptedOut, reason, now);
    }

    private void Change(WhatsAppContactStatus status, string reason, DateTimeOffset now)
    {
        History.Add(new WhatsAppContactChange(now, status, reason));
        UpdatedAt = now;
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

public interface IWhatsAppContactRepository : IRepository<WhatsAppContact, Guid>
{
    Task<WhatsAppContact?> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Contacts using this number that are not opted out (an inbound "SAIR" applies to all of them).</summary>
    Task<IReadOnlyList<WhatsAppContact>> ListByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

public static class WhatsAppErrors
{
    public static readonly Error InvalidPhoneNumber = Error.Validation("WhatsApp.InvalidPhoneNumber", "Enter a mobile number with area code, e.g. (11) 98765-4321.");
    public static readonly Error WaitBeforeResending = Error.Conflict("WhatsApp.WaitBeforeResending", "A code was just sent. Wait a minute before asking for another.");
    public static readonly Error CodeExpired = Error.Validation("WhatsApp.CodeExpired", "The code expired or too many attempts were made. Ask for a new code.");
    public static readonly Error WrongCode = Error.Validation("WhatsApp.WrongCode", "The code does not match.");
    public static readonly Error NotActive = Error.Conflict("WhatsApp.NotActive", "WhatsApp is not active for this account.");
    public static readonly Error SendFailed = Error.Failure("WhatsApp.SendFailed", "The WhatsApp message could not be sent. Check the number and try again.");
    public static readonly Error ContactNotShared = Error.NotFound("WhatsApp.ContactNotShared", "This client has not shared a WhatsApp number with professionals.");
    public static readonly Error InvalidWebhook = Error.Forbidden("WhatsApp.InvalidWebhook", "The webhook signature is missing or invalid.");
}
