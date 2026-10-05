namespace MailClient.Core.Diagnostics;

/// <summary>
/// Diagnostic hook for the protocol libraries. The application routes it to its log file;
/// messages never contain passwords or message bodies.
/// </summary>
public static class MailLog
{
    public static Action<string>? Info { get; set; }
    public static Action<string>? Warn { get; set; }

    /// <summary>Exception with its inner exceptions on one line, e.g. "HttpRequestException: … ← IOException: …".</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 5; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" ← ", parts);
    }
}
