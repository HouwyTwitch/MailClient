using System.Globalization;
using MailClient.Core.Models;
using MailClient.Core.Services;

namespace MailClient.App.Services;

/// <summary>Russian formatting helpers: plurals, dates, sizes, folder names, error texts.</summary>
public static class RuText
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Plural form: Plural(5, "письмо", "письма", "писем") → "писем".</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        var n1 = n % 10;
        if (n is > 10 and < 20) return many;
        if (n1 is > 1 and < 5) return few;
        if (n1 == 1) return one;
        return many;
    }

    public static string Count(long n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";

    /// <summary>Compact date for message lists: time today, weekday this week, date otherwise.</summary>
    public static string ShortDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime().DateTime;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm", Culture);
        if (local.Date == today.AddDays(-1)) return "Вчера " + local.ToString("HH:mm", Culture);
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd HH:mm", Culture);
        if (local.Year == today.Year) return local.ToString("d MMM", Culture).TrimEnd('.');
        return local.ToString("dd.MM.yyyy", Culture);
    }

    public static string FullDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("dddd, d MMMM yyyy г., HH:mm", Culture);

    private static readonly char[] NameSeparators = [' ', '.', '@'];

    /// <summary>One or two capital letters for an avatar ("Иванов Иван" → "ИИ"); "?" when there is no name.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant(),
        };
    }

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.#} МБ";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} ГБ";
    }

    public static string FolderName(MailFolder f) => f.WellKnown switch
    {
        WellKnownFolder.Inbox => "Входящие",
        WellKnownFolder.Drafts => "Черновики",
        WellKnownFolder.SentItems => "Отправленные",
        WellKnownFolder.DeletedItems => "Удалённые",
        WellKnownFolder.JunkEmail => "Нежелательная почта",
        WellKnownFolder.Outbox => "Исходящие",
        WellKnownFolder.Archive => "Архив",
        WellKnownFolder.Contacts => "Контакты",
        _ => f.DisplayName,
    };

    public static string FolderIcon(MailFolder f) => f.WellKnown switch
    {
        WellKnownFolder.Inbox => "\uE715",
        WellKnownFolder.Drafts => "\uE70F",
        WellKnownFolder.SentItems => "\uE724",
        WellKnownFolder.DeletedItems => "\uE74D",
        WellKnownFolder.JunkEmail => "\uE7BA",
        WellKnownFolder.Outbox => "\uE898",
        WellKnownFolder.Root => "\uE77B",
        _ => "\uE8B7",
    };

    public static int FolderOrder(MailFolder f) => f.WellKnown switch
    {
        WellKnownFolder.Inbox => 0,
        WellKnownFolder.Drafts => 1,
        WellKnownFolder.SentItems => 2,
        WellKnownFolder.DeletedItems => 3,
        WellKnownFolder.JunkEmail => 4,
        WellKnownFolder.Outbox => 5,
        WellKnownFolder.Archive => 6,
        _ => 100,
    };

    /// <summary>User-facing explanation of an exception.</summary>
    public static string Error(Exception ex) => ex switch
    {
        MailAuthenticationException => ex.Message,
        MailConnectionException => ex.Message,
        MailServiceException => ex.Message,
        OperationCanceledException => "Операция отменена.",
        UnauthorizedAccessException => "Нет доступа к файлу или папке: " + ex.Message,
        System.IO.IOException => "Ошибка ввода-вывода: " + ex.Message,
        _ => "Непредвиденная ошибка: " + ex.Message,
    };

    public static string ResponseText(ResponseStatus s) => s switch
    {
        ResponseStatus.Accept => "Принято",
        ResponseStatus.Tentative => "Под вопросом",
        ResponseStatus.Decline => "Отклонено",
        ResponseStatus.Organizer => "Вы организатор",
        ResponseStatus.NoResponseReceived => "Ответ не отправлен",
        _ => "",
    };

    /// <summary>Adds a reply/forward prefix in the Russian Outlook style, avoiding "RE: RE:" chains.</summary>
    public static string PrefixSubject(string subject, string prefix)
    {
        var s = subject?.Trim() ?? "";
        string[] known = { "RE:", "FW:", "FWD:", "Fwd:", "Re:", "Fw:", "ОТВЕТ:", "ПЕРЕСЛ:", "Ответ:", "Пересл:" };
        if (known.Any(k => s.StartsWith(k, StringComparison.OrdinalIgnoreCase)) && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return s;
        return $"{prefix} {s}";
    }
}
