using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using Microsoft.Web.WebView2.Wpf;

namespace MailClient.App.Controls;

/// <summary>
/// Rich-text (HTML) message editor built on a contenteditable page inside WebView2.
/// Falls back to a plain-text box when the WebView2 runtime is not installed.
/// </summary>
public sealed class HtmlEditor : UserControl
{
    private readonly WebView2 _web = new();
    private readonly TextBox _plain = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };
    private readonly TaskCompletionSource<bool> _ready = new();
    private bool _initStarted;
    private bool _allowNavigation;

    public event EventHandler? ContentChanged;
    public event EventHandler? FilesDroppedOnEditor;

    /// <summary>False when running in plain-text fallback mode.</summary>
    public bool IsHtml { get; private set; } = true;

    public HtmlEditor()
    {
        var grid = new Grid();
        grid.Children.Add(_web);
        grid.Children.Add(_plain);
        Content = grid;
        _plain.TextChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewHost.GetEnvironmentAsync());
            var core = _web.CoreWebView2;
            WebViewHost.HardenSettings(core.Settings, allowScripts: true);
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (_allowNavigation) { _allowNavigation = false; return; }
                e.Cancel = true; // links inside the editor must never navigate
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg == "changed") ContentChanged?.Invoke(this, EventArgs.Empty);
                else if (msg == "filedrop") FilesDroppedOnEditor?.Invoke(this, EventArgs.Empty);
            };
            var navigated = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, _) => navigated.TrySetResult(true);
            _allowNavigation = true;
            _web.NavigateToString(LoadEditorPage());
            await navigated.Task;
            await _web.ExecuteScriptAsync($"setDark({(ThemeService.IsDark ? "true" : "false")})");
            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 недоступен, редактор работает в текстовом режиме: {ex.Message}");
            IsHtml = false;
            _web.Visibility = Visibility.Collapsed;
            _plain.Visibility = Visibility.Visible;
            _ready.TrySetResult(false);
        }
    }

    private static string LoadEditorPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MailClient.App.Assets.editor.html")
                      ?? throw new InvalidOperationException("editor.html resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public async Task SetHtmlAsync(string html)
    {
        if (!await _ready.Task)
        {
            _plain.Text = Core.Rendering.MessageHtmlBuilder.HtmlToText(html);
            return;
        }
        await _web.ExecuteScriptAsync($"setContent({JsonSerializer.Serialize(html)})");
    }

    /// <summary>Returns the body: HTML when <see cref="IsHtml"/>, otherwise plain text.</summary>
    public async Task<string> GetContentAsync()
    {
        if (!await _ready.Task) return _plain.Text;
        var json = await _web.ExecuteScriptAsync("getContent()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public async Task ExecAsync(string command, string? value = null)
    {
        if (!await _ready.Task) return;
        var arg = value == null ? "null" : JsonSerializer.Serialize(value);
        await _web.ExecuteScriptAsync($"exec({JsonSerializer.Serialize(command)}, {arg})");
    }

    public async Task FocusEditorAsync()
    {
        if (!await _ready.Task)
        {
            _plain.Focus();
            return;
        }
        _web.Focus();
        await _web.ExecuteScriptAsync("focusStart()");
    }
}
