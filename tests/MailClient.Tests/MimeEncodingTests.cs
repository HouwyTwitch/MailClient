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

    [Theory]
    [InlineData(20866, "")]                                  // KOI8-R, no label
    [InlineData(20866, "; charset=windows-1251")]            // KOI8-R mislabelled
    [InlineData(866, "; charset=us-ascii")]                  // DOS code page from old systems
    [InlineData(1251, "; charset=iso-8859-1")]               // windows-1251 labelled Latin-1
    [InlineData(28595, "")]
    public void Raw_russian_headers_and_body_in_any_charset_are_detected(int codePage, string label)
    {
        var enc = Encoding.GetEncoding(codePage);
        var bytes = Encoding.ASCII.GetBytes("From: ").Concat(enc.GetBytes("Иванов Иван")).Concat(Encoding.ASCII.GetBytes(" <a@t.ru>\r\nSubject: "))
            .Concat(enc.GetBytes("Счёт на оплату")).Concat(Encoding.ASCII.GetBytes($"\r\nContent-Type: text/plain{label}\r\nContent-Transfer-Encoding: 8bit\r\n\r\n"))
            .Concat(enc.GetBytes("Добрый день! Высылаем документы по договору.")).ToArray();
        var m = new MailMessage();
        MimeMail.Fill(m, MimeMail.Parse(bytes), "x");
        Assert.Equal("Иванов Иван", m.From!.Name);
        Assert.Equal("Счёт на оплату", m.Subject);
        Assert.Equal("Добрый день! Высылаем документы по договору.", m.Body.Trim());
    }

    [Fact]
    public void Genuine_latin1_text_is_not_turned_into_cyrillic()
    {
        var bytes = Encoding.ASCII.GetBytes("Subject: x\r\nContent-Type: text/plain; charset=iso-8859-1\r\nContent-Transfer-Encoding: 8bit\r\n\r\n")
            .Concat(Encoding.Latin1.GetBytes("Grüße aus München, schöne Größe")).ToArray();
        var m = new MailMessage();
        MimeMail.Fill(m, MimeMail.Parse(bytes), "x");
        Assert.Equal("Grüße aus München, schöne Größe", m.Body.Trim());
    }
}
