using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.App.Views;
using MailClient.Core.Models;
using MailClient.Core.Services;

namespace MailClient.App.ViewModels;

/// <summary>A sender or recipient in the message header: shown by name, the address is in the contact card.</summary>
public sealed record RecipientItem(EmailAddress Address, string Separator)
{
    public string Name => string.IsNullOrWhiteSpace(Address.Name) ? Address.Address : Address.Name;
    public string ToolTip => Address.Address.Length > 0 ? Address.Address : Address.Name;

    public static IReadOnlyList<RecipientItem> List(IReadOnlyList<EmailAddress> addresses) =>
        addresses.Select((a, i) => new RecipientItem(a, i < addresses.Count - 1 ? ";" : "")).ToList();
}

/// <summary>
/// Contact card opened by clicking a name: the e-mail address plus whatever the organization address book
/// knows (title, department, phones).
/// </summary>
public sealed partial class ContactCardViewModel : ObservableObject
{
    /// <summary>Directory lookups of this run, so reopening a card is instant and does not load the server.</summary>
    private static readonly ConcurrentDictionary<string, Contact?> DirectoryCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly AccountSession _session;
    private readonly Action<EmailAddress> _writeTo;
    private Contact? _contact;

    public ContactCardViewModel(AccountSession session, EmailAddress address, Action<EmailAddress> writeTo)
    {
        _session = session;
        _writeTo = writeTo;
        Address = address;
        _displayName = string.IsNullOrWhiteSpace(address.Name) ? address.Address : address.Name;
    }

    public EmailAddress Address { get; }
    public string Email => Address.Address;
    public bool HasEmail => EmailAddress.LooksValid(Email);
    public string Initials => RuText.Initials(DisplayName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob))]
    private string _jobText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompany))]
    private string _company = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBusinessPhone))]
    private string _businessPhone = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMobilePhone))]
    private string _mobilePhone = "";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canAddToContacts;

    public bool HasJob => JobText.Length > 0;
    public bool HasCompany => Company.Length > 0;
    public bool HasBusinessPhone => BusinessPhone.Length > 0;
    public bool HasMobilePhone => MobilePhone.Length > 0;

    public async Task LoadAsync()
    {
        var caps = _session.Provider.Capabilities;
        CanAddToContacts = HasEmail && caps.HasFlag(ProviderCapabilities.Contacts);
        if (!HasEmail || !caps.HasFlag(ProviderCapabilities.Directory)) return;

        var key = _session.Settings.EmailAddress + "|" + Email;
        if (!DirectoryCache.TryGetValue(key, out var contact))
        {
            IsLoading = true;
            try
            {
                var found = await _session.Provider.ResolveNamesAsync(Email);
                contact = found.FirstOrDefault(c => c.EmailAddresses.Contains(Email, StringComparer.OrdinalIgnoreCase));
                DirectoryCache[key] = contact;
            }
            catch (Exception ex)
            {
                // The card still shows the name and address; details are a bonus.
                Log.Warn($"Сведения о «{Email}» не получены: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }
        if (contact != null) Apply(contact);
    }

    private void Apply(Contact c)
    {
        _contact = c;
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) DisplayName = c.DisplayName;
        JobText = string.Join(" · ", new[] { c.JobTitle, c.Department }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Company = c.CompanyName;
        BusinessPhone = c.BusinessPhone;
        MobilePhone = c.MobilePhone;
        // Personal contacts are already saved; directory entries can be copied into the contacts folder.
        CanAddToContacts &= c.IsDirectoryEntry;
    }

    [RelayCommand]
    private void Write() => _writeTo(new EmailAddress(DisplayName == Email ? "" : DisplayName, Email));

    [RelayCommand]
    private void CopyAddress() =>
        DesktopIntegration.CopyText(Address.Name.Length > 0 && Address.Name != Email ? $"{Address.Name} <{Email}>" : Email);

    [RelayCommand]
    private async Task AddToContactsAsync()
    {
        var contact = _contact is { } known
            ? new Contact
            {
                DisplayName = known.DisplayName, GivenName = known.GivenName, Surname = known.Surname,
                CompanyName = known.CompanyName, JobTitle = known.JobTitle, Department = known.Department,
                EmailAddresses = known.EmailAddresses.ToList(), BusinessPhone = known.BusinessPhone,
                MobilePhone = known.MobilePhone, HomePhone = known.HomePhone,
            }
            : new Contact { DisplayName = DisplayName == Email ? "" : DisplayName, EmailAddresses = [Email] };
        if (WindowFactory.EditContact(contact, isNew: true) != true) return;
        try
        {
            await _session.Provider.CreateContactAsync(contact);
            CanAddToContacts = false;
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить контакт");
        }
    }
}
