using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MailClient.App.Services;

namespace MailClient.Linux.Controls;

/// <summary>Formatting at the caret, reported by the editor page for the toolbar.</summary>
public sealed record EditorFormatState(string FontFamily, double FontSizePt, bool Bold, bool Italic, bool Underline,
    bool Bullets, bool Numbering);

/// <summary>
/// Rich-text (HTML) message editor: the same editor page as on Windows (editor.html, contenteditable) inside
/// WebKitGTK, driven through its script API. Falls back to plain text without WebKitGTK.
/// </summary>
public sealed class HtmlEditor : ContentControl
{
    private readonly NativeWebView? _web;
    private readonly TextBox _plain = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12),
        PlaceholderText = "Текст письма…",
    };
    private readonly TaskCompletionSource<bool> _ready = new();
    private bool _allowNavigation = true;

    public event EventHandler? ContentChanged;
    public event EventHandler<EditorFormatState>? FormatStateChanged;

    /// <summary>False when the editor works in plain-text mode.</summary>
    public bool IsHtml => _web != null;

    public HtmlEditor()
    {
        _plain.TextChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        if (!WebKit.IsAvailable)
        {
            Content = _plain;
            _ready.TrySetResult(false);
            return;
        }
        _web = new NativeWebView();
        _web.NavigationStarted += (_, e) =>
        {
            if (_allowNavigation)
            {
                _allowNavigation = false;
                return;
            }
            e.Cancel = true; // links inside the editor must never navigate
        };
        _web.NewWindowRequested += (_, e) => e.Handled = true;
        _web.NavigationCompleted += async (_, _) =>
        {
            if (_ready.Task.IsCompleted) return;
            await _web.InvokeScript($"setDark({(ThemeService.IsDark ? "true" : "false")})");
            _ready.TrySetResult(true);
        };
        _web.WebMessageReceived += (_, e) => OnMessage(e.Body);
        Content = _web;
        _web.NavigateToString(LoadEditorPage(), new Uri("about:blank"));
    }

    private void OnMessage(string? body)
    {
        if (string.IsNullOrEmpty(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "changed":
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "state":
                    FormatStateChanged?.Invoke(this, new EditorFormatState(
                        m.GetProperty("font").GetString() ?? "", m.GetProperty("size").GetDouble(),
                        m.GetProperty("bold").GetBoolean(), m.GetProperty("italic").GetBoolean(),
                        m.GetProperty("underline").GetBoolean(),
                        m.GetProperty("bullets").GetBoolean(), m.GetProperty("numbers").GetBoolean()));
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

    private async Task<string?> RunAsync(string function, params object?[] args)
    {
        if (!await _ready.Task || _web == null) return null;
        return await _web.InvokeScript($"{function}({string.Join(", ", args.Select(a => JsonSerializer.Serialize(a)))})");
    }

    public async Task SetHtmlAsync(string html)
    {
        if (await _ready.Task) await RunAsync("setContent", html);
        else _plain.Text = Core.Rendering.MessageHtmlBuilder.HtmlToText(html);
    }

    /// <summary>The body: HTML when <see cref="IsHtml"/>, otherwise plain text.</summary>
    public async Task<string> GetContentAsync()
    {
        if (!await _ready.Task) return _plain.Text ?? "";
        var result = await RunAsync("getContent") ?? "";
        // The engine returns the string either as is or JSON-encoded.
        if (result.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(result) ?? ""; }
            catch (JsonException) { }
        }
        return result;
    }

    public Task SetBaseFontAsync(string family, double sizePt) => RunAsync("setBaseFont", family, sizePt);

    /// <summary>A document.execCommand command (bold, italic, insertUnorderedList, undo…).</summary>
    public Task ExecAsync(string command, string? value = null) => RunAsync("exec", command, value);

    public Task SetFontFamilyAsync(string family) => RunAsync("setFontFamily", family);

    public Task SetFontSizeAsync(double sizePt) => RunAsync("setFontSize", sizePt);

    public async Task FocusEditorAsync()
    {
        if (!await _ready.Task)
        {
            _plain.Focus();
            return;
        }
        _web?.Focus();
        await RunAsync("focusStart");
    }
}
