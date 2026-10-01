using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;
using Microsoft.Win32;

namespace MailClient.App.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private readonly DispatcherTimer _suggestTimer;
    private TextBox? _suggestTarget;
    private CancellationTokenSource? _suggestCts;
    private bool _closeConfirmed;

    public ComposeWindow(ComposeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.GetBody = async () => (await Editor.GetContentAsync(), Editor.IsHtml);
        vm.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };

        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _suggestTimer.Tick += async (_, _) =>
        {
            _suggestTimer.Stop();
            await ShowSuggestionsAsync();
        };
        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.TextChanged += (_, _) =>
            {
                if (!box.IsKeyboardFocused) return;
                _suggestTarget = box;
                _suggestTimer.Stop();
                _suggestTimer.Start();
            };
            box.PreviewKeyDown += RecipientBox_PreviewKeyDown;
        }

        Editor.ContentChanged += (_, _) => vm.IsDirty = true;
        Editor.FilesDroppedOnEditor += (_, _) => vm.StatusText = "Перетащите файлы на заголовок письма или нажмите «Вложить файл»";
        Loaded += async (_, _) =>
        {
            await Editor.SetHtmlAsync(vm.InitialHtml);
            vm.IsDirty = false;
            if (string.IsNullOrWhiteSpace(vm.To)) ToBox.Focus();
            else await Editor.FocusEditorAsync();
        };
    }

    // ------------------------------------------------------------------ closing

    public bool HasUnsavedChanges => _vm.IsDirty;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !_vm.IsDirty) return;
        e.Cancel = true;
        var answer = Dialogs.YesNoCancel("Сохранить изменения в черновиках?", "Письмо не отправлено");
        if (answer == null) return;
        if (answer == true && !await _vm.SaveDraftCoreAsync(closeAfter: false)) return;
        _closeConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------------ attachments

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Вложить файлы", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _vm.AddFiles(dlg.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm.AddFiles(files);
    }

    // ------------------------------------------------------------------ formatting

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string cmd }) await Editor.ExecAsync(cmd);
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string color }) await Editor.ExecAsync("foreColor", color);
    }

    private async void Highlight_Click(object sender, RoutedEventArgs e) => await Editor.ExecAsync("hiliteColor", "#FFF100");

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = WindowFactory.Prompt("Вставка ссылки", "Адрес (URL):", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !WindowsIntegration.IsSafeExternalLink(uri))
        {
            Dialogs.Error("Допускаются только ссылки http://, https:// и mailto:.");
            return;
        }
        await Editor.ExecAsync("createLink", uri.AbsoluteUri);
    }

    // ------------------------------------------------------------------ recipient autocomplete

    private static (int start, string token) CurrentToken(TextBox box)
    {
        var text = box.Text;
        var caret = Math.Min(box.CaretIndex, text.Length);
        int start = text.LastIndexOfAny(new[] { ';', ',' }, Math.Max(0, caret - 1)) + 1;
        if (caret == 0) start = 0;
        return (start, text[start..caret].Trim());
    }

    private async Task ShowSuggestionsAsync()
    {
        var box = _suggestTarget;
        if (box == null) return;
        var (_, token) = CurrentToken(box);
        if (token.Length < 2 || token.Contains('<'))
        {
            SuggestPopup.IsOpen = false;
            return;
        }
        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var results = await _vm.SuggestAsync(token, _suggestCts.Token);
        if (_suggestCts.IsCancellationRequested || box != _suggestTarget) return;
        SuggestList.ItemsSource = results;
        SuggestPopup.PlacementTarget = box;
        SuggestPopup.Width = box.ActualWidth;
        SuggestPopup.IsOpen = results.Count > 0;
        if (results.Count > 0) SuggestList.SelectedIndex = 0;
    }

    private void RecipientBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                SuggestList.SelectedIndex = Math.Min(SuggestList.Items.Count - 1, SuggestList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                SuggestList.SelectedIndex = Math.Max(0, SuggestList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (SuggestList.SelectedItem is Contact c)
                {
                    AcceptSuggestion(c);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                SuggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SuggestList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is Contact c) AcceptSuggestion(c);
    }

    private void SuggestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestList.SelectedItem is Contact c) AcceptSuggestion(c);
    }

    private void AcceptSuggestion(Contact c)
    {
        var box = _suggestTarget;
        SuggestPopup.IsOpen = false;
        if (box == null) return;
        var (start, _) = CurrentToken(box);
        var caret = Math.Min(box.CaretIndex, box.Text.Length);
        var formatted = EmailAddress.FormatList(new[] { new EmailAddress(c.DisplayName, c.PrimaryEmail) }) + "; ";
        var prefix = box.Text[..start];
        if (prefix.Length > 0 && !prefix.EndsWith(' ')) prefix += " ";
        box.Text = prefix + formatted + box.Text[caret..].TrimStart(';', ',', ' ');
        box.CaretIndex = (prefix + formatted).Length;
        box.Focus();
    }
}
