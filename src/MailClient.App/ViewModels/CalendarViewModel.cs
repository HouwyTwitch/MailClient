using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

public sealed class CalendarEventViewModel
{
    public CalendarEventViewModel(CalendarEvent e) => Event = e;
    public CalendarEvent Event { get; }
    public string Subject => string.IsNullOrWhiteSpace(Event.Subject) ? "(без темы)" : Event.Subject;
    public string TimeText => Event.IsAllDay ? "Весь день" : $"{Event.Start.ToLocalTime():HH:mm} – {Event.End.ToLocalTime():HH:mm}";
    public string Location => Event.Location;
    public bool HasLocation => !string.IsNullOrWhiteSpace(Event.Location);
    public string OrganizerText => Event.Organizer?.DisplayText ?? "";
    public string ResponseText => RuText.ResponseText(Event.MyResponse);
    public bool IsTentative => Event.FreeBusy == FreeBusyStatus.Tentative || Event.MyResponse == ResponseStatus.NoResponseReceived;
    public bool IsCancelled => Event.IsCancelled;
    public bool CanRespond => Event.IsMeeting && Event.MyResponse != ResponseStatus.Organizer && !Event.IsCancelled;
    public bool IsOrganizer => !Event.IsMeeting || Event.MyResponse == ResponseStatus.Organizer;
    public string DateText => Event.IsAllDay
        ? Event.Start.ToLocalTime().ToString("dddd, d MMMM yyyy", RuText.Culture)
        : $"{Event.Start.ToLocalTime().ToString("dddd, d MMMM yyyy", RuText.Culture)}, {TimeText}";
    public string RecurrenceText => Event.IsRecurring ? "Повторяющееся событие" : "";
}

public sealed partial class CalendarDayViewModel : ObservableObject
{
    public CalendarDayViewModel(DateTime date) => Date = date;
    public DateTime Date { get; }
    public string DayName => Date.ToString("ddd", RuText.Culture).ToUpperInvariant();
    public string DayNumber => Date.Day.ToString();
    public bool IsToday => Date.Date == DateTime.Today;
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    public ObservableCollection<CalendarEventViewModel> Events { get; } = new();
}

public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly Func<AccountSession?> _session;
    private CancellationTokenSource? _cts;

    public CalendarViewModel(Func<AccountSession?> session)
    {
        _session = session;
        _weekStart = StartOfWeek(DateTime.Today);
    }

    public ObservableCollection<CalendarDayViewModel> Days { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    private DateTime _weekStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CalendarEventViewModel? _selectedEvent;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    public bool HasSelection => SelectedEvent != null;

    public string RangeText
    {
        get
        {
            var end = WeekStart.AddDays(6);
            return WeekStart.Month == end.Month
                ? $"{WeekStart.Day} – {end.ToString("d MMMM yyyy", RuText.Culture)}"
                : $"{WeekStart.ToString("d MMMM", RuText.Culture)} – {end.ToString("d MMMM yyyy", RuText.Culture)}";
        }
    }

    private static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday

    [RelayCommand]
    private void SelectEvent(CalendarEventViewModel? e) => SelectedEvent = e;

    [RelayCommand] private Task PreviousAsync() { WeekStart = WeekStart.AddDays(-7); return LoadAsync(); }
    [RelayCommand] private Task NextAsync() { WeekStart = WeekStart.AddDays(7); return LoadAsync(); }
    [RelayCommand] private Task TodayAsync() { WeekStart = StartOfWeek(DateTime.Today); return LoadAsync(); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var session = _session();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Days.Clear();
        for (int i = 0; i < 7; i++) Days.Add(new CalendarDayViewModel(WeekStart.AddDays(i)));
        SelectedEvent = null;
        if (session == null) return;

        IsBusy = true;
        StatusText = "Загрузка календаря…";
        try
        {
            var start = new DateTimeOffset(WeekStart);
            var events = await session.Provider.GetEventsAsync(start, start.AddDays(7), null, ct);
            foreach (var e in events)
            {
                var vm = new CalendarEventViewModel(e);
                var first = e.Start.ToLocalTime().Date;
                var last = e.IsAllDay ? e.End.ToLocalTime().Date.AddDays(-1) : e.End.ToLocalTime().AddTicks(-1).Date;
                foreach (var day in Days.Where(d => d.Date >= first && d.Date <= last))
                    day.Events.Add(vm);
            }
            StatusText = RuText.Count(events.Count, "событие", "события", "событий") + " на неделе";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Календарь недоступен: " + RuText.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NewEventAsync(DateTime? day)
    {
        var session = _session();
        if (session == null) return;
        var date = day ?? (WeekStart <= DateTime.Today && DateTime.Today < WeekStart.AddDays(7) ? DateTime.Today : WeekStart);
        var hour = date.Date == DateTime.Today ? Math.Clamp(DateTime.Now.Hour + 1, 8, 20) : 10;
        var evt = new CalendarEvent
        {
            Start = new DateTimeOffset(date.Date.AddHours(hour)),
            End = new DateTimeOffset(date.Date.AddHours(hour + 1)),
        };
        if (Views.WindowFactory.EditEvent(evt) != true) return;
        try
        {
            await session.Provider.CreateEventAsync(evt);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось создать событие");
        }
    }

    [RelayCommand]
    private async Task DeleteEventAsync()
    {
        var session = _session();
        if (session == null || SelectedEvent == null) return;
        var e = SelectedEvent;
        var isOrganizerMeeting = e.Event.IsMeeting && e.Event.MyResponse == ResponseStatus.Organizer;
        var question = isOrganizerMeeting
            ? $"Отменить встречу «{e.Subject}»? Участники получат уведомление об отмене."
            : $"Удалить событие «{e.Subject}»?";
        if (!Dialogs.Confirm(question)) return;
        try
        {
            await session.Provider.CancelOrDeleteEventAsync(e.Event.Id, isOrganizerMeeting);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось удалить событие");
        }
    }

    [RelayCommand]
    private async Task RespondAsync(string? response)
    {
        var session = _session();
        if (session == null || SelectedEvent == null || !Enum.TryParse<MeetingResponse>(response, out var r)) return;
        try
        {
            await session.Provider.RespondToMeetingAsync(SelectedEvent.Event.Id, r);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось отправить ответ");
        }
    }
}
