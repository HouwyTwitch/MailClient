using System.Text;

namespace MailClient.Core.Rendering;

public static class TextUtil
{
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file name Windows accepts, whatever the attachment or subject contains: forbidden characters replaced,
    /// no trailing dots/spaces, reserved device names (CON, NUL, COM1…) prefixed, length kept under the
    /// 255-character limit with the extension preserved.
    /// </summary>
    public static string SafeFileName(string? name, int maxLength = 150)
    {
        var sb = new StringBuilder(name?.Length ?? 0);
        foreach (var c in name ?? "")
        {
            if (c is '\t' or '\r' or '\n') sb.Append(' ');          // a subject split over lines
            else if (c < 32) continue;                               // other control characters
            else sb.Append("\"<>|:*?\\/".Contains(c) ? '_' : c);
        }
        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) return "attachment";
        var dot = clean.LastIndexOf('.');
        var stem = dot > 0 ? clean[..dot] : clean;
        var ext = dot > 0 && clean.Length - dot <= 16 ? clean[dot..] : "";
        if (ext.Length == 0) stem = clean;
        if (ReservedFileNames.Contains(stem.TrimEnd())) stem = "_" + stem;
        if (stem.Length + ext.Length > maxLength) stem = stem[..Math.Max(1, maxLength - ext.Length)].TrimEnd('.', ' ');
        return stem + ext;
    }

    /// <summary>
    /// Collapses line breaks, tabs, repeated spaces and invisible characters into single spaces and
    /// truncates with an ellipsis. Used for message-list rows: a WPF TextBlock honours embedded newlines
    /// even with trimming, which made rows of some messages several lines tall.
    /// </summary>
    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        bool space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u00AD' or '\u034F')
            {
                if (sb.Length > 0) space = true;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
            if (sb.Length >= max)
            {
                sb.Append('…');
                break;
            }
        }
        return sb.ToString();
    }
}
