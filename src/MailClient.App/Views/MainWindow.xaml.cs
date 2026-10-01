using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailClient.App.Services;
using MailClient.App.ViewModels;

namespace MailClient.App.Views;

public partial class MainWindow : Window
{
    private const string DragFormat = "MailClient.Messages";
    private readonly MainViewModel _vm;
    private Point _dragStart;
    private bool _exiting;
    private FolderNodeViewModel? _dropTarget;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        var s = vm.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        FolderColumn.Width = new GridLength(Math.Clamp(s.FolderPaneWidth, 170, 500));
        ListColumn.Width = new GridLength(Math.Clamp(s.MessageListWidth, 260, 900));

        InputBindings.Add(new KeyBinding(new RelayAction(() =>
        {
            vm.Section = AppSection.Mail;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }), new KeyGesture(Key.E, ModifierKeys.Control)));
    }

    // ------------------------------------------------------------------ window lifecycle

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveLayout();
        if (!_exiting && _vm.Settings.MinimizeToTray)
        {
            // Keep running in the notification area to receive new mail notifications.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Windows logoff/shutdown: close without minimizing to the tray.</summary>
    public void AllowClose() => _exiting = true;

    public void ExitApplication()
    {
        var unsaved = Application.Current.Windows.OfType<ComposeWindow>().FirstOrDefault(w => w.HasUnsavedChanges);
        if (unsaved != null)
        {
            unsaved.Activate();
            Dialogs.Info("Есть неотправленное письмо. Отправьте его, сохраните черновик или закройте окно письма, затем повторите выход.");
            return;
        }
        foreach (var w in Application.Current.Windows.OfType<ComposeWindow>().ToList()) w.Close();
        _exiting = true;
        Close();
    }

    private void SaveLayout()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
        }
        s.FolderPaneWidth = FolderColumn.ActualWidth;
        s.MessageListWidth = ListColumn.ActualWidth;
        SettingsStore.Save(s);
    }

    // ------------------------------------------------------------------ folder tree

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node) _vm.SelectedFolder = node;
    }

    /// <summary>Right-click selects the folder first, so context menu commands act on it.</summary>
    private void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not TreeViewItem) element = VisualTreeHelper.GetParent(element);
        if (element is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private FolderNodeViewModel? NodeAt(DragEventArgs e)
    {
        var element = FolderTree.InputHitTest(e.GetPosition(FolderTree)) as DependencyObject;
        while (element != null && element is not TreeViewItem) element = VisualTreeHelper.GetParent(element);
        return (element as TreeViewItem)?.DataContext as FolderNodeViewModel;
    }

    private void SetDropTarget(FolderNodeViewModel? node)
    {
        if (_dropTarget == node) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = node;
        if (node != null) node.IsDropTarget = true;
    }

    private void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        var node = NodeAt(e);
        bool messages = e.Data.GetDataPresent(DragFormat);
        bool emlFiles = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                        ((string[])e.Data.GetData(DataFormats.FileDrop)).Any(f => f.EndsWith(".eml", StringComparison.OrdinalIgnoreCase));
        if (node == null || node.IsAccountRoot || !(messages || emlFiles))
        {
            e.Effects = DragDropEffects.None;
            SetDropTarget(null);
        }
        else
        {
            e.Effects = emlFiles || (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            SetDropTarget(node);
        }
        e.Handled = true;
    }

    private void FolderTree_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private async void FolderTree_Drop(object sender, DragEventArgs e)
    {
        var node = NodeAt(e);
        SetDropTarget(null);
        if (node == null || node.IsAccountRoot) return;

        if (e.Data.GetDataPresent(DragFormat))
        {
            await _vm.DropOnFolderAsync(node, copy: (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // .eml files from Explorer: import into the folder.
            int ok = 0;
            foreach (var file in ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(f => f.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await node.Session.Provider.ImportMimeAsync(node.Id, await File.ReadAllBytesAsync(file));
                    ok++;
                }
                catch (Exception ex)
                {
                    Dialogs.Error(ex, $"Не удалось импортировать «{Path.GetFileName(file)}»");
                }
            }
            _vm.StatusText = $"Импортировано в «{node.Name}»: {RuText.Count(ok, "письмо", "письма", "писем")}";
            node.Session.SyncNow();
        }
    }

    // ------------------------------------------------------------------ message list

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedMessages.Clear();
        _vm.SelectedMessages.AddRange(MessageList.SelectedItems.OfType<MessageItemViewModel>());
    }

    private void MessageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void MessageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || MessageList.SelectedItems.Count == 0) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        // Only start dragging from an item, not from the scroll bar.
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) == null) return;
        var data = new DataObject(DragFormat, MessageList.SelectedItems.OfType<MessageItemViewModel>().Select(m => m.Id).ToArray());
        DragDrop.DoDragDrop(MessageList, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void MessageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
            _vm.OpenSelectedCommand.Execute(null);
    }

    private void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 200)
            _vm.LoadMoreCommand.Execute(null);
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.HasPreview) await BodyView.PrintAsync();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

/// <summary>Tiny ICommand wrapper for code-behind key bindings.</summary>
public sealed class RelayAction : ICommand
{
    private readonly Action _action;
    public RelayAction(Action action) => _action = action;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
}
