using System.Text;

namespace MailClient.Core.Rendering;

public static class TextUtil
{
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
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '​' or '‌' or '‍' or '﻿' or '­' or '͏')
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
