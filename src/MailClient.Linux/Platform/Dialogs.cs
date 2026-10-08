using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MailClient.App.Services;

/// <summary>
/// Message boxes for the Linux build, with the same calls as on Windows. They are modal and return the answer
/// directly (a nested dispatcher frame keeps the interface responsive), so the shared view models work unchanged.
/// </summary>
public static class Dialogs
{
    public static void Error(string message, string title = "Ошибка")
    {
        Log.Warn($"Показана ошибка: {message}");
        Show(message, title, ["ОК"]);
    }

    public static void Error(Exception ex, string action) => Error($"{action}.\n\n{RuText.Error(ex)}");

    public static void Info(string message, string title = "Корпоративная почта") => Show(message, title, ["ОК"]);

    public static bool Confirm(string message, string title = "Подтверждение") => Show(message, title, ["Да", "Нет"]) == 0;

    /// <summary>Returns true = yes, false = no, null = cancel.</summary>
    public static bool? YesNoCancel(string message, string title = "Подтверждение") =>
        Show(message, title, ["Да", "Нет", "Отмена"]) switch
        {
            0 => true,
            1 => false,
            _ => null,
        };

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    public static string? Prompt(string title, string label, string initial)
    {
        if (Application.Current == null) return null;
        string? result = null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "ОК", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            result = box.Text;
            window.Close();
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
            },
        };
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return ShowModal(window, () => result);
    }

    /// <summary>The active window of the application (owner of dialogs), if any.</summary>
    internal static Window? ActiveWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    /// <summary>Waits for a task while the interface keeps running (like a modal dialog).</summary>
    internal static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted && Dispatcher.UIThread.CheckAccess())
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Shows a window modally (owned by the active window when there is one) and returns its result.</summary>
    internal static T ShowModal<T>(Window dialog, Func<T> result)
    {
        var owner = ActiveWindow;
        Task done;
        if (owner is { IsVisible: true } && owner != dialog)
        {
            done = dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            done = closed.Task;
        }
        Wait(done.ContinueWith(_ => true, TaskScheduler.Default));
        return result();
    }

    private static int Show(string message, string title, string[] buttons)
    {
        if (Application.Current == null)
        {
            // No interface (command-line use, tests): the log is the only place for the message.
            Log.Info($"{title}: {message}");
            return buttons.Length - 1;
        }
        int answer = buttons.Length - 1; // closing the window = the last (safest) choice
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                answer = index;
                window.Close();
            };
            panel.Children.Add(button);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 480 },
                panel,
            },
        };
        return ShowModal(window, () => answer);
    }
}
