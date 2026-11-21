using Microsoft.JSInterop;

namespace MiniLeague.Web.Services;

/// <summary>Copies text to the clipboard and prints the page (used for the box score).</summary>
public class ExportService(IJSRuntime js)
{
    /// <summary>True if the text reached the clipboard.</summary>
    public async Task<bool> CopyAsync(string text)
    {
        try { return await js.InvokeAsync<bool>("mlbExport.copy", text); }
        catch (JSException) { return false; }
    }

    public async Task PrintAsync()
    {
        try { await js.InvokeVoidAsync("mlbExport.print"); }
        catch (JSException) { }
    }
}
