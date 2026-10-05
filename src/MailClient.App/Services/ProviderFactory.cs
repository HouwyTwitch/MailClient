using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Ews;
using MailClient.Imap;

namespace MailClient.App.Services;

public static class ProviderFactory
{
    public static IMailProvider Create(AccountSettings account, ICredentialProvider credentials) => account.Protocol switch
    {
        MailProtocol.Imap => new ImapProvider(account, credentials),
        _ => new ExchangeProvider(account, credentials),
    };
}

/// <summary>Known IMAP/SMTP settings of popular Russian mail services.</summary>
public static class MailPresets
{
    public sealed record Preset(string ImapHost, int ImapPort, ConnectionSecurity ImapSecurity, string SmtpHost, int SmtpPort, ConnectionSecurity SmtpSecurity);

    public static readonly IReadOnlyDictionary<string, Preset> ByKey = new Dictionary<string, Preset>
    {
        ["yandex"] = new("imap.yandex.ru", 993, ConnectionSecurity.SslOnConnect, "smtp.yandex.ru", 465, ConnectionSecurity.SslOnConnect),
        ["mailru"] = new("imap.mail.ru", 993, ConnectionSecurity.SslOnConnect, "smtp.mail.ru", 465, ConnectionSecurity.SslOnConnect),
        ["rambler"] = new("imap.rambler.ru", 993, ConnectionSecurity.SslOnConnect, "smtp.rambler.ru", 465, ConnectionSecurity.SslOnConnect),
    };

    private static readonly Dictionary<string, string> DomainToKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yandex.ru"] = "yandex", ["ya.ru"] = "yandex", ["yandex.com"] = "yandex", ["yandex.by"] = "yandex", ["yandex.kz"] = "yandex", ["narod.ru"] = "yandex",
        ["mail.ru"] = "mailru", ["bk.ru"] = "mailru", ["inbox.ru"] = "mailru", ["list.ru"] = "mailru", ["internet.ru"] = "mailru", ["vk.com"] = "mailru",
        ["rambler.ru"] = "rambler", ["lenta.ru"] = "rambler", ["autorambler.ru"] = "rambler", ["myrambler.ru"] = "rambler", ["ro.ru"] = "rambler",
    };

    /// <summary>Settings for an address: a known provider, or the conventional imap./smtp. hosts of the domain.</summary>
    public static (string? key, Preset preset) Guess(string email)
    {
        var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..].Trim() : "";
        if (DomainToKey.TryGetValue(domain, out var key)) return (key, ByKey[key]);
        return (null, new Preset($"imap.{domain}", 993, ConnectionSecurity.SslOnConnect, $"smtp.{domain}", 465, ConnectionSecurity.SslOnConnect));
    }

    /// <summary>Exchange with IMAP enabled: typically mail.domain, IMAP 993 SSL and authenticated SMTP 587 STARTTLS.</summary>
    public static Preset ExchangeImap(string email)
    {
        var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..].Trim() : "";
        return new($"mail.{domain}", 993, ConnectionSecurity.SslOnConnect, $"mail.{domain}", 587, ConnectionSecurity.StartTls);
    }
}
