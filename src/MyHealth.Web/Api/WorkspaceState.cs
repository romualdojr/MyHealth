using System.Security.Cryptography;
using MyHealth.Web.Common.Api;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace MyHealth.Web.Api;

/// <summary>
/// Who is signed in and which organization they are acting for, shared by the layout and the page in one circuit.
/// Switching organization only changes the <c>X-Tenant-Id</c> sent from now on; pages reload their data (MT-07) and
/// the API re-checks the membership on every call. The choice is remembered per browser in protected storage.
/// </summary>
public sealed class WorkspaceState(MyHealthApiClient api, ApiSession session, ProtectedLocalStorage storage)
{
    private const string TenantKey = "myhealth.tenant";
    private Task? _loading;

    public event Action? Changed;

    public MeResponse? Me { get; private set; }

    public ApiProblem? Problem { get; private set; }

    public MyMembership? Membership => Me?.Memberships.FirstOrDefault(m => m.TenantId == session.TenantId);

    public bool IsTenantAdministrator => Membership?.IsAdministrator == true;

    public bool Practices(string discipline) => Membership?.Disciplines.Contains(discipline) == true;

    /// <summary>
    /// Loads the profile and the remembered organization once per circuit, then tells the components that rendered
    /// before it finished (navigation, organization switcher) to render again.
    /// </summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAndNotifyAsync();

    private async Task LoadAndNotifyAsync()
    {
        await LoadAsync();
        Changed?.Invoke();
    }

    public async Task SelectTenantAsync(Guid? tenantId)
    {
        session.TenantId = tenantId is { } id && Me?.Memberships.Any(m => m.TenantId == id) == true ? id : null;
        try
        {
            if (session.TenantId is { } selected)
            {
                await storage.SetAsync(TenantKey, selected);
            }
            else
            {
                await storage.DeleteAsync(TenantKey);
            }
        }
        catch (InvalidOperationException)
        {
            // JavaScript is not available yet (e.g. while rendering statically); the choice applies to this circuit only.
        }

        Changed?.Invoke();
    }

    public async Task ReloadAsync()
    {
        _loading = LoadAsync();
        await _loading;
        Changed?.Invoke();
    }

    private async Task LoadAsync()
    {
        var me = await api.GetAsync<MeResponse>("me");
        Me = me.Value;
        Problem = me.Problem;
        if (Me is null)
        {
            return;
        }

        Guid? remembered = null;
        try
        {
            var stored = await storage.GetAsync<Guid>(TenantKey);
            remembered = stored.Success ? stored.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
        {
            // No JavaScript yet, or a value protected with an old key: start without a selection.
        }

        // A single organization is selected automatically; otherwise the remembered one, if still a membership.
        var tenantId = Me.Memberships.Count == 1 ? Me.Memberships[0].TenantId : remembered;
        session.TenantId = tenantId is { } id && Me.Memberships.Any(m => m.TenantId == id) ? id : null;
    }
}
