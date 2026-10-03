using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MyHealth.Web.Common;

public static class AppRenderModes
{
    /// <summary>
    /// Interactive server rendering without prerendering: every API call runs once, in the circuit, where the
    /// signed-in user's session and the selected organization are available.
    /// </summary>
    public static readonly IComponentRenderMode Interactive = new InteractiveServerRenderMode(prerender: false);
}
