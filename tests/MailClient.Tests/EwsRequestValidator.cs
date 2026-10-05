using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MailClient.Tests;

/// <summary>
/// Checks EWS requests against EwsRequestRules.txt: child order, required children and attributes, and
/// allowed values. Exchange rejects a request whose elements are out of protocol order (ErrorSchemaValidation),
/// so this is what the tests use to make sure every request the client builds is well-formed.
/// </summary>
internal sealed partial class EwsRequestValidator
{
    private const string TypesNs = "http://schemas.microsoft.com/exchange/services/2006/types";
    private const string MessagesNs = "http://schemas.microsoft.com/exchange/services/2006/messages";

    private sealed record Position(string[] Names, bool Required, bool Repeatable);

    private sealed class Rule
    {
        public List<Position>? Children;   // null for a leaf
        public string? Content;            // leaf content: text, boolean, … or "v1 | v2"
        public Dictionary<string, (bool Required, string Content)> Attributes { get; } = new();
    }

    private readonly Dictionary<string, Rule> _rules = new(StringComparer.Ordinal);

    public static EwsRequestValidator Load() =>
        new(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "EwsRequestRules.txt")));

    public EwsRequestValidator(IEnumerable<string> lines)
    {
        Rule? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("  @", StringComparison.Ordinal))
            {
                var (name, content) = Split(line.Trim()[1..], ':');
                bool required = name.EndsWith('!');
                current!.Attributes[name.TrimEnd('!')] = (required, content);
                continue;
            }
            current = new Rule();
            if (line.Contains(" = ", StringComparison.Ordinal) || line.EndsWith(" =", StringComparison.Ordinal))
            {
                var (name, model) = Split(line, '=');
                current.Children = ParseModel(model);
                _rules.Add(name, current);
            }
            else
            {
                var (name, content) = Split(line, ':');
                current.Content = content;
                _rules.Add(name, current);
            }
        }
    }

    private static (string, string) Split(string line, char separator)
    {
        int i = line.IndexOf(" " + separator, StringComparison.Ordinal);
        return (line[..i].Trim(), line[(i + 2)..].Trim());
    }

    private static List<Position> ParseModel(string model)
    {
        var positions = new List<Position>();
        int i = 0;
        while (i < model.Length)
        {
            if (model[i] == ' ') { i++; continue; }
            string[] names;
            if (model[i] == '(')
            {
                int close = model.IndexOf(')', i);
                names = model[(i + 1)..close].Split('|', StringSplitOptions.TrimEntries);
                i = close + 1;
            }
            else
            {
                int end = i;
                while (end < model.Length && model[end] is not (' ' or '!' or '*')) end++;
                names = [model[i..end]];
                i = end;
            }
            bool required = false, repeatable = false;
            while (i < model.Length && model[i] is '!' or '*')
            {
                if (model[i] == '!') required = true; else repeatable = true;
                i++;
            }
            positions.Add(new Position(names, required, repeatable));
        }
        return positions;
    }

    private static string Prefixed(XName name) =>
        (name.NamespaceName == TypesNs ? "t:" : name.NamespaceName == MessagesNs ? "m:" : name.NamespaceName + ":") + name.LocalName;

    /// <summary>Returns the problems found in <paramref name="element"/> and everything below it.</summary>
    public List<string> Validate(XElement element)
    {
        var errors = new List<string>();
        Check(element, null, errors);
        return errors;
    }

    private void Check(XElement e, string? parent, List<string> errors)
    {
        var name = Prefixed(e.Name);
        var path = parent == null ? name : $"{parent}/{name}";
        if (!_rules.TryGetValue(parent + "/" + name, out var rule) && !_rules.TryGetValue(name, out rule))
        {
            errors.Add($"{path}: элемент не описан в EwsRequestRules.txt");
            return;
        }

        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
        {
            if (!rule.Attributes.TryGetValue(a.Name.LocalName, out var spec))
                errors.Add($"{path}: атрибут {a.Name.LocalName} не описан");
            else if (!ValueMatches(spec.Content, a.Value))
                errors.Add($"{path}: недопустимое значение {a.Name.LocalName}=\"{a.Value}\" (ожидается {spec.Content})");
        }
        foreach (var (attr, spec) in rule.Attributes)
            if (spec.Required && e.Attribute(attr) == null)
                errors.Add($"{path}: нет обязательного атрибута {attr}");

        if (rule.Children == null)
        {
            if (e.HasElements) errors.Add($"{path}: элемент не может содержать вложенные элементы");
            else if (!ValueMatches(rule.Content!, e.Value)) errors.Add($"{path}: недопустимое значение \"{e.Value}\" (ожидается {rule.Content})");
            return;
        }

        if (e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            errors.Add($"{path}: текст внутри элемента со вложенными элементами");
        int last = -1;
        var seen = new HashSet<int>();
        foreach (var child in e.Elements())
        {
            var childName = Prefixed(child.Name);
            int index = rule.Children.FindIndex(p => p.Names.Contains(childName));
            if (index < 0)
                errors.Add($"{path}: недопустимый вложенный элемент {childName}");
            else if (index < last)
                errors.Add($"{path}: {childName} стоит не на своём месте (порядок: {string.Join(" ", rule.Children.Select(p => string.Join("|", p.Names)))})");
            else if (index == last && !rule.Children[index].Repeatable)
                errors.Add($"{path}: {childName} повторяется");
            else
            {
                last = index;
                seen.Add(index);
            }
            Check(child, name, errors);
        }
        for (int i = 0; i < rule.Children.Count; i++)
            if (rule.Children[i].Required && !seen.Contains(i))
                errors.Add($"{path}: нет обязательного элемента {string.Join(" или ", rule.Children[i].Names)}");
    }

    private static bool ValueMatches(string content, string value) => content switch
    {
        "text" => true,
        "boolean" => value is "true" or "false" or "1" or "0",
        "integer" => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        "datetime" => IsDateTime(value),
        "base64" => Convert.TryFromBase64String(value, new byte[value.Length], out _),
        "proptag" => PropertyTag().IsMatch(value),
        _ => content.Split('|', StringSplitOptions.TrimEntries).Contains(value, StringComparer.Ordinal),
    };

    /// <summary>MAPI property tag: hexadecimal (0x1090) or decimal.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^(0x[0-9A-Fa-f]{1,4}|[0-9]{1,5})$")]
    private static partial System.Text.RegularExpressions.Regex PropertyTag();

    private static bool IsDateTime(string value)
    {
        try
        {
            XmlConvert.ToDateTimeOffset(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
