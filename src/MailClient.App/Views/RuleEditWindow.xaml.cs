using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

/// <summary>Edits one forwarding rule: conditions, how and to whom it forwards.</summary>
public partial class RuleEditWindow : Window
{
    private static readonly char[] AddressSeparators = [';', ','];
    private static readonly char[] WordSeparators = [';'];

    private readonly ForwardingRule _rule;

    public RuleEditWindow(ForwardingRule rule, ForwardingRuleSet capabilities, bool isNew)
    {
        InitializeComponent();
        _rule = rule;
        Result = rule;
        Title = isNew ? "Новое правило пересылки" : "Правило пересылки";

        foreach (var mode in capabilities.SupportedModes)
            ModeCombo.Items.Add(new ComboBoxItem { Content = ModeTitle(mode), Tag = mode });
        ModeCombo.SelectedItem = ModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ForwardingMode)i.Tag == rule.Mode)
                                 ?? ModeCombo.Items[0];
        ModeCombo.IsEnabled = ModeCombo.Items.Count > 1;

        BodyBox.IsEnabled = BodyLabel.IsEnabled = capabilities.SupportsBodyConditions;
        if (!capabilities.SupportsBodyConditions) BodyBox.ToolTip = "Сервер не поддерживает поиск по тексту письма";

        NameBox.Text = rule.Name;
        FromBox.Text = string.Join("; ", rule.FromAddresses);
        SubjectBox.Text = string.Join("; ", rule.SubjectContains);
        BodyBox.Text = string.Join("; ", rule.BodyContains);
        ToBox.Text = EmailAddress.FormatList(rule.Recipients);
        KeepCopyCheck.IsChecked = rule.KeepCopy;
        StopCheck.IsChecked = rule.StopProcessing;
        EnabledCheck.IsChecked = rule.IsEnabled;
        Loaded += (_, _) => (isNew ? ToBox : NameBox).Focus();
    }

    /// <summary>The edited rule (valid after OK).</summary>
    public ForwardingRule Result { get; private set; }

    private static string ModeTitle(ForwardingMode mode) => mode switch
    {
        ForwardingMode.Redirect => "Переадресовать — письмо уходит от исходного отправителя",
        ForwardingMode.Forward => "Переслать — новое письмо от вас с исходным текстом",
        _ => "Переслать вложением — исходное письмо приложено файлом",
    };

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ModeHint.Text = ModeCombo.SelectedItem is ComboBoxItem { Tag: ForwardingMode.Redirect }
            ? "Получатель увидит письмо так, как будто оно пришло ему напрямую, и сможет ответить автору."
            : "Получатель увидит письмо от вас; ответ придёт вам.";

    private static List<string> Split(string text, char[] separators) =>
        text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var recipients = EmailAddress.ParseList(ToBox.Text);
        if (recipients.Count == 0)
        {
            Dialogs.Error("Укажите адрес, на который пересылать письма.");
            ToBox.Focus();
            return;
        }
        var bad = recipients.FirstOrDefault(r => !EmailAddress.LooksValid(r.Address));
        if (bad != null)
        {
            Dialogs.Error($"Адрес «{bad.DisplayText}» указан неверно.");
            ToBox.Focus();
            return;
        }
        var from = Split(FromBox.Text, AddressSeparators);
        var badFrom = from.FirstOrDefault(a => !EmailAddress.LooksValid(a));
        if (badFrom != null)
        {
            Dialogs.Error($"Адрес отправителя «{badFrom}» указан неверно. Укажите адрес целиком, например info@bank.ru.");
            FromBox.Focus();
            return;
        }

        var rule = _rule.Clone();
        rule.FromAddresses = from;
        rule.SubjectContains = Split(SubjectBox.Text, WordSeparators);
        rule.BodyContains = BodyBox.IsEnabled ? Split(BodyBox.Text, WordSeparators) : new();
        rule.Mode = (ForwardingMode)((ComboBoxItem)ModeCombo.SelectedItem).Tag;
        rule.Recipients = recipients.Distinct().ToList();
        rule.KeepCopy = KeepCopyCheck.IsChecked == true;
        rule.StopProcessing = StopCheck.IsChecked == true;
        rule.IsEnabled = EnabledCheck.IsChecked == true;
        rule.Name = NameBox.Text.Trim();
        if (rule.Name.Length == 0)
            rule.Name = $"{char.ToUpperInvariant(RuleItemViewModel.ModeText(rule.Mode)[0])}{RuleItemViewModel.ModeText(rule.Mode)[1..]}: " +
                        string.Join(", ", rule.Recipients.Select(r => r.Address));
        Result = rule;
        DialogResult = true;
    }
}
