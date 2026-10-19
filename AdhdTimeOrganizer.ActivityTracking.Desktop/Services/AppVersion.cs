using System.Reflection;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// This build's version — the csproj's <c>&lt;Version&gt;</c>, the one thing the tray shows, the server stores and the
/// installer is packed under.
/// </summary>
/// <remarks>
/// <b>The header.</b> Every request carries <see cref="HeaderName"/>: <c>desktop/1.1.0</c>. The server keeps the last one
/// per tracker kind beside "last seen" (<c>TrackerSourceStatus.ClientVersion</c>), shows it on the sources panel, and lets
/// the tracker-silence notification say "out of date" when it is below the configured minimum. The commit hash the SDK
/// appends to the informational version (<c>1.1.0+a1b2c3d</c>) stays out of it and the tray; "Copy diagnostics" keeps it.
/// </remarks>
public static class AppVersion
{
    public const string HeaderName = "X-Client-Version";

    public static string Current { get; } = FromInformational(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>What every request names itself as.</summary>
    public static string Header { get; } = HeaderValue(Current);

    internal static string HeaderValue(string version) => $"desktop/{version}";

    /// <summary>The version without the SDK's <c>+commit</c> suffix; "unknown" when there is none to read.</summary>
    internal static string FromInformational(string? informational)
    {
        var version = informational?.Split('+', 2)[0].Trim();
        return string.IsNullOrEmpty(version) ? "unknown" : version;
    }
}
