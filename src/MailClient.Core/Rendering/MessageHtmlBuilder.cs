using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailClient.Core.Models;

namespace MailClient.Core.Rendering;

/// <summary>
/// Turns a message body into a self-contained, locked-down HTML document for the preview pane:
/// inline (cid:) images are embedded as data: URIs and a Content-Security-Policy blocks scripts,
/// and — unless allowed — remote content (tracking pixels etc.).
/// </summary>
public static partial class MessageHtmlBuilder
{
    public static string Build(
        MailMessage message,
        IReadOnlyDictionary<string, (string contentType, byte[] data)>? inlineParts,
        bool allowRemoteContent,
        bool darkMode)
    {
        var body = message.BodyIsHtml ? message.Body : TextToHtml(message.Body);
        body = StripDangerous(body);

        if (inlineParts != null && inlineParts.Count > 0)
        {
            body = CidRegex().Replace(body, m =>
            {
                var cid = WebUtility.UrlDecode(m.Groups["cid"].Value).Trim('<', '>');
                return inlineParts.TryGetValue(cid, out var part)
                    ? $"data:{part.contentType};base64,{Convert.ToBase64String(part.data)}"
                    : m.Value;
            });
        }

        var img = allowRemoteContent ? "data: https: http:" : "data:";
        var csp = $"default-src 'none'; img-src {img}; style-src 'unsafe-inline'{(allowRemoteContent ? " https: http:" : "")}; font-src data:{(allowRemoteContent ? " https:" : "")};";

        // Dark mode: invert only the page background/text for plain messages; HTML mail keeps its own colors
        // on a light "paper" so that designs stay readable.
        var bg = darkMode && !message.BodyIsHtml ? "#1f1f1f" : "#ffffff";
        var fg = darkMode && !message.BodyIsHtml ? "#e6e6e6" : "#1a1a1a";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append($"<meta http-equiv=\"Content-Security-Policy\" content=\"{csp}\">");
        sb.Append("<base target=\"_blank\">");
        sb.Append("<style>");
        sb.Append($"html,body{{margin:0;padding:0;background:{bg};color:{fg};}}");
        sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.45;padding:16px 20px;word-wrap:break-word;}");
        sb.Append("img{max-width:100%;height:auto;}pre.plain{white-space:pre-wrap;font-family:Consolas,'Cascadia Mono',monospace;font-size:13px;margin:0}");
        sb.Append("blockquote{border-left:3px solid #8a8a8a;margin:0 0 0 4px;padding-left:10px;color:#555}a{color:#0067c0}");
        sb.Append("</style></head><body>");
        sb.Append(ExtractBodyContent(body));
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string TextToHtml(string text)
    {
        var encoded = WebUtility.HtmlEncode(text ?? "");
        encoded = UrlRegex().Replace(encoded, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        return $"<pre class=\"plain\">{encoded}</pre>";
    }

    /// <summary>Very small HTML → text conversion used for quoting and previews.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = ScriptStyleRegex().Replace(html, "");
        s = BreakRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = MultiBlankRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>Removes scripts, event handlers, javascript: URLs and frames. CSP is the main defense; this is defense in depth.</summary>
    public static string StripDangerous(string html)
    {
        var s = ScriptStyleRegex().Replace(html, m => m.Value.StartsWith("<style", StringComparison.OrdinalIgnoreCase) ? m.Value : "");
        s = DangerousTagRegex().Replace(s, "");
        s = EventHandlerRegex().Replace(s, "");
        s = JsUrlRegex().Replace(s, "${attr}=\"#\"");
        return s;
    }

    /// <summary>Content of &lt;body&gt; (plus &lt;style&gt; blocks), suitable for embedding into another document.</summary>
    public static string BodyFragment(string html) => ExtractBodyContent(html);

    private static string ExtractBodyContent(string html)
    {
        // Keep <style> blocks from <head>, take the content of <body> when present.
        var head = HeadStyleRegex().Matches(html).Select(m => m.Value);
        var bodyMatch = BodyRegex().Match(html);
        var content = bodyMatch.Success ? bodyMatch.Groups["c"].Value : HtmlShellRegex().Replace(HeadRegex().Replace(html, ""), "");
        return string.Concat(head) + content;
    }

    [GeneratedRegex("cid:(?<cid>[^\"'\\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRegex();
    [GeneratedRegex(@"(?<![=""'])\bhttps?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();
    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex MultiBlankRegex();
    [GeneratedRegex(@"<\s*/?\s*(iframe|frame|frameset|object|embed|applet|meta|link|base|form)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTagRegex();
    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();
    [GeneratedRegex(@"(?<attr>href|src|action)\s*=\s*(?<q>[""']?)\s*(?:javascript|vbscript):[^""'>]*\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex JsUrlRegex();
    [GeneratedRegex(@"<head\b[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();
    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadStyleRegex();
    [GeneratedRegex(@"<body\b[^>]*>(?<c>.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRegex();
    [GeneratedRegex(@"</?(html|head|body)\b[^>]*>|<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlShellRegex();
}
