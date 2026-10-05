using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailClient.Core.Models;

namespace MailClient.Core.Security;

/// <summary>
/// Server certificate validation shared by all protocols (EWS, IMAP, SMTP): Windows trust store,
/// optional organization root CA (e.g. "Russian Trusted Root CA") and optional pinned certificate.
/// </summary>
public static class CertificateTrust
{
    /// <summary>Returns a validation callback for the account, or null when default validation suffices.</summary>
    public static RemoteCertificateValidationCallback? CreateCallback(AccountSettings account)
    {
        var roots = ParseCertificates(account.TrustedRootCertificatesPem);
        var pinned = string.IsNullOrWhiteSpace(account.TrustedCertificateThumbprint) ? null : Normalize(account.TrustedCertificateThumbprint);
        if (roots.Count == 0 && pinned == null) return null;
        return (_, cert, _, errors) => Validate(cert, errors, roots, pinned);
    }

    /// <summary>
    /// Accepts a certificate that Windows trusts, one that chains to a user-supplied root CA
    /// (host name must still match), or the exact pinned certificate.
    /// </summary>
    public static bool Validate(X509Certificate? cert, SslPolicyErrors errors,
        IReadOnlyCollection<X509Certificate2> customRoots, string? pinnedThumbprint)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert == null) return false;
        using var leaf = new X509Certificate2(cert);

        if (pinnedThumbprint != null &&
            (Normalize(Convert.ToHexString(SHA256.HashData(leaf.RawData))) == pinnedThumbprint ||
             Normalize(leaf.Thumbprint) == pinnedThumbprint))
            return true;

        if (customRoots.Count == 0 || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                                   || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var root in customRoots) chain.ChainPolicy.CustomTrustStore.Add(root);
        return chain.Build(leaf);
    }

    /// <summary>Parses one or more PEM certificates (or a single Base64 DER blob).</summary>
    public static List<X509Certificate2> ParseCertificates(string? pem)
    {
        var result = new List<X509Certificate2>();
        if (string.IsNullOrWhiteSpace(pem)) return result;
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        int pos = 0;
        while ((pos = pem.IndexOf(begin, pos, StringComparison.Ordinal)) >= 0)
        {
            int stop = pem.IndexOf(end, pos, StringComparison.Ordinal);
            if (stop < 0) break;
            var b64 = pem[(pos + begin.Length)..stop];
            result.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(new string(b64.Where(c => !char.IsWhiteSpace(c)).ToArray()))));
            pos = stop + end.Length;
        }
        if (result.Count == 0)
            result.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(new string(pem.Where(c => !char.IsWhiteSpace(c)).ToArray()))));
        return result;
    }

    private static string Normalize(string s) =>
        new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}
