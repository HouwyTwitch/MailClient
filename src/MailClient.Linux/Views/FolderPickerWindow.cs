using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using MailClient.App.Services;
using MailClient.App.ViewModels;

namespace MailClient.Linux.Views;

/// <summary>Chooses a target folder (move messages).</summary>
public sealed class FolderPickerWindow : Window
{
    private readonly TreeView _tree;
    private FolderNodeViewModel? _picked;

    private FolderPickerWindow(IEnumerable<FolderNodeViewModel> roots, string title)
    {
        Title = title;
        Width = 420;
        Height = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _tree = new TreeView
        {
            ItemsSource = roots.ToList(),
            ItemTemplate = new FuncTreeDataTemplate<FolderNodeViewModel>(
                (node, _) => new TextBlock { Text = node.Name },
                node => node.Children),
        };
        _tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem item) item.IsExpanded = true;
        };
        _tree.DoubleTapped += (_, _) => Accept();
        var ok = new Button { Content = "Выбрать", MinWidth = 100, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(16), Children = { buttons, _tree } };
    }

    private void Accept()
    {
        if (_tree.SelectedItem is FolderNodeViewModel { IsAccountRoot: false } node)
        {
            _picked = node;
            Close();
        }
    }

    public static FolderNodeViewModel? Pick(IEnumerable<FolderNodeViewModel> roots, string title)
    {
        var window = new FolderPickerWindow(roots, title);
        return Dialogs.ShowModal(window, () => window._picked);
    }
}
