using System.Globalization;
using System.Net;
using MailClient.Core.Models;
using MailClient.Core.Rendering;
using MailClient.Core.Services;
using MimeKit;
using MimeKit.Utils;

namespace MailClient.Core.Mime;

/// <summary>
/// MIME handling shared by the Exchange and IMAP back-ends. Like Thunderbird, messages are read as raw MIME
/// and parsed locally, and outgoing messages (including reply quotes, forwarded attachments and threading
/// headers) are composed locally and handed to the server as MIME.
/// </summary>
public static class MimeMail
{
    /// <summary>Separates the item id from the MIME part index inside attachment ids.</summary>
    public const char PartSeparator = '\u001E';

    /// <summary>MAPI PR_MESSAGE_FLAGS bits (MAPIDefS.h), as used by Thunderbird's create_message.rs.</summary>
    public const int MsgFlagRead = 0x1, MsgFlagUnmodified = 0x2, MsgFlagUnsent = 0x8;

    /// <summary>
    /// RFC 2047 file names: understood by Outlook and Russian corporate mail systems (RFC 2231 is not everywhere).
    /// </summary>
    public static readonly FormatOptions SendFormat = CreateFormat(hideBcc: false);

    /// <summary>Same as <see cref="SendFormat"/> but without the Bcc header (recipients must not see it).</summary>
    public static readonly FormatOptions TransportFormat = CreateFormat(hideBcc: true);

    private static FormatOptions CreateFormat(bool hideBcc)
    {
        var f = FormatOptions.Default.Clone();
        f.ParameterEncodingMethod = ParameterEncodingMethod.Rfc2047;
        if (hideBcc) f.HiddenHeaders.Add(HeaderId.Bcc);
        return f;
    }

    public static MimeMessage Parse(byte[] mime) => MimeMessage.Load(new MemoryStream(mime));

    public static byte[] ToBytes(MimeMessage message, FormatOptions format)
    {
        using var ms = new MemoryStream();
        message.WriteTo(format, ms);
        return ms.ToArray();
    }

    /// <summary>Attachment-like parts in a stable order (the index is part of the attachment id).</summary>
    public static List<MimeEntity> AttachmentParts(MimeMessage message) =>
        message.BodyParts.Where(p =>
            p is MessagePart ||
            p.IsAttachment ||
            (p is MimePart mp && !mp.ContentType.IsMimeType("text", "*") && (mp.ContentId != null || mp.FileName != null))).ToList();

    private static List<EmailAddress> Addresses(InternetAddressList list) =>
        list.Mailboxes.Select(m => new EmailAddress(m.Name ?? "", m.Address ?? "")).ToList();

    private static string PartName(MimeEntity part, int index) => part switch
    {
        MessagePart msgPart => (msgPart.Message?.Subject is { Length: > 0 } subj ? MessagePreviewSafe(subj) : "Вложенное письмо") + ".eml",
        MimePart mp => mp.FileName ?? (mp.ContentId != null ? $"image{index}.{mp.ContentType.MediaSubtype}" : $"attachment{index}"),
        _ => $"attachment{index}",
    };

    private static string MessagePreviewSafe(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    /// <summary>Fills recipients, body and the attachment list of <paramref name="m"/> from parsed MIME.</summary>
    public static void Fill(MailMessage m, MimeMessage mime, string itemId)
    {
        if (!string.IsNullOrEmpty(mime.Subject)) m.Subject = mime.Subject;
        m.To = Addresses(mime.To);
        m.Cc = Addresses(mime.Cc);
        m.Bcc = Addresses(mime.Bcc);
        m.ReplyTo = Addresses(mime.ReplyTo);
        if (mime.From.Mailboxes.FirstOrDefault() is { } from) m.From = new EmailAddress(from.Name ?? "", from.Address ?? "");
        if (mime.Sender is { } sender) m.Sender = new EmailAddress(sender.Name ?? "", sender.Address ?? "");
        if (m.DateSent == default && mime.Date != default) m.DateSent = mime.Date;
        if (m.DateReceived == default) m.DateReceived = m.DateSent;
        m.InternetMessageId = mime.MessageId is { Length: > 0 } mid ? $"<{mid}>" : "";
        m.IsReadReceiptRequested = mime.Headers.Contains(HeaderId.DispositionNotificationTo);
        m.BodyIsHtml = mime.HtmlBody != null;
        m.Body = mime.HtmlBody ?? mime.TextBody ?? "";
        if (m.Importance == Importance.Normal)
            m.Importance = mime.Importance switch { MessageImportance.High => Importance.High, MessageImportance.Low => Importance.Low, _ => Importance.Normal };

        m.Attachments.Clear();
        int index = 0;
        foreach (var part in AttachmentParts(mime))
        {
            m.Attachments.Add(new AttachmentInfo
            {
                Id = $"{itemId}{PartSeparator}{index}",
                Name = PartName(part, index),
                ContentType = part is MessagePart ? "message/rfc822" : part.ContentType.MimeType,
                ContentId = (part.ContentId ?? "").Trim('<', '>'),
                IsInline = !part.IsAttachment && part.ContentId != null,
                IsItemAttachment = part is MessagePart,
                Size = part is MimePart { Content: { } c } && c.Stream.CanSeek ? c.Stream.Length * 3 / 4 : 0,
            });
            index++;
        }
        m.HasAttachments = m.Attachments.Any(a => !a.IsInline);
    }

    public static bool IsPartId(string attachmentId) => attachmentId.Contains(PartSeparator);

    public static (string itemId, int index) SplitPartId(string attachmentId)
    {
        var i = attachmentId.LastIndexOf(PartSeparator);
        return (attachmentId[..i], int.Parse(attachmentId[(i + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>Decodes one attachment part of a parsed message.</summary>
    public static async Task<AttachmentContent> ExtractAsync(MimeMessage mime, string attachmentId, CancellationToken ct)
    {
        var (_, index) = SplitPartId(attachmentId);
        var parts = AttachmentParts(mime);
        if (index < 0 || index >= parts.Count) throw new MailServiceException("Вложение не найдено.", "ErrorAttachmentNotFound");
        var part = parts[index];
        using var ms = new MemoryStream();
        if (part is MessagePart mp) await mp.Message.WriteToAsync(ms, ct).ConfigureAwait(false);
        else if (part is MimePart file) await file.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);
        return new AttachmentContent
        {
            Info = new AttachmentInfo
            {
                Id = attachmentId,
                Name = PartName(part, index),
                ContentType = part is MessagePart ? "message/rfc822" : part.ContentType.MimeType,
                ContentId = (part.ContentId ?? "").Trim('<', '>'),
                IsInline = !part.IsAttachment && part.ContentId != null,
                IsItemAttachment = part is MessagePart,
                Size = ms.Length,
            },
            Content = ms.ToArray(),
        };
    }

    private static string QuoteHeader(MimeMessage o, string title) =>
        $"<p>{title}</p><p style=\"margin:0\"><b>От:</b> {WebUtility.HtmlEncode(o.From.ToString())}<br>" +
        $"<b>Отправлено:</b> {o.Date.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}<br>" +
        $"<b>Кому:</b> {WebUtility.HtmlEncode(o.To.ToString())}<br>" +
        (o.Cc.Count > 0 ? $"<b>Копия:</b> {WebUtility.HtmlEncode(o.Cc.ToString())}<br>" : "") +
        $"<b>Тема:</b> {WebUtility.HtmlEncode(o.Subject ?? "")}</p>";

    /// <summary>
    /// Composes an outgoing message as MIME. Replies quote the original and carry In-Reply-To/References;
    /// forwards include the original and its attachments — composed locally, as Thunderbird does.
    /// </summary>
    /// <param name="from">Author (account or shared mailbox).</param>
    /// <param name="loadOriginal">Loads the referenced original message (reply/forward).</param>
    public static async Task<MimeMessage> BuildAsync(OutgoingMessage message, MailboxAddress from,
        Func<string, CancellationToken, Task<MimeMessage>> loadOriginal, CancellationToken ct)
    {
        var m = new MimeMessage();
        m.From.Add(from);
        m.To.AddRange(message.To.Select(a => new MailboxAddress(a.Name, a.Address)));
        m.Cc.AddRange(message.Cc.Select(a => new MailboxAddress(a.Name, a.Address)));
        m.Bcc.AddRange(message.Bcc.Select(a => new MailboxAddress(a.Name, a.Address)));
        m.Subject = message.Subject;
        m.Date = DateTimeOffset.Now;
        var domain = from.Address.Contains('@') ? from.Address[(from.Address.IndexOf('@') + 1)..] : "localhost";
        m.MessageId = MimeUtils.GenerateMessageId(domain);
        if (message.Importance == Importance.High) { m.Importance = MessageImportance.High; m.XPriority = XMessagePriority.High; }
        if (message.Importance == Importance.Low) { m.Importance = MessageImportance.Low; m.XPriority = XMessagePriority.Low; }
        if (message.RequestReadReceipt) m.Headers[HeaderId.DispositionNotificationTo] = from.Address;

        var builder = new BodyBuilder();
        var html = message.BodyIsHtml ? message.Body : MessageHtmlBuilder.TextToHtml(message.Body);

        if (message.Action is ComposeAction.Reply or ComposeAction.ReplyAll or ComposeAction.Forward && message.ReferenceItemId != null)
        {
            var original = await loadOriginal(message.ReferenceItemId, ct).ConfigureAwait(false);
            var originalHtml = original.HtmlBody != null
                ? MessageHtmlBuilder.BodyFragment(MessageHtmlBuilder.StripDangerous(original.HtmlBody))
                : MessageHtmlBuilder.TextToHtml(original.TextBody ?? "");
            var forward = message.Action == ComposeAction.Forward;
            html = MessageHtmlBuilder.BodyFragment(html) + "<br>" +
                   QuoteHeader(original, forward ? "-------- Пересылаемое сообщение --------" : "-------- Исходное сообщение --------") +
                   (forward ? $"<div>{originalHtml}</div>" : $"<blockquote style=\"border-left:2px solid #8a8a8a;margin:0 0 0 4px;padding-left:10px\">{originalHtml}</blockquote>");

            // Keep inline images of the quoted original working.
            foreach (var part in original.BodyParts.OfType<MimePart>().Where(p => p.ContentId != null && !p.IsAttachment))
            {
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);
                var res = builder.LinkedResources.Add(part.FileName ?? "image", ms.ToArray(), part.ContentType);
                res.ContentId = part.ContentId;
            }

            if (forward)
            {
                foreach (var part in original.Attachments)
                {
                    using var ms = new MemoryStream();
                    if (part is MessagePart mp) await mp.Message.WriteToAsync(ms, ct).ConfigureAwait(false);
                    else if (part is MimePart file) await file.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);
                    var name = (part as MimePart)?.FileName ?? "message.eml";
                    builder.Attachments.Add(name, ms.ToArray(), part.ContentType);
                }
            }
            else if (!string.IsNullOrEmpty(original.MessageId))
            {
                // Threading headers so replies are grouped in every mail client.
                m.InReplyTo = original.MessageId;
                foreach (var r in original.References) m.References.Add(r);
                m.References.Add(original.MessageId);
            }
        }

        // A complete document that declares UTF-8: Exchange (and Outlook) otherwise guess the charset of an
        // HTML body from the server's code page, and Cyrillic text turns into mojibake in Sent Items.
        builder.HtmlBody = Utf8HtmlDocument(html);
        builder.TextBody = MessageHtmlBuilder.HtmlToText(html);
        foreach (var a in message.Attachments)
        {
            var type = ContentType.TryParse(a.ContentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            if (a.IsInline && !string.IsNullOrEmpty(a.ContentId))
            {
                var res = builder.LinkedResources.Add(a.Name, a.Content, type);
                res.ContentId = a.ContentId;
            }
            else
            {
                builder.Attachments.Add(a.Name, a.Content, type);
            }
        }
        m.Body = builder.ToMessageBody();
        foreach (var text in m.BodyParts.OfType<TextPart>().Where(t => !t.IsAttachment))
            text.ContentType.Charset = "utf-8";
        // 7-bit transfer encodings (quoted-printable/base64) instead of raw 8-bit UTF-8: Thunderbird does the
        // same, and Exchange's MIME conversion and older SMTP relays mangle 8-bit bodies without 8BITMIME.
        m.Prepare(EncodingConstraint.SevenBit);
        return m;
    }

    internal static string Utf8HtmlDocument(string html) =>
        "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"></head><body>" +
        MessageHtmlBuilder.BodyFragment(html) + "</body></html>";
}
