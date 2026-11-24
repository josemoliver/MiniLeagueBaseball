using Microsoft.JSInterop;

namespace MiniLeague.Web.Services;

/// <summary>Installing the game as an app (js/pwa.js holds the browser's install prompt).</summary>
public class PwaService(IJSRuntime js)
{
    /// <summary>True when the browser offers to install the game and it is not installed already.</summary>
    public async Task<bool> CanInstallAsync()
    {
        try { return await js.InvokeAsync<bool>("mlbPwa.canInstall"); }
        catch (JSException) { return false; }
    }

    /// <summary>Shows the browser's install dialog: "accepted", "dismissed" or "unavailable".</summary>
    public async Task<string> InstallAsync()
    {
        try { return await js.InvokeAsync<string>("mlbPwa.install"); }
        catch (JSException) { return "unavailable"; }
    }
}
