using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MailClient.App.Services;

/// <summary>
/// Collects what the IT department needs to investigate a problem: recent logs, settings without secrets
/// (passwords are never in settings.json; certificates and signatures are removed as well) and system data.
/// </summary>
public static class SupportBundle
{
    public static void Create(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var log in Directory.GetFiles(AppPaths.Logs, "mailclient-*.log").OrderByDescending(File.GetLastWriteTime).Take(7))
        {
            // The log may be open for writing; copy through a shared read.
            using var src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = zip.CreateEntry("logs/" + Path.GetFileName(log)).Open();
            src.CopyTo(dst);
        }

        if (File.Exists(AppPaths.SettingsFile))
        {
            var node = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile));
            if (node?["Accounts"] is JsonArray accounts)
                foreach (var a in accounts.OfType<JsonObject>())
                {
                    if (a["TrustedRootCertificatesPem"] is JsonValue pem && pem.ToString().Length > 0)
                        a["TrustedRootCertificatesPem"] = "(задан)";
                    a.Remove("Signature");
                    a.Remove("SignatureHtml");
                }
            if (node is JsonObject root) root.Remove("TrustedSenders");
            Write(zip, "settings.json", node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "");
        }

        var info = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Программа: {AppInfo.Version}")
            .AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.VersionString}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}")
            .AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}")
            .AppendLine(CultureInfo.InvariantCulture, $"Пользователь: {Environment.UserDomainName}\\{Environment.UserName}, компьютер: {Environment.MachineName}")
            .AppendLine(CultureInfo.InvariantCulture, $"WebView2: {(Controls.WebViewHost.IsRuntimeInstalled() ? "установлен" : "НЕ установлен")}")
            .AppendLine(CultureInfo.InvariantCulture, $"Создано: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write(zip, "system.txt", info.ToString());
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
