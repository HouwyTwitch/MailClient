using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Rendering;

namespace MailClient.App.ViewModels;

public sealed class ComposeAttachment
{
    public required string Name { get; init; }
    public required byte[] Content { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public bool IsInline { get; init; }
    public string ContentId { get; init; } = "";
    public string SizeText => RuText.Size(Content.Length);
}

public sealed partial class ComposeViewModel : ObservableObject
{
    /// <summary>Default Exchange limit is 25–35 MB; base64 encoding adds ~33 %.</summary>
    private const long WarnTotalBytes = 20L * 1024 * 1024;
    private const long MaxFileBytes = 150L * 1024 * 1024;

    public ComposeViewModel(IReadOnlyList<AccountSession> sessions, AccountSession session)
    {
        Sessions = sessions;
        _selectedSession = session;
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsSummary));
            IsDirty = true;
        };
    }

    public IReadOnlyList<AccountSession> Sessions { get; }
    public bool HasMultipleAccounts => Sessions.Count > 1;

    [ObservableProperty] private AccountSession _selectedSession;
    [ObservableProperty] private ComposeAction _action = ComposeAction.New;
    [ObservableProperty] private string? _referenceItemId;

    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private bool _showBcc;
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private bool _highImportance;
    [ObservableProperty] private bool _requestReadReceipt;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isDirty;

    /// <summary>Draft saved from a reply/forward (replaced on the next save, removed after sending).</summary>
    private string? _responseDraftId;

    /// <summary>Initial HTML loaded into the editor.</summary>
    public string InitialHtml { get; set; } = "";

    /// <summary>Shown under the editor for replies/forwards (the server appends the original).</summary>
    public string QuoteNote { get; set; } = "";
    public bool HasQuoteNote => QuoteNote.Length > 0;

    public ObservableCollection<ComposeAttachment> Attachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0;
    public string AttachmentsSummary => Attachments.Count == 0 ? "" :
        $"{RuText.Count(Attachments.Count, "вложение", "вложения", "вложений")}, {RuText.Size(Attachments.Sum(a => (long)a.Content.Length))}";

    public string WindowTitle => (string.IsNullOrWhiteSpace(Subject) ? "Новое письмо" : Subject) + " — Корпоративная почта";

    /// <summary>Supplied by the view: returns the editor content (HTML or plain text) and whether it is HTML.</summary>
    public Func<Task<(string body, bool isHtml)>>? GetBody { get; set; }

    /// <summary>Raised when the window should close after a successful send/save.</summary>
    public event EventHandler? CloseRequested;

    partial void OnSubjectChanged(string value)
    {
        IsDirty = true;
        OnPropertyChanged(nameof(WindowTitle));
    }
    partial void OnToChanged(string value) => IsDirty = true;
    partial void OnCcChanged(string value) => IsDirty = true;
    partial void OnBccChanged(string value) => IsDirty = true;

    // ------------------------------------------------------------------ factories

    private static string SignatureHtml(AccountSession s) =>
        string.IsNullOrWhiteSpace(s.Settings.Signature)
            ? ""
            : "<p><br></p><div class=\"signature\">-- <br>" +
              WebUtility.HtmlEncode(s.Settings.Signature).Replace("\r\n", "<br>").Replace("\n", "<br>") + "</div>";

    public static ComposeViewModel New(IReadOnlyList<AccountSession> sessions, AccountSession session)
    {
        var vm = new ComposeViewModel(sessions, session) { InitialHtml = "<p><br></p>" + SignatureHtml(session) };
        vm.IsDirty = false;
        return vm;
    }

    public static ComposeViewModel ForResponse(IReadOnlyList<AccountSession> sessions, AccountSession session, MailMessage original, ComposeAction action)
    {
        var vm = new ComposeViewModel(sessions, session)
        {
            Action = action,
            ReferenceItemId = original.Id,
            InitialHtml = "<p><br></p>" + SignatureHtml(session),
        };
        var me = (string.IsNullOrWhiteSpace(session.Settings.SharedMailbox) ? session.Settings.EmailAddress : session.Settings.SharedMailbox)
            .Trim();
        bool NotMe(EmailAddress a) => !string.Equals(a.Address, me, StringComparison.OrdinalIgnoreCase);

        switch (action)
        {
            case ComposeAction.Reply:
            case ComposeAction.ReplyAll:
            {
                var primary = original.ReplyTo.Count > 0 ? original.ReplyTo : original.From != null ? new List<EmailAddress> { original.From } : new();
                // Replying to a message I sent: address the original recipients instead.
                if (primary.All(a => !NotMe(a))) primary = original.To;
                var to = primary.ToList();
                var cc = new List<EmailAddress>();
                if (action == ComposeAction.ReplyAll)
                {
                    to.AddRange(original.To.Where(NotMe));
                    cc.AddRange(original.Cc.Where(NotMe));
                }
                to = to.DistinctBy(a => a.Address.ToLowerInvariant()).ToList();
                cc = cc.Where(c => to.All(t => !t.Address.Equals(c.Address, StringComparison.OrdinalIgnoreCase)))
                       .DistinctBy(a => a.Address.ToLowerInvariant()).ToList();
                vm.To = EmailAddress.FormatList(to);
                vm.Cc = EmailAddress.FormatList(cc);
                vm.Subject = RuText.PrefixSubject(original.Subject, "RE:");
                vm.QuoteNote = "Исходное письмо будет добавлено в ответ автоматически.";
                break;
            }
            case ComposeAction.Forward:
                vm.Subject = RuText.PrefixSubject(original.Subject, "FW:");
                vm.QuoteNote = original.Attachments.Any(a => !a.IsInline)
                    ? "Исходное письмо и его вложения будут добавлены автоматически."
                    : "Исходное письмо будет добавлено автоматически.";
                break;
        }
        vm.IsDirty = false;
        return vm;
    }

    /// <summary>Opens an existing draft for editing (attachments are downloaded so nothing is lost).</summary>
    public static async Task<ComposeViewModel> FromDraftAsync(IReadOnlyList<AccountSession> sessions, AccountSession session, string draftId)
    {
        var draft = await session.Provider.GetMessageAsync(draftId);
        var vm = new ComposeViewModel(sessions, session)
        {
            Action = ComposeAction.EditDraft,
            ReferenceItemId = draft.Id,
            To = EmailAddress.FormatList(draft.To),
            Cc = EmailAddress.FormatList(draft.Cc),
            Bcc = EmailAddress.FormatList(draft.Bcc),
            ShowBcc = draft.Bcc.Count > 0,
            Subject = draft.Subject,
            HighImportance = draft.Importance == Importance.High,
            RequestReadReceipt = draft.IsReadReceiptRequested,
        };
        var body = draft.BodyIsHtml ? draft.Body : MessageHtmlBuilder.TextToHtml(draft.Body);
        if (draft.Attachments.Count > 0)
        {
            var files = await session.Provider.GetAttachmentsAsync(draft.Attachments.Where(a => !a.IsItemAttachment).Select(a => a.Id));
            foreach (var f in files)
            {
                // Inline images go back into the editor as data: URIs; they are re-extracted on send.
                if (f.Info.IsInline && f.Info.ContentId.Length > 0 && body.Contains("cid:" + f.Info.ContentId, StringComparison.OrdinalIgnoreCase))
                {
                    body = body.Replace("cid:" + f.Info.ContentId, $"data:{f.Info.ContentType};base64,{Convert.ToBase64String(f.Content)}",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                vm.Attachments.Add(new ComposeAttachment { Name = f.Info.Name, Content = f.Content, ContentType = f.Info.ContentType });
            }
        }
        vm.InitialHtml = body;
        vm.IsDirty = false;
        return vm;
    }

    /// <summary>Creates a message from a mailto: URI (RFC 6068).</summary>
    public static ComposeViewModel FromMailto(IReadOnlyList<AccountSession> sessions, AccountSession session, string mailto)
    {
        var vm = New(sessions, session);
        try
        {
            var uri = new Uri(mailto);
            vm.To = Uri.UnescapeDataString(uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped)).Replace(',', ';');
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                switch (kv[0].ToLowerInvariant())
                {
                    case "subject": vm.Subject = value; break;
                    case "cc": vm.Cc = value.Replace(',', ';'); break;
                    case "bcc": vm.Bcc = value.Replace(',', ';'); vm.ShowBcc = true; break;
                    case "body": vm.InitialHtml = MessageHtmlBuilder.TextToHtml(value) + SignatureHtml(session); break;
                }
            }
        }
        catch (UriFormatException ex)
        {
            Log.Warn($"Некорректная ссылка mailto: {ex.Message}");
        }
        vm.IsDirty = false;
        return vm;
    }

    // ------------------------------------------------------------------ attachments

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Dialogs.Error($"«{Path.GetFileName(path)}» — это папка. Чтобы отправить папку, упакуйте её в ZIP-архив.");
                    continue;
                }
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                {
                    Dialogs.Error($"Файл «{info.Name}» слишком большой ({RuText.Size(info.Length)}). Используйте файловый ресурс или облачное хранилище организации.");
                    continue;
                }
                Attachments.Add(new ComposeAttachment
                {
                    Name = info.Name,
                    Content = File.ReadAllBytes(path),
                    ContentType = MimeTypes.FromFileName(info.Name),
                });
            }
            catch (Exception ex)
            {
                Dialogs.Error(ex, $"Не удалось прикрепить файл «{Path.GetFileName(path)}»");
            }
        }
    }

    [RelayCommand]
    private void RemoveAttachment(ComposeAttachment? a)
    {
        if (a != null) Attachments.Remove(a);
    }

    [RelayCommand]
    private void ToggleBcc() => ShowBcc = !ShowBcc;

    // ------------------------------------------------------------------ recipients

    /// <summary>Directory/contacts suggestions for the recipient autocomplete.</summary>
    public async Task<IReadOnlyList<Contact>> SuggestAsync(string text, CancellationToken ct)
    {
        if (text.Trim().Length < 2) return Array.Empty<Contact>();
        var result = new List<Contact>();
        try
        {
            result.AddRange((await SelectedSession.Provider.ResolveNamesAsync(text, ct)).Where(c => c.PrimaryEmail.Length > 0));
        }
        catch (OperationCanceledException)
        {
            // The user kept typing (or the window closed): a newer lookup supersedes this one.
            if (ct.IsCancellationRequested) return Array.Empty<Contact>();
        }
        catch (Exception ex)
        {
            Log.Warn($"Поиск в адресной книге не удался: {ex.Message}");
        }
        if (ct.IsCancellationRequested) return Array.Empty<Contact>();
        // Plus people from the user's own correspondence (the only source for IMAP accounts).
        var local = await Task.Run(() => SelectedSession.Cache.SuggestAddresses(text, 10));
        foreach (var a in local)
        {
            if (result.Any(c => c.EmailAddresses.Contains(a.Address, StringComparer.OrdinalIgnoreCase))) continue;
            result.Add(new Contact { DisplayName = a.ShortName, EmailAddresses = { a.Address } });
        }
        return result.Take(12).ToList();
    }

    /// <summary>
    /// Parses a recipient field; entries without "@" (e.g. "Иванов") are resolved against the address book.
    /// Returns null and reports an error when a name cannot be resolved unambiguously.
    /// </summary>
    private async Task<List<EmailAddress>?> ResolveFieldAsync(string field, string label)
    {
        var result = new List<EmailAddress>();
        foreach (var entry in EmailAddress.ParseList(field))
        {
            if (EmailAddress.LooksValid(entry.Address))
            {
                result.Add(entry);
                continue;
            }
            var query = string.IsNullOrWhiteSpace(entry.Address) ? entry.Name : entry.Address;
            var matches = (await SelectedSession.Provider.ResolveNamesAsync(query)).Where(c => c.PrimaryEmail.Length > 0).ToList();
            if (matches.Count == 0)
                matches = SelectedSession.Cache.SuggestAddresses(query, 5)
                    .Select(a => new Contact { DisplayName = a.ShortName, EmailAddresses = { a.Address } }).ToList();
            if (matches.Count == 1)
            {
                result.Add(new EmailAddress(matches[0].DisplayName, matches[0].PrimaryEmail));
            }
            else
            {
                Dialogs.Error(matches.Count == 0
                    ? $"Поле «{label}»: получатель «{query}» не найден в адресной книге."
                    : $"Поле «{label}»: имени «{query}» соответствует несколько получателей ({matches.Count}). Уточните имя или укажите адрес.");
                return null;
            }
        }
        return result;
    }

    [RelayCommand]
    private async Task CheckNamesAsync()
    {
        IsBusy = true;
        StatusText = "Проверка имён…";
        try
        {
            var to = await ResolveFieldAsync(To, "Кому");
            if (to == null) return;
            var cc = await ResolveFieldAsync(Cc, "Копия");
            if (cc == null) return;
            var bcc = await ResolveFieldAsync(Bcc, "Скрытая копия");
            if (bcc == null) return;
            To = EmailAddress.FormatList(to);
            Cc = EmailAddress.FormatList(cc);
            Bcc = EmailAddress.FormatList(bcc);
            StatusText = "Все имена распознаны.";
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось проверить имена");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ send / save

    private async Task<OutgoingMessage?> BuildAsync(bool forSending)
    {
        var to = await ResolveFieldAsync(To, "Кому");
        if (to == null) return null;
        var cc = await ResolveFieldAsync(Cc, "Копия");
        if (cc == null) return null;
        var bcc = await ResolveFieldAsync(Bcc, "Скрытая копия");
        if (bcc == null) return null;

        if (forSending && to.Count + cc.Count + bcc.Count == 0)
        {
            Dialogs.Error("Укажите хотя бы одного получателя.");
            return null;
        }
        if (forSending && string.IsNullOrWhiteSpace(Subject) &&
            !Dialogs.Confirm("У письма нет темы. Отправить без темы?"))
            return null;

        var (body, isHtml) = GetBody != null ? await GetBody() : ("", true);
        var message = new OutgoingMessage
        {
            Action = Action,
            ReferenceItemId = ReferenceItemId,
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = Subject.Trim(),
            BodyIsHtml = isHtml,
            Importance = HighImportance ? Importance.High : Importance.Normal,
            RequestReadReceipt = RequestReadReceipt,
        };
        if (isHtml)
        {
            var (html, images) = InlineImageExtractor.Extract(body);
            // Replies/forwards: the server merges this fragment with the quoted original, so send a fragment only.
            message.Body = Action is ComposeAction.Reply or ComposeAction.ReplyAll or ComposeAction.Forward
                ? $"<div style=\"font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:11pt\">{html}</div>"
                : $"<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:11pt\">{html}</body></html>";
            message.Attachments.AddRange(images);
        }
        else
        {
            message.Body = body;
        }
        message.Attachments.AddRange(Attachments.Select(a => new OutgoingAttachment
        {
            Name = a.Name, Content = a.Content, ContentType = a.ContentType, IsInline = a.IsInline, ContentId = a.ContentId,
        }));

        var total = message.Attachments.Sum(a => (long)a.Content.Length);
        if (forSending && total > WarnTotalBytes &&
            !Dialogs.Confirm($"Общий размер вложений — {RuText.Size(total)}. Сервер Exchange может отклонить такое письмо " +
                             "(обычно ограничение 25–35 МБ). Всё равно отправить?"))
            return null;
        return message;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Отправка…";
        try
        {
            var message = await BuildAsync(forSending: true);
            if (message == null) return;
            await SelectedSession.Provider.SendAsync(message);
            await DeleteResponseDraftAsync();
            Log.Info($"Письмо отправлено ({message.To.Count + message.Cc.Count + message.Bcc.Count} получ., {message.Attachments.Count} влож.)");
            IsDirty = false;
            SelectedSession.SyncNow();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка отправки", ex);
            Dialogs.Error(ex, "Письмо не отправлено");
        }
        finally
        {
            IsBusy = false;
            StatusText = "";
        }
    }

    private async Task DeleteResponseDraftAsync()
    {
        if (_responseDraftId == null) return;
        try
        {
            await SelectedSession.Provider.DeleteItemsAsync(new[] { _responseDraftId }, permanent: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Предыдущий черновик ответа не удалён: {ex.Message}");
        }
        _responseDraftId = null;
    }

    [RelayCommand]
    private async Task SaveDraftAsync() => await SaveDraftCoreAsync(closeAfter: false);

    public async Task<bool> SaveDraftCoreAsync(bool closeAfter)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusText = "Сохранение черновика…";
        try
        {
            var message = await BuildAsync(forSending: false);
            if (message == null) return false;
            var id = await SelectedSession.Provider.SaveDraftAsync(message);
            // Subsequent saves replace this draft.
            if (Action == ComposeAction.New || Action == ComposeAction.EditDraft)
            {
                Action = ComposeAction.EditDraft;
                ReferenceItemId = id;
            }
            else
            {
                await DeleteResponseDraftAsync();
                _responseDraftId = id;
            }
            IsDirty = false;
            StatusText = $"Черновик сохранён в {DateTime.Now:HH:mm}";
            SelectedSession.SyncNow();
            if (closeAfter) CloseRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить черновик");
            StatusText = "";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
