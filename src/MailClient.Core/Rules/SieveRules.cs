using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailClient.Core.Models;

namespace MailClient.Core.Rules;

/// <summary>
/// Forwarding rules as a block of a Sieve script (RFC 5228), for IMAP servers with ManageSieve (Dovecot,
/// Cyrus, Stalwart…). The block sits between two marker lines; every rule is stored as a JSON comment followed
/// by the generated code, so the editor reads back exactly what it wrote and leaves the rest of the script —
/// filters made elsewhere — untouched.
/// </summary>
public static partial class SieveRules
{
    public const string BeginMarker = "# MailClient rules begin (managed by the mail client, do not edit)";
    public const string EndMarker = "# MailClient rules end";
    private const string RulePrefix = "# rule ";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };

    /// <summary>Sieve extensions the server announced (ManageSieve "SIEVE" capability).</summary>
    public sealed record Extensions(IReadOnlySet<string> Names)
    {
        public bool Has(string name) => Names.Contains(name);
        public static Extensions Parse(string? capability) =>
            new(new HashSet<string>((capability ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The rules stored in a script's managed block (none when there is no block).</summary>
    public static List<ForwardingRule> Parse(string? script)
    {
        var rules = new List<ForwardingRule>();
        if (Block(script) is not { } block) return rules;
        foreach (var line in script![block.Start..block.End].Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (!text.StartsWith(RulePrefix, StringComparison.Ordinal)) continue;
            try
            {
                if (JsonSerializer.Deserialize<StoredRule>(text[RulePrefix.Length..], Json) is { } stored) rules.Add(stored.ToRule());
            }
            catch (JsonException)
            {
                // A hand-edited line: skip it rather than lose the other rules.
            }
        }
        return rules;
    }

    /// <summary>True when the script holds filters besides the managed block.</summary>
    public static bool HasOtherContent(string? script)
    {
        var rest = Remove(script ?? "");
        return CommentRegex().Replace(rest, "").Split('\n').Any(l => l.Trim().Length > 0);
    }

    /// <summary>
    /// The script with the managed block replaced by <paramref name="rules"/> (or removed when there are none).
    /// The block goes right after the script's leading <c>require</c> commands, which Sieve only allows at the top.
    /// </summary>
    public static string Apply(string? script, IReadOnlyList<ForwardingRule> rules, Extensions extensions)
    {
        var rest = Remove(script ?? "");
        if (rules.Count == 0) return rest;
        var at = HeaderEnd(rest);
        var block = Build(rules, extensions);
        var before = rest[..at];
        var after = rest[at..];
        if (before.Length > 0 && !before.EndsWith('\n')) before += "\n";
        // The block ends with a line break, so removing it later gives back the script exactly as it was.
        return before + block + after;
    }

    /// <summary>The managed block: marker, requirements, one commented and generated section per rule, marker.</summary>
    public static string Build(IReadOnlyList<ForwardingRule> rules, Extensions extensions)
    {
        var active = rules.Where(r => r.IsEnabled && r.Recipients.Count > 0).ToList();
        bool copy = extensions.Has("copy");
        bool unicode = extensions.Has("comparator-i;unicode-casemap");
        var require = new List<string>();
        if (copy && active.Any(r => r.KeepCopy)) require.Add("copy");
        if (active.Any(r => r.BodyContains.Count > 0)) require.Add("body");
        if (unicode && active.Any(r => r.SubjectContains.Count > 0 || r.BodyContains.Count > 0)) require.Add("comparator-i;unicode-casemap");

        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append('\n');
        if (require.Count > 0) sb.Append("require ").Append(StringList(require)).Append(";\n");
        foreach (var rule in rules)
        {
            sb.Append(RulePrefix).Append(JsonSerializer.Serialize(StoredRule.Of(rule), Json)).Append('\n');
            if (!rule.IsEnabled || rule.Recipients.Count == 0) continue;

            var tests = new List<string>();
            if (rule.FromAddresses.Count > 0) tests.Add("address :is \"from\" " + StringList(rule.FromAddresses));
            var comparator = unicode ? ":comparator \"i;unicode-casemap\" " : "";
            if (rule.SubjectContains.Count > 0) tests.Add($"header {comparator}:contains \"subject\" " + StringList(rule.SubjectContains));
            if (rule.BodyContains.Count > 0) tests.Add($"body {comparator}:text :contains " + StringList(rule.BodyContains));

            var actions = new List<string>();
            foreach (var r in rule.Recipients)
                actions.Add(rule.KeepCopy && copy ? $"redirect :copy {Quote(r.Address)};" : $"redirect {Quote(r.Address)};");
            // Without the "copy" extension a redirect cancels the implicit keep: keep explicitly.
            if (rule.KeepCopy && !copy) actions.Add("keep;");
            if (rule.StopProcessing) actions.Add("stop;");

            if (tests.Count == 0)
            {
                foreach (var a in actions) sb.Append(a).Append('\n');
                continue;
            }
            sb.Append("if ").Append(tests.Count == 1 ? tests[0] : "allof (" + string.Join(", ", tests) + ")").Append(" {\n");
            foreach (var a in actions) sb.Append("    ").Append(a).Append('\n');
            sb.Append("}\n");
        }
        sb.Append(EndMarker).Append('\n');
        return sb.ToString();
    }

    /// <summary>Position of the managed block's text (from the begin marker to after the end marker line).</summary>
    private static (int Start, int End)? Block(string? script)
    {
        if (string.IsNullOrEmpty(script)) return null;
        var start = script.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = script.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return null;
        end += EndMarker.Length;
        if (end < script.Length && script[end] == '\r') end++;
        if (end < script.Length && script[end] == '\n') end++;
        return (start, end);
    }

    private static string Remove(string script) =>
        Block(script) is { } b ? script[..b.Start] + script[b.End..] : script;

    /// <summary>End of the leading comments and <c>require</c> commands.</summary>
    public static int HeaderEnd(string script)
    {
        int pos = 0, end = 0;
        while (pos < script.Length)
        {
            if (char.IsWhiteSpace(script[pos])) { pos++; continue; }
            if (script[pos] == '#')
            {
                pos = LineEnd(script, pos);
                continue;
            }
            if (script.AsSpan(pos).StartsWith("/*"))
            {
                var close = script.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                pos = close < 0 ? script.Length : close + 2;
                continue;
            }
            var command = RequireRegex().Match(script, pos);
            if (!command.Success || command.Index != pos) break;
            // require "x"; or require ["x", "y"]; — skip to the semicolon outside quoted strings.
            bool quoted = false;
            int i = pos + "require".Length;
            for (; i < script.Length; i++)
            {
                var c = script[i];
                if (quoted && c == '\\') { i++; continue; }
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == ';') break;
            }
            pos = end = Math.Min(script.Length, i + 1);
            // Take the rest of the line along when it is only blanks or a comment.
            var lineEnd = LineEnd(script, pos);
            var tail = script[pos..lineEnd].Trim();
            if (tail.Length == 0 || tail.StartsWith('#')) pos = end = lineEnd;
        }
        return end;
    }

    private static int LineEnd(string s, int from)
    {
        var nl = s.IndexOf('\n', from);
        return nl < 0 ? s.Length : nl + 1;
    }

    /// <summary>A Sieve quoted string; line breaks are not allowed in what the rule editor stores.</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\r", "").Replace("\n", " ").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string StringList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";

    [GeneratedRegex(@"require(?=[\s\[""])", RegexOptions.CultureInvariant)]
    private static partial Regex RequireRegex();

    [GeneratedRegex(@"#[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentRegex();

    /// <summary>What a rule's JSON comment holds (short names keep the line readable).</summary>
    private sealed class StoredRule
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Off { get; set; }
        public List<string>? From { get; set; }
        public List<string>? Subject { get; set; }
        public List<string>? Body { get; set; }
        public List<string>? To { get; set; }
        public bool NoCopy { get; set; }
        public bool Stop { get; set; }

        public static StoredRule Of(ForwardingRule r) => new()
        {
            Id = r.Id, Name = r.Name, Off = !r.IsEnabled,
            From = r.FromAddresses.Count > 0 ? r.FromAddresses : null,
            Subject = r.SubjectContains.Count > 0 ? r.SubjectContains : null,
            Body = r.BodyContains.Count > 0 ? r.BodyContains : null,
            To = r.Recipients.Select(a => a.Name.Length > 0 && a.Name != a.Address ? $"{a.Name} <{a.Address}>" : a.Address).ToList(),
            NoCopy = !r.KeepCopy, Stop = r.StopProcessing,
        };

        public ForwardingRule ToRule() => new()
        {
            Id = Id, Name = Name, IsEnabled = !Off,
            FromAddresses = From ?? new(), SubjectContains = Subject ?? new(), BodyContains = Body ?? new(),
            Mode = ForwardingMode.Redirect,
            Recipients = (To ?? new()).Select(EmailAddress.Parse).OfType<EmailAddress>().ToList(),
            KeepCopy = !NoCopy, StopProcessing = Stop,
        };
    }
}
