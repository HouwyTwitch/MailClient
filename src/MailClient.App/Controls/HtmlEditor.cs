using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailClient.App.Controls;

/// <summary>Formatting at the caret, reported by the editor page for the toolbar.</summary>
public sealed record EditorFormatState(
    string FontFamily, double FontSizePt, bool Bold, bool Italic, bool Underline, bool Strikethrough,
    bool Bullets, bool Numbering, string Alignment, bool CanUndo, bool CanRedo);

/// <summary>
/// Rich-text (HTML) message editor: a contenteditable page inside WebView2, driven through a small script API
/// (editor.html). Falls back to a plain-text box when the WebView2 runtime is not installed.
/// </summary>
public sealed class HtmlEditor : UserControl, IDisposable
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
    public event EventHandler<EditorFormatState>? FormatStateChanged;

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
            core.WebMessageReceived += OnWebMessage;
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "changed":
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "filedrop":
                    FilesDroppedOnEditor?.Invoke(this, EventArgs.Empty);
                    break;
                case "state":
                    FormatStateChanged?.Invoke(this, new EditorFormatState(
                        m.GetProperty("font").GetString() ?? "", m.GetProperty("size").GetDouble(),
                        m.GetProperty("bold").GetBoolean(), m.GetProperty("italic").GetBoolean(),
                        m.GetProperty("underline").GetBoolean(), m.GetProperty("strike").GetBoolean(),
                        m.GetProperty("bullets").GetBoolean(), m.GetProperty("numbers").GetBoolean(),
                        m.GetProperty("align").GetString() ?? "left",
                        m.GetProperty("canUndo").GetBoolean(), m.GetProperty("canRedo").GetBoolean()));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Редактор: некорректное сообщение страницы: {ex.Message}");
        }
    }

    private static string LoadEditorPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MailClient.App.Assets.editor.html")
                      ?? throw new InvalidOperationException("editor.html resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private async Task<bool> RunAsync(string function, params object?[] args)
    {
        if (!await _ready.Task) return false;
        await _web.ExecuteScriptAsync($"{function}({string.Join(", ", args.Select(a => JsonSerializer.Serialize(a)))})");
        return true;
    }

    public async Task SetHtmlAsync(string html)
    {
        if (!await RunAsync("setContent", html)) _plain.Text = Core.Rendering.MessageHtmlBuilder.HtmlToText(html);
    }

    /// <summary>Returns the body: HTML when <see cref="IsHtml"/>, otherwise plain text.</summary>
    public async Task<string> GetContentAsync()
    {
        if (!await _ready.Task) return _plain.Text;
        var json = await _web.ExecuteScriptAsync("getContent()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>Default font of the message; a reopened draft keeps the font it was written in.</summary>
    public Task SetBaseFontAsync(string family, double sizePt) => RunAsync("setBaseFont", family, sizePt);

    /// <summary>A document.execCommand command (bold, italic, insertOrderedList, justifyCenter, undo…).</summary>
    public Task ExecAsync(string command, string? value = null) => RunAsync("exec", command, value);

    public Task SetFontFamilyAsync(string family) => RunAsync("setFontFamily", family);

    public Task SetFontSizeAsync(double sizePt) => RunAsync("setFontSize", sizePt);

    /// <summary>Text colour (#RRGGBB); null removes the colour.</summary>
    public Task SetTextColorAsync(string? color) => RunAsync("setColor", color);

    /// <summary>Highlight colour (#RRGGBB); null removes the highlight.</summary>
    public Task SetHighlightAsync(string? color) => RunAsync("setHighlight", color);

    public Task InsertImageAsync(string dataUrl) => RunAsync("insertImage", dataUrl);

    /// <summary>
    /// Puts the signature at the end of the text or replaces the one there; "" removes it. With
    /// <paramref name="onlyIfPresent"/> a text without a signature stays as it is.
    /// </summary>
    public async Task SetSignatureAsync(string html, bool onlyIfPresent = false)
    {
        if (await RunAsync("setSignature", html, onlyIfPresent)) return;
        // Plain-text fallback: the signature goes at the end once.
        var text = Core.Rendering.MessageHtmlBuilder.HtmlToText(html).Trim();
        if (!onlyIfPresent && text.Length > 0 && !_plain.Text.Contains(text, StringComparison.Ordinal))
            _plain.Text = _plain.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + text;
    }

    /// <summary>Returns keyboard focus to the text, keeping the selection (after a toolbar action).</summary>
    public async Task FocusAsync()
    {
        if (!await _ready.Task)
        {
            _plain.Focus();
            return;
        }
        _web.Focus();
        await _web.ExecuteScriptAsync("focusEditor()");
    }

    /// <summary>Releases the browser instance (called when the owning window closes).</summary>
    public void Dispose()
    {
        _ready.TrySetResult(false);
        _web.Dispose();
    }

    /// <summary>Puts the caret at the start of the text.</summary>
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
