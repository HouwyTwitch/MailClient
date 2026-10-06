using System.Reflection;

namespace MailClient.App.Services;

/// <summary>Product version as stamped by the build ("1.2.0", plus the commit for builds from CI).</summary>
public static class AppInfo
{
    /// <summary>"1.2.0" or "1.2.0 (сборка a1b2c3d4)".</summary>
    public static string Version { get; } = Describe(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "");

    /// <summary>"1.2.0+a1b2c3d4e5…" (informational version with the source commit) → "1.2.0 (сборка a1b2c3d4)".</summary>
    public static string Describe(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return informational;
        var commit = informational[(plus + 1)..];
        return $"{informational[..plus]} (сборка {commit[..Math.Min(8, commit.Length)]})";
    }
}
