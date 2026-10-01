using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.Core.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailClient.App.Controls;

/// <summary>
/// Read-only message renderer on WebView2 with scripts disabled. All navigation is blocked; http(s)/mailto
/// links open in the user's default browser/mail handler. Falls back to plain text if WebView2 is missing.
/// </summary>
public sealed class MessageBodyView : UserControl
{
    private const string VirtualHost = "preview.mailclient.local";

    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(MessageBodyView),
        new PropertyMetadata(null, (d, _) => ((MessageBodyView)d).Render()));

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    private readonly WebView2 _web = new();
    private readonly TextBox _fallback = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(16),
        Visibility = Visibility.Collapsed,
    };
    private Task<bool>? _init;
    private bool _allowNextNavigation;
    private string? _lastFile;

    public MessageBodyView()
    {
        var grid = new Grid();
        grid.Children.Add(_web);
        grid.Children.Add(_fallback);
        Content = grid;
        _web.DefaultBackgroundColor = System.Drawing.Color.White;
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => CleanupFile();
    }

    private Task<bool> EnsureInitializedAsync() => _init ??= InitializeAsync();

    private async Task<bool> InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewHost.GetEnvironmentAsync());
            var core = _web.CoreWebView2;
            WebViewHost.HardenSettings(core.Settings, allowScripts: false);
            core.SetVirtualHostNameToFolderMapping(VirtualHost, AppPaths.Preview, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 недоступен, используется текстовый просмотр: {ex.Message}");
            _web.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_allowNextNavigation)
        {
            _allowNextNavigation = false;
            return;
        }
        // Anchors inside the same document (#section) are harmless.
        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && WindowsIntegration.IsSafeExternalLink(u))
            WindowsIntegration.ShellOpen(u.AbsoluteUri);
    }

    private async void Render()
    {
        if (!IsLoaded) return;
        var html = Html ?? "";
        if (!await EnsureInitializedAsync())
        {
            _fallback.Text = MessageHtmlBuilder.HtmlToText(html);
            return;
        }
        try
        {
            CleanupFile();
            _allowNextNavigation = true;
            if (html.Length < 1_500_000)
            {
                _web.NavigateToString(html.Length == 0 ? "<html><body></body></html>" : html);
            }
            else
            {
                // NavigateToString is limited to 2 MB: large messages (embedded images) go through a temp file.
                _lastFile = $"{Guid.NewGuid():N}.html";
                await File.WriteAllTextAsync(Path.Combine(AppPaths.Preview, _lastFile), html);
                _web.CoreWebView2.Navigate($"https://{VirtualHost}/{_lastFile}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка отображения письма", ex);
        }
    }

    private void CleanupFile()
    {
        if (_lastFile == null) return;
        try { File.Delete(Path.Combine(AppPaths.Preview, _lastFile)); } catch { }
        _lastFile = null;
    }

    /// <summary>Prints the current message via the browser print dialog.</summary>
    public async Task PrintAsync()
    {
        if (await EnsureInitializedAsync())
            _web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }
}
