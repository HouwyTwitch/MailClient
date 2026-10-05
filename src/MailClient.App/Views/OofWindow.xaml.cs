using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Rendering;

namespace MailClient.App.Views;

public partial class OofWindow : Window
{
    private static readonly string[] TimeFormats = [@"h\:mm", @"hh\:mm"];

    private readonly AccountSession _session;

    public OofWindow(AccountSession session)
    {
        InitializeComponent();
        _session = session;
        Title = $"Автоматические ответы — {session.Settings.EmailAddress}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var s = await _session.Provider.GetOutOfOfficeAsync();
            OffRadio.IsChecked = s.State == OofState.Disabled;
            OnRadio.IsChecked = s.State == OofState.Enabled;
            ScheduledRadio.IsChecked = s.State == OofState.Scheduled;
            StartDate.SelectedDate = s.StartTime.LocalDateTime.Date;
            StartTime.Text = s.StartTime.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndDate.SelectedDate = s.EndTime.LocalDateTime.Date;
            EndTime.Text = s.EndTime.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            InternalBox.Text = ToPlain(s.InternalReply);
            ExternalBox.Text = ToPlain(s.ExternalReply);
            AudienceCombo.SelectedItem = AudienceCombo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == s.ExternalAudience.ToString());
            LoadingText.Visibility = Visibility.Collapsed;
            Form.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LoadingText.Text = "Не удалось загрузить настройки: " + RuText.Error(ex);
        }
    }

    private static string ToPlain(string html) =>
        Regex.IsMatch(html, "<[a-z]", RegexOptions.IgnoreCase) ? MessageHtmlBuilder.HtmlToText(html) : html;

    private static string ToHtml(string text) =>
        string.IsNullOrWhiteSpace(text) ? "" : "<html><body>" + WebUtility.HtmlEncode(text).Replace("\r\n", "<br>").Replace("\n", "<br>") + "</body></html>";

    private static DateTimeOffset? Combine(DateTime? date, string time)
    {
        if (date is not { } d) return null;
        if (!TimeSpan.TryParseExact(time.Trim(), TimeFormats, CultureInfo.InvariantCulture, out var t)) return null;
        return new DateTimeOffset(d.Date + t);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var state = OnRadio.IsChecked == true ? OofState.Enabled : ScheduledRadio.IsChecked == true ? OofState.Scheduled : OofState.Disabled;
        var startValue = Combine(StartDate.SelectedDate, StartTime.Text);
        var endValue = Combine(EndDate.SelectedDate, EndTime.Text);
        if (state == OofState.Scheduled && (startValue == null || endValue == null))
        {
            Dialogs.Error("Укажите даты и время начала и окончания. Время — в формате ЧЧ:ММ, например 09:00.");
            return;
        }
        var start = startValue ?? DateTimeOffset.Now;
        var end = endValue ?? DateTimeOffset.Now.AddDays(1);
        if (state == OofState.Scheduled && end <= start)
        {
            Dialogs.Error("Дата окончания должна быть позже даты начала.");
            return;
        }
        if (state != OofState.Disabled && string.IsNullOrWhiteSpace(InternalBox.Text))
        {
            Dialogs.Error("Введите текст автоматического ответа.");
            return;
        }
        IsEnabled = false;
        try
        {
            await _session.Provider.SetOutOfOfficeAsync(new OofSettings
            {
                State = state,
                StartTime = start,
                EndTime = end,
                InternalReply = ToHtml(InternalBox.Text),
                ExternalReply = ToHtml(ExternalBox.Text),
                ExternalAudience = Enum.Parse<OofExternalAudience>((string)((ComboBoxItem)AudienceCombo.SelectedItem).Tag),
            });
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить автоответ");
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
