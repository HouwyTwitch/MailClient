using System.Windows;
using Forms = System.Windows.Forms;

namespace MailClient.App.Services;

/// <summary>Notification-area icon with new-mail balloons and a small menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public event EventHandler? OpenRequested;
    public event EventHandler? NewMessageRequested;
    public event EventHandler? ExitRequested;

    public TrayService()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
            Text = "Корпоративная почта",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Новое письмо", null, (_, _) => NewMessageRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetUnread(int unread)
    {
        var text = unread > 0 ? $"Корпоративная почта — {RuText.Count(unread, "непрочитанное", "непрочитанных", "непрочитанных")}" : "Корпоративная почта";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title.Length > 63 ? title[..63] : title;
        _icon.BalloonTipText = text.Length > 255 ? text[..255] : text;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
