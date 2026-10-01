using System.Windows;
using MailClient.App.ViewModels;

namespace MailClient.App.Views;

public partial class FolderPickerWindow : Window
{
    private readonly bool _allowRoot;

    public FolderPickerWindow(IEnumerable<FolderNodeViewModel> roots, string title, bool allowRoot)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _allowRoot = allowRoot;
        Tree.ItemsSource = roots.ToList();
    }

    public FolderNodeViewModel? Selected => Tree.SelectedItem as FolderNodeViewModel;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || (Selected.IsAccountRoot && !_allowRoot))
        {
            Services.Dialogs.Error("Выберите папку.");
            return;
        }
        DialogResult = true;
    }

    private void Tree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected != null && (!Selected.IsAccountRoot || _allowRoot)) DialogResult = true;
    }
}
