using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MailClient.App.Services;

public static class CertificateImport
{
    /// <summary>Converts a DER (.cer) or PEM (.crt/.pem) file content into PEM text.</summary>
    public static string ToPem(byte[] fileContent)
    {
        var text = Encoding.ASCII.GetString(fileContent);
        if (text.Contains("-----BEGIN CERTIFICATE-----")) return text.Trim();
        using var cert = new X509Certificate2(fileContent);
        return cert.ExportCertificatePem();
    }

    public static string Describe(string pem)
    {
        try
        {
            var certs = MailClient.Exchange.Http.ExchangeHttp.ParseCertificates(pem);
            return string.Join("; ", certs.Select(c => $"{c.GetNameInfo(X509NameType.SimpleName, false)} (до {c.NotAfter:dd.MM.yyyy})"));
        }
        catch
        {
            return "Некорректный сертификат";
        }
    }
}
