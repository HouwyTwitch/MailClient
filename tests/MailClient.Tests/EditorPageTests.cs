using System.Diagnostics;
using Xunit;

namespace MailClient.Tests;

/// <summary>
/// Runs tests/editor/editor.check.js: the message editor page (fonts, sizes, colours, drafts, toolbar state) in
/// Chromium, the engine behind WebView2. Enabled when EDITOR_TEST=1; needs Node.js and the "playwright" package
/// (PLAYWRIGHT_MODULE may point to it).
/// </summary>
public class EditorPageTests
{
    [Fact]
    public async Task Editor_page_formatting_works_in_chromium()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EDITOR_TEST") == "1",
            "Проверка редактора в Chromium включается переменной EDITOR_TEST=1 (нужны Node.js и Playwright).");

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MailClient.sln"))) root = Path.GetDirectoryName(root)!;
        var psi = new ProcessStartInfo("node", Path.Combine(root, "tests", "editor", "editor.check.js"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var node = Process.Start(psi)!;
        var output = await node.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = await node.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await node.WaitForExitAsync(TestContext.Current.CancellationToken);

        TestContext.Current.TestOutputHelper?.WriteLine(output);
        Assert.True(node.ExitCode == 0, $"{output}\n{errors}");
        Assert.DoesNotContain("FAIL", output);
    }
}
