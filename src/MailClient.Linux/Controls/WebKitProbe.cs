using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using MailClient.App.Services;

namespace MailClient.Linux.Controls;

/// <summary>
/// A crash inside WebKitGTK (a graphics driver, a renderer that does not work on this computer) would end the
/// whole program, and .NET cannot catch it. So before the first use of a WebKitGTK version the program starts
/// itself with <c>--probe-webkit</c> to show a test page: if that crashes, WebKitGTK is tried again with its
/// conservative rendering switches, and if nothing works, letters are shown as text. The outcome is remembered
/// until WebKitGTK or the program is updated.
/// </summary>
public static class WebKitProbe
{
    public const string ProbeArgument = "--probe-webkit";
    private const string OkMarker = "probe:ok";

    /// <summary>Environment settings tried in order: as is, then WebKitGTK's software-friendly renderers.</summary>
    private static readonly Dictionary<string, string>[] Variants =
    [
        new(),
        new() { ["WEBKIT_DISABLE_DMABUF_RENDERER"] = "1" },
        new() { ["WEBKIT_DISABLE_DMABUF_RENDERER"] = "1", ["WEBKIT_DISABLE_COMPOSITING_MODE"] = "1" },
    ];

    private sealed record Outcome(string Key, bool Works, Dictionary<string, string> Environment);

    private static string CacheFile => Path.Combine(AppPaths.Local, "webkit-probe.json");

    /// <summary>
    /// Decides how this run uses WebKitGTK (call before any web view is created): applies the remembered or newly
    /// found working settings, or switches to text mode.
    /// </summary>
    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("MAILCLIENT_NO_WEBVIEW") == "1" || Environment.GetEnvironmentVariable("MAILCLIENT_WEBKIT_PROBED") == "1")
            return;
        var key = EngineKey();
        if (key == null) return; // no WebKitGTK installed: text mode anyway

        var outcome = Load(key) ?? Probe(key);
        if (outcome.Works)
        {
            foreach (var (name, value) in outcome.Environment)
                if (Environment.GetEnvironmentVariable(name) == null) Environment.SetEnvironmentVariable(name, value);
        }
        else
        {
            Environment.SetEnvironmentVariable("MAILCLIENT_NO_WEBVIEW", "1");
        }
    }

    private static Outcome Probe(string key)
    {
        foreach (var variant in Variants)
        {
            if (RunProbe(variant))
            {
                Log.Info($"WebKitGTK работает{(variant.Count == 0 ? "" : " с настройками " + string.Join(", ", variant.Select(kv => $"{kv.Key}={kv.Value}")))}");
                return Save(new Outcome(key, true, variant));
            }
        }
        Log.Warn("WebKitGTK на этом компьютере аварийно завершается — письма будут показываться в текстовом виде");
        return Save(new Outcome(key, false, new()));
    }

    private static bool RunProbe(Dictionary<string, string> variant)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // A framework-dependent start (dotnet mailclient.dll) passes the assembly first.
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(WebKitProbe).Assembly.Location);
            start.ArgumentList.Add(ProbeArgument);
            foreach (var (name, value) in variant) start.Environment[name] = value;
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }
            return p.ExitCode == 0 && output.Result.Contains(OkMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Проверка WebKitGTK не запущена: {ex.Message}");
            return false;
        }
    }

    /// <summary>The probe itself (child process): a web view with a small page; prints the marker once it loaded.</summary>
    public static int RunInChild()
    {
        Environment.SetEnvironmentVariable("MAILCLIENT_WEBKIT_PROBED", "1");
        return AppBuilder.Configure<Application>().UsePlatformDetect().StartWithClassicDesktopLifetime([], lifetime =>
        {
            lifetime.Startup += (_, _) =>
            {
                var web = new NativeWebView();
                var mode = Environment.GetEnvironmentVariable("MAILCLIENT_PROBE_MODE");
                Control content = web;
                if (mode == "hidden") content = new Panel { IsVisible = false, Children = { web } };
                if (mode == "nonav") web.NavigationStarted += (_, e) => e.Cancel = false;
                var window = new Window { Width = 300, Height = 200, Content = content, ShowInTaskbar = false };
                if (mode == "hidden") _ = Task.Delay(1500).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Console.WriteLine(OkMarker); lifetime.Shutdown(0); }));
                web.NavigationCompleted += async (_, e) =>
                {
                    await Task.Delay(1500); // let the page render (most crashes happen while compositing)
                    Console.WriteLine(e.IsSuccess ? OkMarker : "probe:navigation-failed");
                    lifetime.Shutdown(e.IsSuccess ? 0 : 2);
                };
                window.Show();
                web.NavigateToString("<html><body><p>Проверка <b>WebKitGTK</b></p></body></html>", new Uri("about:blank"));
                _ = Task.Delay(20_000).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => lifetime.Shutdown(3)));
            };
        });
    }

    /// <summary>Identifies the installed engine and this program build; a change of either repeats the probe.</summary>
    private static string? EngineKey()
    {
        string[] names = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37", "libWPEWebKit-2.0.so.1"];
        string[] dirs = ["/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib", "/lib/x86_64-linux-gnu"];
        foreach (var dir in dirs)
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? new FileInfo(path);
                return $"{target.FullName}|{target.Length}|{target.LastWriteTimeUtc.Ticks}|{AppInfo.Version}";
            }
        return null;
    }

    private static Outcome? Load(string key)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var outcome = JsonSerializer.Deserialize<Outcome>(File.ReadAllText(CacheFile));
            return outcome?.Key == key ? outcome : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static Outcome Save(Outcome outcome)
    {
        try
        {
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(outcome));
        }
        catch (IOException ex)
        {
            Log.Warn($"Результат проверки WebKitGTK не сохранён: {ex.Message}");
        }
        return outcome;
    }
}
