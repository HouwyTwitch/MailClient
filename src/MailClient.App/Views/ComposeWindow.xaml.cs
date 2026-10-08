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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Released in OnClosed, where a window's lifetime ends")]
public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _suggestTimer;
    private TextBox? _suggestTarget;
    private CancellationTokenSource? _suggestCts;
    private static readonly char[] RecipientSeparators = [';', ','];
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    public ComposeWindow(ComposeViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        Formatting.Attach(Editor);
        vm.SignatureChangeRequested += async (_, html) => await Editor.SetSignatureAsync(html, onlyIfPresent: true);
        vm.GetBody = async () => (await Editor.GetContentAsync(), Editor.IsHtml);
        vm.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            // Deferred: the request may arrive while a close is already in progress.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        };

        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _suggestTimer.Tick += async (_, _) =>
        {
            _suggestTimer.Stop();
            try
            {
                await ShowSuggestionsAsync();
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer input.
            }
            catch (Exception ex)
            {
                Log.Warn($"Подсказка адресов не удалась: {ex.Message}");
            }
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
            // Base font first: a reopened draft then switches to the font it was written in.
            await Editor.SetBaseFontAsync(settings.ComposeFontFamily, settings.ComposeFontSize);
            await Editor.SetHtmlAsync(vm.InitialHtml);
            // Without WebView2 the editor is plain text: formatting does not apply.
            if (!Editor.IsHtml) FormatBar.Visibility = Visibility.Collapsed;
            vm.IsDirty = false;
            if (string.IsNullOrWhiteSpace(vm.To)) ToBox.Focus();
            else await Editor.FocusEditorAsync();
        };
    }

    // ------------------------------------------------------------------ closing

    public bool HasUnsavedChanges => _vm.IsDirty;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !_vm.IsDirty) return;
        // WPF forbids Close()/ShowDialog on a window while it is closing: cancel this close, ask once the
        // Closing event has finished, and close again if the user agrees.
        e.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ConfirmCloseAsync));
    }

    private async void ConfirmCloseAsync()
    {
        try
        {
            // The window may already be gone (application shutdown, Windows logoff).
            if (!IsVisible || PresentationSource.FromVisual(this) == null) return;
            var answer = Dialogs.YesNoCancel("Сохранить изменения в черновиках?", "Письмо не отправлено");
            if (answer == null) return;
            if (answer == true && !await _vm.SaveDraftCoreAsync(closeAfter: false)) return;
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при закрытии окна письма", ex);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _suggestTimer.Stop();
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        Editor.Dispose();
        base.OnClosed(e);
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

    // ------------------------------------------------------------------ signature

    private void Signature_Click(object sender, RoutedEventArgs e)
    {
        var menu = SignatureButton.ContextMenu;
        menu.PlacementTarget = SignatureButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void InsertSignature_Click(object sender, RoutedEventArgs e)
    {
        var html = MailClient.Core.Rendering.MessageSignature.Html(_vm.SelectedSession.Settings);
        if (html.Length == 0)
        {
            EditSignature_Click(sender, e);
            return;
        }
        await Editor.SetSignatureAsync(html);
    }

    private async void RemoveSignature_Click(object sender, RoutedEventArgs e) => await Editor.SetSignatureAsync("");

    /// <summary>Changes the signature of the sending account (saved in its settings) and puts the new one into this message.</summary>
    private async void EditSignature_Click(object sender, RoutedEventArgs e)
    {
        var session = _vm.SelectedSession;
        var copy = session.Settings.Clone();
        if (WindowFactory.EditSignature(copy, session) != true) return;
        foreach (var account in _settings.Accounts.Where(a => a.Id == copy.Id).Append(session.Settings).Distinct())
            account.CopySignatureFrom(copy);
        SettingsStore.Save(_settings);
        await Editor.SetSignatureAsync(copy.SignatureHtml);
    }

    // ------------------------------------------------------------------ recipient autocomplete

    private static (int start, string token) CurrentToken(TextBox box)
    {
        var text = box.Text;
        var caret = Math.Min(box.CaretIndex, text.Length);
        int start = text.LastIndexOfAny(RecipientSeparators, Math.Max(0, caret - 1)) + 1;
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
        var cts = _suggestCts = new CancellationTokenSource();
        var results = await _vm.SuggestAsync(token, cts.Token);
        if (cts.IsCancellationRequested || box != _suggestTarget) return;
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
