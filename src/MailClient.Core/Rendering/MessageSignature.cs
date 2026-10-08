using System.Text;
using MailClient.Core.Models;

namespace MailClient.Core.Rendering;

/// <summary>
/// The account's signature in a message being written: where it goes, when it is added, and a ready-made signature
/// built from the user's address book entry. In the editor the signature is one element with a known id, so it
/// can be replaced when another sender account is chosen.
/// </summary>
public static class MessageSignature
{
    /// <summary>Id of the element that holds the signature in the message text.</summary>
    public const string ElementId = "mc-signature";

    /// <summary>The account's signature as HTML (a plain text signature of earlier versions is converted).</summary>
    public static string Html(AccountSettings account) =>
        !string.IsNullOrWhiteSpace(account.SignatureHtml) ? account.SignatureHtml
        : string.IsNullOrWhiteSpace(account.Signature) ? ""
        : FromText(account.Signature);

    /// <summary>The signature to add to a new message of this kind, or "" when the settings say none.</summary>
    public static string For(AccountSettings account, ComposeAction action) => action switch
    {
        ComposeAction.New when account.SignatureOnNew => Html(account),
        ComposeAction.Reply or ComposeAction.ReplyAll or ComposeAction.Forward when account.SignatureOnReply => Html(account),
        _ => "",
    };

    /// <summary>
    /// Starting text of a new message: an empty line to type in, then (after one more empty line) the signature.
    /// The quoted original of a reply follows below it when the message is sent.
    /// </summary>
    public static string InitialBody(string signatureHtml) =>
        "<p><br></p>" + (string.IsNullOrWhiteSpace(signatureHtml) ? "" : $"<p><br></p><div id=\"{ElementId}\">{signatureHtml}</div>");

    /// <summary>Escapes markup characters only, so the HTML stays readable (« », № as they are).</summary>
    private static string Encode(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Plain text (one line per line) as HTML lines without paragraph spacing.</summary>
    public static string FromText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
        return string.Join("", lines.Select(l => l.Trim().Length == 0 ? "<div><br></div>" : $"<div>{Encode(l.TrimEnd())}</div>"));
    }

    /// <summary>
    /// A business signature from the user's own address book entry: closing, name, position, company, phones and
    /// e-mail, each on its own line; empty fields are left out.
    /// </summary>
    public static string FromContact(Contact contact, string email)
    {
        static string Enc(string s) => Encode(s.Trim());
        var sb = new StringBuilder();
        void Line(string html, string style = "") =>
            sb.Append(style.Length == 0 ? $"<div>{html}</div>" : $"<div style=\"{style}\">{html}</div>");

        Line("С уважением,");
        var name = !string.IsNullOrWhiteSpace(contact.DisplayName) ? contact.DisplayName : $"{contact.GivenName} {contact.Surname}";
        if (!string.IsNullOrWhiteSpace(name)) Line($"<b>{Enc(name)}</b>");
        var position = string.Join(", ", new[] { contact.JobTitle, contact.Department }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Enc));
        if (position.Length > 0) Line(position);
        if (!string.IsNullOrWhiteSpace(contact.CompanyName)) Line(Enc(contact.CompanyName));
        var phones = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact.BusinessPhone)) phones.Add($"тел.: {Enc(contact.BusinessPhone)}");
        if (!string.IsNullOrWhiteSpace(contact.MobilePhone)) phones.Add($"моб.: {Enc(contact.MobilePhone)}");
        if (phones.Count > 0) Line(string.Join(", ", phones));
        var address = !string.IsNullOrWhiteSpace(contact.PrimaryEmail) ? contact.PrimaryEmail : email;
        if (!string.IsNullOrWhiteSpace(address))
            Line($"<a href=\"mailto:{Enc(address)}\">{Enc(address)}</a>");
        return sb.ToString();
    }
}
