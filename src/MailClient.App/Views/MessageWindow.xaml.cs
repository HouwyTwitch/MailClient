using System.Windows;
using System.Windows.Input;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

/// <summary>A message in its own window (double-click in the list): the same view as the reading pane.</summary>
public partial class MessageWindow : Window
{
    private readonly MessagePreviewViewModel _preview;
    private readonly MainViewModel _main;

    public MessageWindow(MessagePreviewViewModel preview, MainViewModel main)
    {
        InitializeComponent();
        _preview = preview;
        _main = main;
        DataContext = preview;
    }

    protected override void OnClosed(EventArgs e)
    {
        PreviewView.BodyView.Dispose();
        base.OnClosed(e);
    }

    private void Respond(ComposeAction action) =>
        WindowFactory.OpenCompose(ComposeViewModel.ForResponse(_main.Sessions, _preview.Session, _preview.Message, action), _main.Settings);

    private void Reply_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.Reply);
    private void ReplyAll_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.ReplyAll);
    private void Forward_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.Forward);
    private async void Print_Click(object sender, RoutedEventArgs e) => await PreviewView.BodyView.PrintAsync();

    /// <summary>The main window's shortcuts work here too; Esc closes the window.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.R when ctrl && shift: Respond(ComposeAction.ReplyAll); break;
            case Key.R when ctrl: Respond(ComposeAction.Reply); break;
            case Key.F when ctrl: Respond(ComposeAction.Forward); break;
            case Key.P when ctrl: Print_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }
}
