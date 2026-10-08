using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;
using MailClient.Linux.Controls;
using MailClient.Linux.ViewModels;
using MailClient.Linux.Views;
using Xunit;

namespace MailClient.Linux.Tests;

/// <summary>Avalonia without a display: the application, its styles and windows load and bind.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class HeadlessWindowTests
{
    private static readonly HeadlessUnitTestSession Session = StartSession();

    private static HeadlessUnitTestSession StartSession()
    {
        // No web engine in headless runs: message bodies fall back to text.
        Environment.SetEnvironmentVariable("MAILCLIENT_NO_WEBVIEW", "1");
        return HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
    }

    [Fact]
    public Task Main_window_opens_with_its_panes() => Session.Dispatch(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new CredentialProvider(new LinuxSecretStore()));
        var window = new MainWindow(vm);
        window.Show();

        Assert.Equal("Корпоративная почта", window.Title);
        Assert.NotNull(window.FindControl<TreeView>("FolderTree"));
        Assert.NotNull(window.FindControl<ListBox>("MessageList"));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Написать");
        window.Close();
        vm.Dispose();
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Message_body_without_web_engine_is_shown_as_text() => Session.Dispatch(() =>
    {
        var view = new HtmlView { Html = "<html><body><p>Добрый день!</p><p>Счёт <b>№ 5</b> во вложении.</p></body></html>" };
        var window = new Window { Content = view };
        window.Show();

        var text = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text;
        Assert.Contains("Добрый день!", text, StringComparison.Ordinal);
        Assert.Contains("Счёт № 5 во вложении.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
        window.Close();
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Compose_window_binds_the_shared_view_model() => Session.Dispatch(async () =>
    {
        var account = new AccountSettings { Protocol = MailProtocol.Imap, EmailAddress = "ivanov@company.ru", ImapHost = "127.0.0.1", SmtpHost = "127.0.0.1" };
        using var session = new AccountSession(account, new CredentialProvider(new LinuxSecretStore()));
        var vm = ComposeViewModel.New([session], session);
        vm.To = "petrov@company.ru";
        vm.Subject = "Проверка";
        var window = new ComposeWindow(vm, new AppSettings());
        window.Show();

        Assert.Equal("Проверка — Корпоративная почта", window.Title);
        var editor = window.GetVisualDescendants().OfType<HtmlEditor>().Single();
        Assert.False(editor.IsHtml);
        await editor.SetHtmlAsync("<p>Текст <b>письма</b></p>");
        var (body, isHtml) = await vm.GetBody!();
        Assert.False(isHtml);
        Assert.Contains("Текст письма", body, StringComparison.Ordinal);
        vm.IsDirty = false;
        window.Close();
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Folder_counter_includes_its_subfolders() => Session.Dispatch(() =>
    {
        var account = new AccountSettings { Protocol = MailProtocol.Imap, EmailAddress = "ivanov@company.ru", ImapHost = "127.0.0.1", SmtpHost = "127.0.0.1" };
        using var session = new AccountSession(account, new CredentialProvider(new LinuxSecretStore()));
        FolderNodeViewModel Node(FolderNodeViewModel? parent, string id, int unread, WellKnownFolder wk = WellKnownFolder.None)
        {
            var node = new FolderNodeViewModel(session, new MailFolder { Id = id, DisplayName = id, UnreadCount = unread, WellKnown = wk }, parent == null)
            {
                Parent = parent,
            };
            parent?.Children.Add(node);
            return node;
        }
        var root = Node(null, "root", 0);
        var inbox = Node(root, "Входящие", 2, WellKnownFolder.Inbox);
        var projects = Node(inbox, "Проекты", 3);
        var archive = Node(projects, "Архив", 1);
        var deleted = Node(root, "Удалённые", 5, WellKnownFolder.DeletedItems);
        Node(deleted, "Старое", 2);

        Assert.Equal(6, inbox.BadgeCount);
        Assert.Equal(4, projects.BadgeCount);
        Assert.Equal("Непрочитанных: 6 (в этой папке — 2, в подпапках — 4)", inbox.BadgeTip);
        Assert.False(deleted.HasUnread);          // Deleted Items never shows a counter...
        Assert.Equal(6, root.BadgeCount);         // ...nor adds to the account's

        archive.Unread = 0;                       // read in the subfolder: every parent follows
        Assert.Equal(5, inbox.BadgeCount);
        Assert.Equal(5, root.BadgeCount);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Signature_goes_into_a_new_message_and_its_editor_opens() => Session.Dispatch(async () =>
    {
        var account = new AccountSettings
        {
            Protocol = MailProtocol.Imap, EmailAddress = "ivanov@company.ru", ImapHost = "127.0.0.1", SmtpHost = "127.0.0.1",
            SignatureHtml = "<div><b>Иванов Иван</b></div><div>Отдел АСУ</div>",
        };
        using var session = new AccountSession(account, new CredentialProvider(new LinuxSecretStore()));
        var vm = ComposeViewModel.New([session], session);
        var window = new ComposeWindow(vm, new AppSettings());
        window.Show();
        var editor = window.GetVisualDescendants().OfType<HtmlEditor>().Single();
        await editor.SetHtmlAsync(vm.InitialHtml);
        var (body, _) = await vm.GetBody!();
        Assert.Contains("Иванов Иван", body, StringComparison.Ordinal);
        Assert.Contains("Отдел АСУ", body, StringComparison.Ordinal);
        vm.IsDirty = false;
        window.Close();

        var signature = new SignatureWindow(account, session);
        signature.Show();
        Assert.Equal("Подпись — ivanov@company.ru", signature.Title);
        Assert.Contains(signature.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Заполнить из адресной книги");
        Assert.DoesNotContain(signature.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Взять из Outlook в Интернете" && b.IsVisible);
        signature.Close();
    }, TestContext.Current.CancellationToken);
}
