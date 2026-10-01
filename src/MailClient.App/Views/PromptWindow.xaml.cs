using System.Windows;

namespace MailClient.App.Views;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
