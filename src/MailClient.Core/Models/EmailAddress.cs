namespace MailClient.Core.Models;

public sealed record EmailAddress(string Name, string Address, string RoutingType = "SMTP")
{
    public string DisplayText => string.IsNullOrWhiteSpace(Name) || Name == Address
        ? Address
        : string.IsNullOrWhiteSpace(Address) ? Name : $"{Name} <{Address}>";

    public string ShortName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public override string ToString() => DisplayText;

    /// <summary>
    /// Parses a list like <c>"Jane Doe &lt;jane@contoso.com&gt;; bob@contoso.com, "Smith, Al" &lt;al@x.com&gt;"</c>.
    /// </summary>
    public static IReadOnlyList<EmailAddress> ParseList(string? text)
    {
        var result = new List<EmailAddress>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new System.Text.StringBuilder();
        bool inQuotes = false, inAngle = false;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == '<' && !inQuotes) inAngle = true;
            else if (ch == '>' && !inQuotes) inAngle = false;

            if ((ch == ';' || ch == ',') && !inQuotes && !inAngle)
            {
                AddParsed(current.ToString(), result);
                current.Clear();
            }
            else current.Append(ch);
        }
        AddParsed(current.ToString(), result);
        return result;
    }

    public static EmailAddress? Parse(string? text)
    {
        var list = ParseList(text);
        return list.Count > 0 ? list[0] : null;
    }

    private static void AddParsed(string token, List<EmailAddress> into)
    {
        token = token.Trim();
        if (token.Length == 0) return;
        int lt = token.LastIndexOf('<');
        int gt = token.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            var addr = token[(lt + 1)..gt].Trim();
            var name = token[..lt].Trim().Trim('"').Trim();
            into.Add(new EmailAddress(name, addr));
        }
        else
        {
            into.Add(new EmailAddress("", token.Trim('"')));
        }
    }

    public static string FormatList(IEnumerable<EmailAddress> list) =>
        string.Join("; ", list.Select(a => a.Name.Contains(',') ? $"\"{a.Name}\" <{a.Address}>" : a.DisplayText));

    public static bool LooksValid(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        int at = address.IndexOf('@');
        return at > 0 && at < address.Length - 1 && address.IndexOf('@', at + 1) < 0 && !address.Any(char.IsWhiteSpace);
    }
}
