using Avalonia.Media;

namespace MailClient.App.Services;

/// <summary>Fonts and sizes offered in the message editor on Linux.</summary>
public static class EditorFonts
{
    /// <summary>Arial is what recipients on Windows see; on Linux fontconfig shows it with Liberation Sans.</summary>
    public const string DefaultFamily = "Arial";
    public const double DefaultSize = 11;

    /// <summary>
    /// Fonts that look the same for recipients: the Windows standard fonts (metric-compatible Liberation fonts
    /// stand in for them on Linux) and the PT Astra fonts that ship with Astra Linux.
    /// </summary>
    private static readonly string[] Candidates =
    [
        "Arial", "Times New Roman", "Courier New", "Calibri", "Verdana", "Tahoma", "Georgia",
        "PT Astra Sans", "PT Astra Serif", "Liberation Sans", "Liberation Serif", "Liberation Mono", "DejaVu Sans",
    ];

    public static IReadOnlyList<string> Families { get; } = LoadFamilies();

    public static IReadOnlyList<double> Sizes { get; } = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 28, 36, 48];

    private static List<string> LoadFamilies()
    {
        try
        {
            var installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            // The Windows names stay available: the message carries the name, the reader's computer picks the font.
            return Candidates.Where(f => installed.Contains(f) || f is "Arial" or "Times New Roman" or "Courier New").ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // No font manager yet (tests, command line).
            return [.. Candidates];
        }
    }
}
