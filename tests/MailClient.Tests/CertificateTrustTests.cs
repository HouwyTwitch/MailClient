using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailClient.Exchange.Http;
using Xunit;

namespace MailClient.Tests;

public class CertificateTrustTests
{
    private static (X509Certificate2 root, X509Certificate2 leaf) CreateChain(string host)
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Test Corporate Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest($"CN={host}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        leafReq.CertificateExtensions.Add(san.Build());
        var leaf = leafReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 1, 2, 3, 4 });
        return (root, leaf);
    }

    [Fact]
    public void Leaf_signed_by_imported_root_is_accepted()
    {
        var (root, leaf) = CreateChain("mail.contoso.ru");
        var roots = ExchangeHttp.ParseCertificates(root.ExportCertificatePem());
        Assert.True(ExchangeHttp.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors, roots, null));
    }

    [Fact]
    public void Name_mismatch_is_rejected_even_with_imported_root()
    {
        var (root, leaf) = CreateChain("mail.contoso.ru");
        var roots = ExchangeHttp.ParseCertificates(root.ExportCertificatePem());
        Assert.False(ExchangeHttp.ValidateServerCertificate(leaf,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, roots, null));
    }

    [Fact]
    public void Unrelated_root_is_rejected()
    {
        var (_, leaf) = CreateChain("mail.contoso.ru");
        var (otherRoot, _) = CreateChain("other");
        var roots = ExchangeHttp.ParseCertificates(otherRoot.ExportCertificatePem());
        Assert.False(ExchangeHttp.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors, roots, null));
    }

    [Fact]
    public void Pinned_thumbprint_is_accepted()
    {
        var (_, leaf) = CreateChain("mail.contoso.ru");
        Assert.True(ExchangeHttp.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors,
            Array.Empty<X509Certificate2>(), leaf.Thumbprint));
    }
}
