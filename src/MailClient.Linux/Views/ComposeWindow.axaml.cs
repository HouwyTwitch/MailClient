using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Linux.Controls;

namespace MailClient.Linux.Views;

/// <summary>New message, reply, forward or draft (the shared ComposeViewModel does the work).</summary>
public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private readonly AppSettings _settings;
    private bool _closeConfirmed;
    private bool _updatingFormat;

    public ComposeWindow() : this(null!, new AppSettings()) { }

    public ComposeWindow(ComposeViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        if (vm == null) return;
        vm.SignatureChangeRequested += async (_, html) => await Editor.SetSignatureAsync(html, onlyIfPresent: true);
        vm.GetBody = async () => (await Editor.GetContentAsync(), Editor.IsHtml);
        vm.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        FormatBar.IsVisible = Editor.IsHtml;
        FontCombo.ItemsSource = EditorFonts.Families;
        SizeCombo.ItemsSource = EditorFonts.Sizes.Select(s => s.ToString("0.#", CultureInfo.InvariantCulture)).ToList();
        _updatingFormat = true;
        FontCombo.SelectedItem = settings.ComposeFontFamily;
        SizeCombo.SelectedItem = settings.ComposeFontSize.ToString("0.#", CultureInfo.InvariantCulture);
        _updatingFormat = false;
        Editor.ContentChanged += (_, _) => vm.IsDirty = true;
        Editor.FormatStateChanged += (_, state) => ShowFormat(state);
        Opened += async (_, _) =>
        {
            var dirty = vm.IsDirty;
            await Editor.SetBaseFontAsync(settings.ComposeFontFamily, settings.ComposeFontSize);
            await Editor.SetHtmlAsync(vm.InitialHtml);
            vm.IsDirty = dirty;
            if (string.IsNullOrWhiteSpace(vm.To)) ToBox.Focus();
            else await Editor.FocusEditorAsync();
        };
    }

    public bool HasUnsavedChanges => _vm.IsDirty;

    private void ShowFormat(EditorFormatState state)
    {
        _updatingFormat = true;
        BoldButton.IsChecked = state.Bold;
        ItalicButton.IsChecked = state.Italic;
        UnderlineButton.IsChecked = state.Underline;
        if (EditorFonts.Families.Contains(state.FontFamily)) FontCombo.SelectedItem = state.FontFamily;
        SizeCombo.SelectedItem = state.FontSizePt.ToString("0.#", CultureInfo.InvariantCulture);
        _updatingFormat = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || _vm is not { IsDirty: true }) return;
        // Asked after the close is cancelled, so the question is not shown while the window is closing.
        e.Cancel = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var answer = Dialogs.YesNoCancel("Сохранить письмо в черновиках?", "Письмо не отправлено");
            if (answer == null) return;
            if (answer == true && !await _vm.SaveDraftCoreAsync(closeAfter: false)) return;
            _closeConfirmed = true;
            Close();
        });
    }

    private async void InsertSignature_Click(object? sender, RoutedEventArgs e)
    {
        var html = MailClient.Core.Rendering.MessageSignature.Html(_vm.SelectedSession.Settings);
        if (html.Length == 0)
        {
            EditSignature_Click(sender, e);
            return;
        }
        await Editor.SetSignatureAsync(html);
    }

    private async void RemoveSignature_Click(object? sender, RoutedEventArgs e) => await Editor.SetSignatureAsync("");

    /// <summary>Changes the signature of the sending account (saved in its settings) and puts the new one into this message.</summary>
    private async void EditSignature_Click(object? sender, RoutedEventArgs e)
    {
        await Task.Yield(); // let the menu close before the dialog opens
        var session = _vm.SelectedSession;
        var copy = session.Settings.Clone();
        if (!SignatureWindow.Edit(copy, session)) return;
        foreach (var account in _settings.Accounts.Where(a => a.Id == copy.Id).Append(session.Settings).Distinct())
            account.CopySignatureFrom(copy);
        SettingsStore.Save(_settings);
        await Editor.SetSignatureAsync(copy.SignatureHtml);
    }

    private void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var files = FileDialogs.OpenFiles("Вложить файлы");
        if (files.Count > 0) _vm.AddFiles(files);
    }

    private async void Format_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string command }) await Editor.ExecAsync(command);
        await Editor.FocusEditorAsync();
    }

    private async void FontCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingFormat || FontCombo.SelectedItem is not string family) return;
        await Editor.SetFontFamilyAsync(family);
    }

    private async void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingFormat || SizeCombo.SelectedItem is not string text ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)) return;
        await Editor.SetFontSizeAsync(size);
    }
}
