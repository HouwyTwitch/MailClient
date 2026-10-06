using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

/// <summary>Forwarding rules of a mailbox: list, order, on/off; saved to the server in one go.</summary>
public partial class RulesWindow : Window
{
    private readonly AccountSession _session;
    private readonly ObservableCollection<RuleItemViewModel> _items = new();
    private ForwardingRuleSet? _set;
    private bool _dirty;
    private bool _saved;

    public RulesWindow(AccountSession session)
    {
        InitializeComponent();
        _session = session;
        Title = $"Правила пересылки — {session.Settings.EmailAddress}";
        List.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateState();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _set = await _session.Provider.GetForwardingRulesAsync();
            foreach (var r in _set.Rules) Add(new RuleItemViewModel(r));
            if (_set.Note.Length > 0)
            {
                NoteText.Text = _set.Note;
                NoteBanner.Visibility = Visibility.Visible;
            }
            Toolbar.IsEnabled = true;
            _dirty = false;
            UpdateState();
            if (_items.Count > 0) List.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            EmptyText.Text = "Не удалось загрузить правила.\n\n" + RuText.Error(ex);
        }
    }

    private void Add(RuleItemViewModel item, int index = -1)
    {
        item.Changed += (_, _) => MarkDirty();
        if (index < 0) _items.Add(item); else _items.Insert(index, item);
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateState();
    }

    private RuleItemViewModel? Selected => List.SelectedItem as RuleItemViewModel;

    private void UpdateState()
    {
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_set != null && _items.Count == 0) EmptyText.Text = "Правил пока нет. Нажмите «Новое правило», чтобы пересылать письма на другой адрес.";
        var i = List.SelectedIndex;
        EditButton.IsEnabled = DeleteButton.IsEnabled = i >= 0;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _items.Count - 1;
        SaveButton.IsEnabled = _set != null && _dirty;
        StatusText.Text = _dirty ? "Изменения ещё не сохранены на сервере." : "";
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateState();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement(List, d) is System.Windows.Controls.ListBoxItem) Edit_Click(sender, e);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) Delete_Click(sender, e);
        else if (e.Key == Key.Enter) Edit_Click(sender, e);
        else return;
        e.Handled = true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_set == null) return;
        var rule = new ForwardingRule { Mode = _set.SupportedModes.Contains(ForwardingMode.Redirect) ? ForwardingMode.Redirect : _set.SupportedModes[0] };
        var dlg = new RuleEditWindow(rule, _set, isNew: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Add(new RuleItemViewModel(dlg.Result));
        List.SelectedIndex = _items.Count - 1;
        List.ScrollIntoView(List.SelectedItem);
        MarkDirty();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_set == null || Selected is not { } item) return;
        if (!item.IsEditable)
        {
            // Rules made elsewhere: only the name can be changed here.
            var name = WindowFactory.Prompt("Переименовать правило", "Название правила:", item.Rule.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Rule.Name) return;
            var renamed = item.Rule.Clone();
            renamed.Name = name.Trim();
            item.Replace(renamed);
            MarkDirty();
            return;
        }
        var dlg = new RuleEditWindow(item.Rule.Clone(), _set, isNew: false) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        item.Replace(dlg.Result);
        MarkDirty();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (!Dialogs.Confirm($"Удалить правило «{item.Name}»?")) return;
        var index = List.SelectedIndex;
        _items.Remove(item);
        if (_items.Count > 0) List.SelectedIndex = Math.Min(index, _items.Count - 1);
        MarkDirty();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        var i = List.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        List.SelectedIndex = j;
        MarkDirty();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_set == null) return;
        if (_set.OutlookRulesPresent &&
            !Dialogs.Confirm("Правила этого ящика также настраивались в Outlook. После сохранения Outlook получит обновлённый список, " +
                             "а правила, которые работают только в Outlook на компьютере (например, с действиями на этом компьютере), " +
                             "могут быть отключены.\n\nСохранить правила?", "Правила Outlook"))
            return;
        IsEnabled = false;
        StatusText.Text = "Сохранение на сервере…";
        try
        {
            await _session.Provider.SaveForwardingRulesAsync(_items.Select(i => i.Rule).ToList(), replaceOutlookRules: true);
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Dialogs.Error(ex, "Не удалось сохранить правила");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty && !_saved && !Dialogs.Confirm("Изменения правил не сохранены. Закрыть без сохранения?"))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
