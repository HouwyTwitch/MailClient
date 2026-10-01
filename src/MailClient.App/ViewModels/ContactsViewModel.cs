using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

public sealed class ContactItemViewModel
{
    public ContactItemViewModel(Contact c) => Contact = c;
    public Contact Contact { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Contact.DisplayName) ? Contact.PrimaryEmail : Contact.DisplayName;
    public string Subtitle => string.Join(" · ", new[] { Contact.JobTitle, Contact.CompanyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Initials
    {
        get
        {
            var parts = DisplayName.Split(new[] { ' ', '.', '@' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant(),
            };
        }
    }
    public string Emails => string.Join("\n", Contact.EmailAddresses);
    public string Phones => string.Join("\n", new[]
    {
        Contact.BusinessPhone.Length > 0 ? "Рабочий: " + Contact.BusinessPhone : "",
        Contact.MobilePhone.Length > 0 ? "Мобильный: " + Contact.MobilePhone : "",
        Contact.HomePhone.Length > 0 ? "Домашний: " + Contact.HomePhone : "",
    }.Where(s => s.Length > 0));
    public string Department => Contact.Department;
    public bool IsDirectory => Contact.IsDirectoryEntry;
    public string SourceText => Contact.IsDirectoryEntry ? "Адресная книга организации" : "Мои контакты";
}

public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly Func<AccountSession?> _session;
    private readonly Action<EmailAddress> _writeTo;
    private List<ContactItemViewModel> _all = new();

    public ContactsViewModel(Func<AccountSession?> session, Action<EmailAddress> writeTo)
    {
        _session = session;
        _writeTo = writeTo;
    }

    public ObservableCollection<ContactItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanEdit))]
    private ContactItemViewModel? _selected;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    public bool HasSelection => Selected != null;
    public bool CanEdit => Selected != null && !Selected.IsDirectory;

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        Items.Clear();
        foreach (var c in _all.Where(c => f.Length == 0
                     || c.DisplayName.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                     || c.Contact.EmailAddresses.Any(e => e.Contains(f, StringComparison.OrdinalIgnoreCase))
                     || c.Contact.CompanyName.Contains(f, StringComparison.CurrentCultureIgnoreCase)))
            Items.Add(c);
        StatusText = RuText.Count(Items.Count, "контакт", "контакта", "контактов");
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var session = _session();
        if (session == null) return;
        IsBusy = true;
        try
        {
            var contacts = await session.Provider.GetContactsAsync();
            _all = contacts.Select(c => new ContactItemViewModel(c)).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = "Контакты недоступны: " + RuText.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Searches the organization's Global Address List (Active Directory).</summary>
    [RelayCommand]
    private async Task SearchDirectoryAsync()
    {
        var session = _session();
        if (session == null) return;
        if (Filter.Trim().Length < 2)
        {
            Dialogs.Info("Введите не менее двух символов имени, фамилии или адреса для поиска в адресной книге организации.");
            return;
        }
        IsBusy = true;
        try
        {
            var found = await session.Provider.ResolveNamesAsync(Filter.Trim());
            Items.Clear();
            foreach (var c in found) Items.Add(new ContactItemViewModel(c));
            StatusText = found.Count == 0 ? "В адресной книге ничего не найдено" :
                $"Адресная книга: {RuText.Count(found.Count, "результат", "результата", "результатов")}" +
                (found.Count >= 100 ? " (показаны первые 100, уточните запрос)" : "");
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Поиск в адресной книге не выполнен");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void WriteEmail(ContactItemViewModel? c)
    {
        c ??= Selected;
        if (c == null || c.Contact.PrimaryEmail.Length == 0) return;
        _writeTo(new EmailAddress(c.DisplayName, c.Contact.PrimaryEmail));
    }

    [RelayCommand]
    private async Task NewContactAsync()
    {
        var session = _session();
        if (session == null) return;
        var contact = new Contact();
        if (Views.WindowFactory.EditContact(contact, isNew: true) != true) return;
        try
        {
            await session.Provider.CreateContactAsync(contact);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось создать контакт");
        }
    }

    [RelayCommand]
    private async Task SaveToContactsAsync()
    {
        var session = _session();
        if (session == null || Selected is not { IsDirectory: true } s) return;
        try
        {
            var copy = s.Contact;
            copy.Id = "";
            await session.Provider.CreateContactAsync(copy);
            Dialogs.Info($"«{s.DisplayName}» добавлен в ваши контакты.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить контакт");
        }
    }

    [RelayCommand]
    private async Task EditContactAsync()
    {
        var session = _session();
        if (session == null || Selected is not { IsDirectory: false } s) return;
        if (Views.WindowFactory.EditContact(s.Contact, isNew: false) != true) return;
        try
        {
            await session.Provider.UpdateContactAsync(s.Contact);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить изменения контакта");
        }
    }

    [RelayCommand]
    private async Task DeleteContactAsync()
    {
        var session = _session();
        if (session == null || Selected is not { IsDirectory: false } s) return;
        if (!Dialogs.Confirm($"Удалить контакт «{s.DisplayName}»?")) return;
        try
        {
            await session.Provider.DeleteItemsAsync(new[] { s.Contact.Id }, permanent: false);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось удалить контакт");
        }
    }
}
