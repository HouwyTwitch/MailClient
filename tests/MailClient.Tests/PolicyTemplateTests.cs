using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MailClient.Tests;

/// <summary>
/// The Group Policy templates (deploy/admx) must offer exactly the registry values the program reads, and every
/// text they reference must exist in each language.
/// </summary>
public partial class PolicyTemplateTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static XDocument Admx => XDocument.Load(RepoFile("deploy", "admx", "MailClient.admx"));

    [Fact]
    public void Templates_cover_exactly_the_values_the_program_reads()
    {
        var source = File.ReadAllText(RepoFile("src", "MailClient.App", "Services", "OrganizationDefaults.cs"));
        var read = GetValueRegex().Matches(source).Select(m => m.Groups[1].Value).ToHashSet();

        var offered = Admx.Descendants().Select(e => (string?)e.Attribute("valueName")).OfType<string>().ToHashSet();

        Assert.Equal(read.Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));
        Assert.All(Admx.Descendants(Ns + "policy"), p => Assert.Equal(@"Software\Policies\MailClient", (string?)p.Attribute("key")));
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Every_text_and_presentation_exists_in_the_language_file(string language)
    {
        var adml = XDocument.Load(RepoFile("deploy", "admx", language, "MailClient.adml"));
        var strings = adml.Descendants(Ns + "string").Select(s => (string)s.Attribute("id")!).ToHashSet();
        var presentations = adml.Descendants(Ns + "presentation").ToDictionary(p => (string)p.Attribute("id")!);
        var admx = Admx;

        foreach (var reference in admx.Descendants().Attributes().Select(a => a.Value))
        {
            if (reference.StartsWith("$(string.", StringComparison.Ordinal))
                Assert.Contains(reference[9..^1], strings);
            else if (reference.StartsWith("$(presentation.", StringComparison.Ordinal))
                Assert.True(presentations.ContainsKey(reference[15..^1]), reference);
        }

        // Each control of a presentation points at an element of the policy that uses it.
        foreach (var policy in admx.Descendants(Ns + "policy").Where(p => p.Attribute("presentation") != null))
        {
            var ids = policy.Element(Ns + "elements")!.Elements().Select(e => (string)e.Attribute("id")!).ToHashSet();
            var presentation = presentations[((string)policy.Attribute("presentation")!)[15..^1]];
            Assert.All(presentation.Elements(), c => Assert.Contains((string)c.Attribute("refId")!, ids));
        }
    }

    [GeneratedRegex(@"GetValue\(""(\w+)""\)")]
    private static partial Regex GetValueRegex();
}
