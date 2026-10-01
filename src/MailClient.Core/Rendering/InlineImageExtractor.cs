using System.Text.RegularExpressions;
using MailClient.Core.Models;

namespace MailClient.Core.Rendering;

/// <summary>
/// Converts images embedded as data: URIs (e.g. pasted screenshots) into inline MIME parts referenced by
/// cid:, which is what Outlook and other clients expect.
/// </summary>
public static partial class InlineImageExtractor
{
    public static (string html, List<OutgoingAttachment> images) Extract(string html)
    {
        var images = new List<OutgoingAttachment>();
        int n = 0;
        var result = DataImageRegex().Replace(html, m =>
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(m.Groups["data"].Value);
            }
            catch (FormatException)
            {
                return m.Value;
            }
            var type = m.Groups["type"].Value.ToLowerInvariant();
            var ext = type switch { "jpeg" or "jpg" => "jpg", "gif" => "gif", "bmp" => "bmp", "webp" => "webp", _ => "png" };
            var cid = $"image{++n:000}.{Guid.NewGuid():N}@mailclient";
            images.Add(new OutgoingAttachment
            {
                Name = $"image{n:000}.{ext}",
                ContentType = $"image/{(ext == "jpg" ? "jpeg" : ext)}",
                Content = data,
                IsInline = true,
                ContentId = cid,
            });
            return $"{m.Groups["prefix"].Value}{m.Groups["q"].Value}cid:{cid}{m.Groups["q"].Value}";
        });
        return (result, images);
    }

    [GeneratedRegex(@"(?<prefix>src\s*=\s*)(?<q>[""'])data:image/(?<type>[a-z0-9.+-]+);base64,(?<data>[A-Za-z0-9+/=\s]+)\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex DataImageRegex();
}
