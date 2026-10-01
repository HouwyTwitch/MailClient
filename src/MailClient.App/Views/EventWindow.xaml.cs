using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.Views;

public partial class EventWindow : Window
{
    private readonly CalendarEvent _evt;

    public EventWindow(CalendarEvent evt)
    {
        InitializeComponent();
        _evt = evt;
        for (var t = TimeSpan.Zero; t < TimeSpan.FromDays(1); t += TimeSpan.FromMinutes(30))
        {
            StartCombo.Items.Add(t.ToString(@"hh\:mm"));
            EndCombo.Items.Add(t.ToString(@"hh\:mm"));
        }
        var start = evt.Start.ToLocalTime();
        var end = evt.End.ToLocalTime();
        SubjectBox.Text = evt.Subject;
        LocationBox.Text = evt.Location;
        DatePick.SelectedDate = start.Date;
        StartCombo.Text = start.ToString("HH:mm");
        EndCombo.Text = end.ToString("HH:mm");
        AllDayCheck.IsChecked = evt.IsAllDay;
        BusyCombo.SelectedIndex = 0;
        ReminderCombo.SelectedIndex = 2;
        Loaded += (_, _) => SubjectBox.Focus();
    }

    private void AllDay_Changed(object sender, RoutedEventArgs e)
    {
        var timed = AllDayCheck.IsChecked != true;
        StartCombo.IsEnabled = timed;
        EndCombo.IsEnabled = timed;
        if (!timed && BusyCombo.SelectedIndex == 0) BusyCombo.SelectedIndex = 2;
    }

    private static bool TryTime(string text, out TimeSpan t) =>
        TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm", @"h\.mm", @"hh\.mm", "hhmm" }, CultureInfo.InvariantCulture, out t);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SubjectBox.Text))
        {
            Dialogs.Error("Введите тему события.");
            return;
        }
        if (DatePick.SelectedDate is not { } date)
        {
            Dialogs.Error("Выберите дату.");
            return;
        }
        var allDay = AllDayCheck.IsChecked == true;
        DateTime start, end;
        if (allDay)
        {
            start = date.Date;
            end = date.Date.AddDays(1);
        }
        else
        {
            if (!TryTime(StartCombo.Text, out var s) || !TryTime(EndCombo.Text, out var f))
            {
                Dialogs.Error("Время указывается в формате ЧЧ:ММ, например 09:30.");
                return;
            }
            start = date.Date + s;
            end = date.Date + f;
            if (end <= start) end = end.AddDays(1);
        }

        var required = EmailAddress.ParseList(RequiredBox.Text);
        var optional = EmailAddress.ParseList(OptionalBox.Text);
        var invalid = required.Concat(optional).FirstOrDefault(a => !EmailAddress.LooksValid(a.Address));
        if (invalid != null)
        {
            Dialogs.Error($"Некорректный адрес участника: «{invalid.DisplayText}». Укажите адрес электронной почты.");
            return;
        }

        _evt.Subject = SubjectBox.Text.Trim();
        _evt.Location = LocationBox.Text.Trim();
        _evt.IsAllDay = allDay;
        _evt.Start = new DateTimeOffset(start);
        _evt.End = new DateTimeOffset(end);
        _evt.FreeBusy = Enum.Parse<FreeBusyStatus>((string)((ComboBoxItem)BusyCombo.SelectedItem).Tag);
        var reminder = int.Parse((string)((ComboBoxItem)ReminderCombo.SelectedItem).Tag);
        _evt.ReminderSet = reminder >= 0;
        _evt.ReminderMinutes = Math.Max(0, reminder);
        _evt.RequiredAttendees = required.ToList();
        _evt.OptionalAttendees = optional.ToList();
        _evt.Body = WebUtility.HtmlEncode(BodyBox.Text).Replace("\r\n", "<br>").Replace("\n", "<br>");
        DialogResult = true;
    }
}
