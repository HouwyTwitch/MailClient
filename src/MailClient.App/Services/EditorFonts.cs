using System.Windows.Media;

namespace MailClient.App.Services;

/// <summary>Fonts and sizes offered in the message editor.</summary>
public static class EditorFonts
{
    public const string DefaultFamily = "Calibri";
    public const double DefaultSize = 11;

    /// <summary>
    /// Fonts that ship with Windows and display the same for recipients (a font the reader does not have would be
    /// replaced by a different one). Only fonts installed on this computer are listed.
    /// </summary>
    private static readonly string[] Candidates =
    [
        "Calibri", "Arial", "Times New Roman", "Segoe UI", "Verdana", "Tahoma", "Georgia",
        "Cambria", "Trebuchet MS", "Courier New", "Consolas",
    ];

    public static IReadOnlyList<string> Families { get; } = LoadFamilies();

    public static IReadOnlyList<double> Sizes { get; } = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 28, 36, 48];

    private static List<string> LoadFamilies()
    {
        try
        {
            var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
            var available = Candidates.Where(installed.Contains).ToList();
            if (available.Count > 0) return available;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось получить список шрифтов: {ex.Message}");
        }
        return [.. Candidates];
    }
}
