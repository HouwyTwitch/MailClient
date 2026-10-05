using System.Text;

namespace MailClient.Core.Mime;

/// <summary>
/// Statistical detection of the single-byte Russian charsets (windows-1251, KOI8-R, KOI8-U, CP866, ISO-8859-5,
/// x-mac-cyrillic) for text that carries no usable charset label, common in mail from older Russian systems.
/// A correct decoding yields mostly lowercase Cyrillic with frequent Russian letters; a wrong one
/// yields capitals inside words ("йЧБОПЧ"), pseudo-graphics or Latin accented letters.
/// </summary>
public static class CyrillicCharset
{
    private static readonly int[] Candidates = { 1251, 20866, 21866, 866, 28595, 10007 };
    private const string Frequent = "оеаинтсрвлкмдпуяыьгзбчйхжшюцщэфъё";

    static CyrillicCharset() => CodePages.EnsureRegistered();

    /// <summary>Best Cyrillic decoding of <paramref name="bytes"/>; windows-1251 when undecidable.</summary>
    public static Encoding Detect(ReadOnlySpan<byte> bytes) => Encoding.GetEncoding(Best(bytes).codePage);

    /// <summary>
    /// The charset to use for bytes labelled <paramref name="declared"/>: the label, unless the bytes clearly
    /// are Russian text in another single-byte charset (a frequent mislabelling by Russian mailers).
    /// </summary>
    public static Encoding Correct(ReadOnlySpan<byte> bytes, Encoding declared)
    {
        if (!declared.IsSingleByte) return declared;
        var (best, bestScore, cyrillicShare) = Best(bytes);
        if (best == declared.CodePage || cyrillicShare < 0.6) return declared;
        var declaredScore = Score(declared.GetString(bytes), out _);
        return bestScore > 0 && declaredScore < bestScore * 0.5 ? Encoding.GetEncoding(best) : declared;
    }

    private static (int codePage, double score, double cyrillicShare) Best(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes.Length > 64 * 1024 ? bytes[..(64 * 1024)] : bytes;
        int best = 1251;
        double bestScore = double.MinValue, share = 0;
        foreach (var cp in Candidates)
        {
            var score = Score(Encoding.GetEncoding(cp).GetString(sample), out var s);
            if (score > bestScore) { bestScore = score; best = cp; share = s; }
        }
        return (best, bestScore, share);
    }

    /// <summary>Plausibility of Russian text; <paramref name="cyrillicShare"/> is the share of Cyrillic among letters.</summary>
    private static double Score(string text, out double cyrillicShare)
    {
        double score = 0;
        int letters = 0, cyrillic = 0, high = 0;
        char prev = ' ';
        foreach (var c in text)
        {
            if (c > '\u007F' && !char.IsWhiteSpace(c)) high++;
            if (char.IsLetter(c))
            {
                letters++;
                bool isCyr = c is >= 'Ѐ' and <= 'ӿ';
                if (isCyr)
                {
                    cyrillic++;
                    if (char.IsLower(c)) score += Frequent.Contains(c) ? 2 : 0.5;
                    // A capital right after a lowercase Cyrillic letter: typical of a wrong KOI8/1251 decoding.
                    if (char.IsUpper(c) && char.IsLower(prev) && prev is >= 'Ѐ' and <= 'ӿ') score -= 3;
                    if (c is 'ъ' or 'Ъ' or 'ё' or 'Ё' && !char.IsLetter(prev)) score -= 2; // never start a word
                }
                else if (c > '\u007F')
                {
                    score -= 2; // accented Latin, Greek… instead of Cyrillic
                }
            }
            else if (c > '\u007F' && !char.IsWhiteSpace(c) && c is not ('«' or '»' or '—' or '–' or '№' or '…' or '„' or '“' or '”' or '’' or ' ' or '°' or '·' or '©' or '®' or '€'))
            {
                score -= 2; // pseudo-graphics, control characters, unusual symbols
            }
            prev = c;
        }
        cyrillicShare = letters == 0 ? 0 : (double)cyrillic / letters;
        return high == 0 ? 0 : score / high;
    }
}
