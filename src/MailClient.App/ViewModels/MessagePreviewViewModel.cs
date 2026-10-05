using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Rendering;
using Microsoft.Win32;

namespace MailClient.App.ViewModels;

public sealed partial class AttachmentItemViewModel : ObservableObject
{
    public AttachmentItemViewModel(AttachmentInfo info) => Info = info;
    public AttachmentInfo Info { get; }
    public string Name => Info.Name;
    public string SizeText => Info.Size > 0 ? RuText.Size(Info.Size) : "";
    public string Icon => Path.GetExtension(Info.Name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" => "\uEB9F",
        ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => "\uF012",
        ".eml" or ".msg" => "\uE715",
        ".ics" => "\uE787",
        _ => "\uE8A5",
    };
}

/// <summary>Reading pane: rendered body, attachments, meeting response actions.</summary>
public sealed partial class MessagePreviewViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly AppSettings _settings;
    private readonly IReadOnlyDictionary<string, (string, byte[])> _inline;

    private MessagePreviewViewModel(AccountSession session, AppSettings settings, MailMessage message,
        IReadOnlyDictionary<string, (string, byte[])> inline)
    {
        _session = session;
        _settings = settings;
        Message = message;
        _inline = inline;
        Attachments = new ObservableCollection<AttachmentItemViewModel>(
            message.Attachments.Where(a => !(a.IsInline && inline.ContainsKey(a.ContentId))).Select(a => new AttachmentItemViewModel(a)));
        HasRemoteContent = message.BodyIsHtml && RemoteRegex().IsMatch(message.Body);
        _allowRemote = settings.LoadRemoteImages ||
                       (message.From != null && settings.TrustedSenders.Contains(message.From.Address, StringComparer.OrdinalIgnoreCase));
        BuildHtml();
    }

    public static async Task<MessagePreviewViewModel> LoadAsync(AccountSession session, AppSettings settings, string itemId, CancellationToken ct)
    {
        var message = await session.Sync.GetMessageAsync(itemId, ct);
        var inline = new Dictionary<string, (string, byte[])>(StringComparer.OrdinalIgnoreCase);
        if (message.BodyIsHtml)
        {
            var referenced = message.Attachments
                .Where(a => !a.IsItemAttachment && a.ContentId.Length > 0 && message.Body.Contains("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (referenced.Count > 0)
            {
                try
                {
                    foreach (var a in await session.Provider.GetAttachmentsAsync(referenced.Select(r => r.Id), ct))
                    {
                        var cid = referenced.FirstOrDefault(r => r.Id == a.Info.Id)?.ContentId ?? a.Info.ContentId;
                        if (cid.Length > 0) inline[cid] = (a.Info.ContentType, a.Content);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Встроенные изображения не загружены: {ex.Message}");
                }
            }
        }
        return new MessagePreviewViewModel(session, settings, message, inline);
    }

    public MailMessage Message { get; }
    public AccountSession Session => _session;
    public ObservableCollection<AttachmentItemViewModel> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    public string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? "(без темы)" : Message.Subject;
    public string FromText => Message.From?.DisplayText ?? "(без отправителя)";
    public string FromInitials => RuText.Initials(Message.From?.ShortName ?? "");
    public string ToText => string.Join("; ", Message.To.Select(a => a.DisplayText));
    public string CcText => string.Join("; ", Message.Cc.Select(a => a.DisplayText));
    public bool HasCc => Message.Cc.Count > 0;
    public string DateText => RuText.FullDate(Message.DateSent == default ? Message.DateReceived : Message.DateSent);
    public bool IsHighImportance => Message.Importance == Importance.High;
    public bool IsSentOnBehalf => Message.Sender != null && Message.From != null &&
                                  !string.Equals(Message.Sender.Address, Message.From.Address, StringComparison.OrdinalIgnoreCase);
    public string OnBehalfText => IsSentOnBehalf ? $"{Message.Sender!.ShortName} от имени {Message.From!.ShortName}" : "";

    public bool IsMeetingRequest => Message.IsMeetingRequest && Message.Meeting != null;
    public bool IsMeetingCancellation => Message.IsMeetingCancellation;
    public string MeetingText => Message.Meeting is { } m
        ? (m.IsAllDay
              ? m.Start.ToLocalTime().ToString("dddd, d MMMM", RuText.Culture) + ", весь день"
              : $"{m.Start.ToLocalTime().ToString("dddd, d MMMM, HH:mm", RuText.Culture)} – {m.End.ToLocalTime().ToString("HH:mm", RuText.Culture)}") +
          (string.IsNullOrWhiteSpace(m.Location) ? "" : $" · {m.Location}")
        : "";

    public bool HasRemoteContent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoteBanner))]
    private bool _allowRemote;

    public bool ShowRemoteBanner => HasRemoteContent && !AllowRemote;

    [ObservableProperty] private string _html = "";

    private void BuildHtml() => Html = MessageHtmlBuilder.Build(Message, _inline, AllowRemote, ThemeService.IsDark);

    partial void OnAllowRemoteChanged(bool value) => BuildHtml();

    [RelayCommand]
    private void ShowRemoteContent() => AllowRemote = true;

    [RelayCommand]
    private void TrustSender()
    {
        if (Message.From?.Address is { Length: > 0 } addr && !_settings.TrustedSenders.Contains(addr, StringComparer.OrdinalIgnoreCase))
        {
            _settings.TrustedSenders.Add(addr);
            SettingsStore.Save(_settings);
        }
        AllowRemote = true;
    }

    [RelayCommand]
    private async Task SaveAttachmentAsync(AttachmentItemViewModel? item)
    {
        if (item == null) return;
        var dlg = new SaveFileDialog { FileName = SafeFileName(item.Name), Title = "Сохранить вложение" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var content = await _session.Provider.GetAttachmentAsync(item.Info.Id);
            await File.WriteAllBytesAsync(dlg.FileName, content.Content);
            MarkAsDownloaded(dlg.FileName);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить вложение");
        }
    }

    [RelayCommand]
    private async Task SaveAllAttachmentsAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Папка для сохранения вложений" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var all = await _session.Provider.GetAttachmentsAsync(Attachments.Select(a => a.Info.Id));
            foreach (var a in all)
            {
                var path = UniquePath(Path.Combine(dlg.FolderName, SafeFileName(a.Info.Name)));
                await File.WriteAllBytesAsync(path, a.Content);
                MarkAsDownloaded(path);
            }
            WindowsIntegration.ShellOpen(dlg.FolderName);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить вложения");
        }
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(AttachmentItemViewModel? item)
    {
        if (item == null) return;
        if (WindowsIntegration.IsDangerousFile(item.Name) &&
            !Dialogs.Confirm($"Файл «{item.Name}» может содержать вредоносный код.\n\nОткрывайте такие файлы, только если уверены в отправителе. Открыть?",
                "Предупреждение безопасности"))
            return;
        try
        {
            var content = await _session.Provider.GetAttachmentAsync(item.Info.Id);
            var dir = Directory.CreateDirectory(Path.Combine(AppPaths.TempAttachments, Guid.NewGuid().ToString("N")[..8])).FullName;
            var path = Path.Combine(dir, SafeFileName(item.Name));
            await File.WriteAllBytesAsync(path, content.Content);
            MarkAsDownloaded(path);
            WindowsIntegration.ShellOpen(path);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось открыть вложение");
        }
    }

    [RelayCommand]
    private async Task RespondAsync(string? response)
    {
        if (!Enum.TryParse<MeetingResponse>(response, out var r)) return;
        try
        {
            await _session.Provider.RespondToMeetingAsync(Message.Id, r);
            Dialogs.Info(r switch
            {
                MeetingResponse.Accept => "Приглашение принято, ответ отправлен организатору.",
                MeetingResponse.Tentative => "Предварительный ответ отправлен организатору.",
                _ => "Приглашение отклонено, ответ отправлен организатору.",
            });
            _session.SyncNow();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось отправить ответ на приглашение");
        }
    }

    /// <summary>Adds the "Mark of the Web" so Windows/Office treat downloaded attachments as untrusted (Protected View, SmartScreen).</summary>
    private static void MarkAsDownloaded(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch
        {
            // Not supported on FAT/network shares - not critical.
        }
    }

    public static string SafeFileName(string name) => TextUtil.SafeFileName(name);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    [GeneratedRegex(@"(src|background)\s*=\s*[""']?\s*https?://|url\(\s*[""']?https?://", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteRegex();
}
