using System.Text;
using MailClient.Core.Mime;
using MailClient.Core.Models;
using MimeKit;
using Xunit;

namespace MailClient.Tests;

public class MimeEncodingTests
{
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
}
