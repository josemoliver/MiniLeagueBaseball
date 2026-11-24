using System.Reflection;

namespace MiniLeague.Web.Services;

/// <summary>The version of the app: whatever the project file says (<c>&lt;Version&gt;</c>), so there is one place to change it.</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;

        // The informational version, without the "+commit" the SDK may add; or the plain assembly version if it was trimmed away.
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
        return version.Length > 0 ? version : assembly.GetName().Version?.ToString(3) ?? "";
    }
}
