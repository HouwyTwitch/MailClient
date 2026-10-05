using System.Windows;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

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

    private void Respond(ComposeAction action)
    {
        WindowFactory.OpenCompose(ComposeViewModel.ForResponse(_main.Sessions, _preview.Session, _preview.Message, action), _main.Settings);
    }

    private void Reply_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.Reply);
    private void ReplyAll_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.ReplyAll);
    private void Forward_Click(object sender, RoutedEventArgs e) => Respond(ComposeAction.Forward);
    private async void Print_Click(object sender, RoutedEventArgs e) => await BodyView.PrintAsync();
}
