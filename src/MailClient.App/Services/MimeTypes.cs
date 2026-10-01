using System.IO;

namespace MailClient.App.Services;

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".xml"] = "application/xml",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".svg"] = "image/svg+xml",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".eml"] = "message/rfc822",
        [".ics"] = "text/calendar",
        [".vcf"] = "text/vcard",
        // Russian e-signature formats (КриптоПро / ГОСТ).
        [".sig"] = "application/pkcs7-signature",
        [".sgn"] = "application/pkcs7-signature",
        [".p7s"] = "application/pkcs7-signature",
        [".p7m"] = "application/pkcs7-mime",
        [".cer"] = "application/pkix-cert",
    };

    public static string FromFileName(string name) =>
        Map.TryGetValue(Path.GetExtension(name), out var t) ? t : "application/octet-stream";
}
