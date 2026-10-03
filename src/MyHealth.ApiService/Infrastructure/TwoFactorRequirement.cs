using Microsoft.AspNetCore.Authorization;
using MyHealth.Application.Abstractions.Authentication;

namespace MyHealth.ApiService.Infrastructure;

/// <summary>Platform operators must protect their account with a second factor (RNF-02).</summary>
internal sealed class TwoFactorRequirement : IAuthorizationRequirement;

internal sealed class TwoFactorRequirementHandler(IHttpContextAccessor httpContextAccessor, SecurityPolicy security)
    : AuthorizationHandler<TwoFactorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorRequirement requirement)
    {
        if (!security.RequireTwoFactorForProfessionals)
        {
            context.Succeed(requirement);
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var user = httpContext.RequestServices.GetRequiredService<IUserContext>();
        if (await user.HasTwoFactorAsync(httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, SecurityErrors.TwoFactorRequired.Description));
        }
    }
}
