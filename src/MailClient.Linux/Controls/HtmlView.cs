using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MailClient.App.Services;
using MailClient.Core.Rendering;

namespace MailClient.Linux.Controls;

/// <summary>
/// A message body: the sanitized HTML from MessageHtmlBuilder in WebKitGTK; links open in the default browser and
/// the page itself never navigates. Without WebKitGTK the text of the message is shown instead.
/// </summary>
public sealed class HtmlView : ContentControl
{
    public static readonly StyledProperty<string?> HtmlProperty = AvaloniaProperty.Register<HtmlView, string?>(nameof(Html));

    private readonly NativeWebView? _web;
    private readonly SelectableTextBlock? _text;
    private bool _allowNext;

    public HtmlView()
    {
        if (WebKit.IsAvailable)
        {
            _web = new NativeWebView();
            _web.NavigationStarted += (_, e) =>
            {
                if (_allowNext)
                {
                    _allowNext = false;
                    return;
                }
                e.Cancel = true;
                if (e.Request is { } uri && DesktopIntegration.IsSafeExternalLink(uri)) DesktopIntegration.ShellOpen(uri.ToString());
            };
            _web.NewWindowRequested += (_, e) => e.Handled = true;
            Content = _web;
        }
        else
        {
            _text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 12) };
            Content = new ScrollViewer { Content = _text };
        }
    }

    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HtmlProperty) return;
        var html = change.GetNewValue<string?>() ?? "";
        if (_web != null)
        {
            _allowNext = true;
            _web.NavigateToString(html, new Uri("about:blank"));
        }
        else if (_text != null)
        {
            _text.Text = MessageHtmlBuilder.HtmlToText(html);
        }
    }
}
