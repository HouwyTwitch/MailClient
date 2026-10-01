using System.Windows;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.Views;

public partial class ContactWindow : Window
{
    private readonly Contact _c;

    public ContactWindow(Contact contact, bool isNew)
    {
        InitializeComponent();
        _c = contact;
        Title = isNew ? "Новый контакт" : "Изменение контакта";
        SurnameBox.Text = contact.Surname;
        GivenBox.Text = contact.GivenName;
        DisplayBox.Text = contact.DisplayName;
        CompanyBox.Text = contact.CompanyName;
        TitleBox.Text = contact.JobTitle;
        DeptBox.Text = contact.Department;
        EmailBox.Text = string.Join("; ", contact.EmailAddresses);
        WorkPhoneBox.Text = contact.BusinessPhone;
        MobileBox.Text = contact.MobilePhone;
        HomePhoneBox.Text = contact.HomePhone;
        NotesBox.Text = contact.Notes;
        Loaded += (_, _) => SurnameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var emails = EmailBox.Text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (emails.Count > 3)
        {
            Dialogs.Error("Exchange позволяет хранить не более трёх адресов электронной почты у контакта.");
            return;
        }
        if (emails.FirstOrDefault(a => !EmailAddress.LooksValid(a)) is { } bad)
        {
            Dialogs.Error($"Некорректный адрес: «{bad}».");
            return;
        }
        var display = DisplayBox.Text.Trim();
        if (display.Length == 0) display = $"{SurnameBox.Text.Trim()} {GivenBox.Text.Trim()}".Trim();
        if (display.Length == 0 && emails.Count == 0)
        {
            Dialogs.Error("Укажите имя или адрес электронной почты.");
            return;
        }
        _c.Surname = SurnameBox.Text.Trim();
        _c.GivenName = GivenBox.Text.Trim();
        _c.DisplayName = display.Length > 0 ? display : emails[0];
        _c.CompanyName = CompanyBox.Text.Trim();
        _c.JobTitle = TitleBox.Text.Trim();
        _c.Department = DeptBox.Text.Trim();
        _c.EmailAddresses = emails;
        _c.BusinessPhone = WorkPhoneBox.Text.Trim();
        _c.MobilePhone = MobileBox.Text.Trim();
        _c.HomePhone = HomePhoneBox.Text.Trim();
        _c.Notes = NotesBox.Text;
        DialogResult = true;
    }
}
