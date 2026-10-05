using System.Text;
using MailClient.Core.Mime;
using MailClient.Core.Models;
using MimeKit;
using Xunit;

namespace MailClient.Tests;

public class MimeEncodingTests
{
    // For the test's own Encoding.GetEncoding(1251/20866) calls; MimeMail registers the provider itself.
    static MimeEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Task<MimeMessage> Build(OutgoingMessage m) =>
        MimeMail.BuildAsync(m, new MailboxAddress("Иванов Иван", "ivanov@test.ru"),
            (_, _) => throw new InvalidOperationException(), CancellationToken.None);

    [Fact]
    public async Task Russian_body_is_sent_as_7bit_utf8_and_round_trips()
    {
        var mime = await Build(new OutgoingMessage
        {
            To = { new EmailAddress("Петров", "petrov@test.ru") },
            Subject = "Отчёт за квартал",
            Body = "<p>Добрый день! Высылаю отчёт.</p>",
            Attachments = { new OutgoingAttachment { Name = "Отчёт.txt", ContentType = "text/plain", Content = Encoding.UTF8.GetBytes("Привет") } },
        });

        var bytes = MimeMail.ToBytes(mime, MimeMail.SendFormat);
        // Every byte is ASCII: nothing depends on the server guessing a code page.
        Assert.All(bytes, b => Assert.True(b < 0x80));

        foreach (var text in mime.BodyParts.OfType<TextPart>().Where(t => !t.IsAttachment))
        {
            Assert.Equal("utf-8", text.ContentType.Charset, ignoreCase: true);
            Assert.NotEqual(ContentEncoding.EightBit, text.ContentTransferEncoding);
            Assert.NotEqual(ContentEncoding.Default, text.ContentTransferEncoding);
        }
        Assert.Contains("charset=utf-8", mime.HtmlBody);

        var parsed = MimeMail.Parse(bytes);
        Assert.Equal("Отчёт за квартал", parsed.Subject);
        Assert.Contains("Добрый день! Высылаю отчёт.", parsed.HtmlBody);
        Assert.Contains("Добрый день!", parsed.TextBody);
        Assert.Equal("Отчёт.txt", parsed.Attachments.OfType<MimePart>().Single().FileName);
        Assert.Equal("Петров", parsed.To.Mailboxes.Single().Name);
    }

    [Fact]
    public async Task Plain_text_body_is_wrapped_in_utf8_document()
    {
        var mime = await Build(new OutgoingMessage { Subject = "Тест", Body = "Строка 1\nСтрока 2", BodyIsHtml = false });
        Assert.StartsWith("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">", mime.HtmlBody);
        Assert.Contains("Строка 1", mime.HtmlBody);
    }

    private static byte[] Raw(string headers, byte[] body) =>
        Encoding.ASCII.GetBytes("From: a@test.ru\r\nTo: b@test.ru\r\nSubject: x\r\nMIME-Version: 1.0\r\n" + headers + "\r\n\r\n")
            .Concat(body).ToArray();

    [Theory]
    [InlineData("Content-Type: text/plain", 1251)]                       // no charset: Russian default
    [InlineData("Content-Type: text/plain; charset=us-ascii", 1251)]     // mislabelled 8-bit
    [InlineData("Content-Type: text/plain; charset=koi8-r", 20866)]      // declared and honoured
    [InlineData("Content-Type: text/plain; charset=utf-8", 65001)]
    [InlineData("Content-Type: text/plain", 65001)]                      // undeclared but valid UTF-8
    public void Incoming_text_charset_detection(string contentType, int codePage)
    {
        var text = "Привет, коллеги! Съешь ещё этих мягких булок.";
        var mime = MimeMail.Parse(Raw(contentType + "\r\nContent-Transfer-Encoding: 8bit", Encoding.GetEncoding(codePage).GetBytes(text)));
        var m = new MailMessage();
        MimeMail.Fill(m, mime, "id");
        Assert.Equal(text, m.Body.TrimEnd());
    }

    [Fact]
    public void Undeclared_html_uses_meta_charset()
    {
        var html = "<html><head><meta charset=\"koi8-r\"></head><body>Добрый день</body></html>";
        var mime = MimeMail.Parse(Raw("Content-Type: text/html\r\nContent-Transfer-Encoding: 8bit", Encoding.GetEncoding(20866).GetBytes(html)));
        Assert.Contains("Добрый день", MimeMail.HtmlBodyOf(mime));
    }

    [Fact]
    public void Raw_8bit_subject_in_windows_1251_is_readable()
    {
        var bytes = Encoding.ASCII.GetBytes("From: a@test.ru\r\nSubject: ").Concat(Encoding.GetEncoding(1251).GetBytes("Счёт на оплату"))
            .Concat(Encoding.ASCII.GetBytes("\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nok")).ToArray();
        Assert.Equal("Счёт на оплату", MimeMail.Parse(bytes).Subject);
    }
}
