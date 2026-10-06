using System.IO;
using MailClient.App.Services;
using MailClient.Core.Models;
using Xunit;

namespace MailClient.Linux.Tests;

public sealed class LinuxPlatformTests
{
    [Fact]
    public void Passwords_survive_a_restart_and_the_files_are_private()
    {
        var store = new LinuxSecretStore();
        var key = "test:" + Guid.NewGuid().ToString("N");

        store.Save(key, "Пароль-123 со «спецсимволами»");

        Assert.Equal("Пароль-123 со «спецсимволами»", new LinuxSecretStore().Load(key));
        if (!OperatingSystem.IsWindows())
        {
            var dir = AppPaths.Secrets;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            foreach (var file in Directory.GetFiles(dir))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            // Not readable as plain text on disk.
            Assert.DoesNotContain(Directory.GetFiles(dir), f => File.ReadAllText(f).Contains("Пароль-123", StringComparison.Ordinal));
        }
        store.Delete(key);
        Assert.Null(store.Load(key));
    }

    [Fact]
    public void Organization_policy_file_preconfigures_new_accounts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mc-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            { "EwsUrl": "https://mail.company.ru/EWS/Exchange.asmx", "Domain": "CORP", "AuthMethod": "Password",
              "ServerVersion": "Exchange2016", "DisableForwardingRules": true, "LockServerSettings": true }
            """);
        var previous = OrganizationDefaults.PolicyFile;
        try
        {
            OrganizationDefaults.PolicyFile = file;
            var org = OrganizationDefaults.Load();
            var account = new AccountSettings();
            org.ApplyTo(account);

            Assert.Equal("https://mail.company.ru/EWS/Exchange.asmx", account.EwsUrl);
            Assert.Equal("CORP", account.Domain);
            Assert.Equal(ExchangeServerVersion.Exchange2016, account.ServerVersion);
            Assert.True(org.DisableForwardingRules);
            Assert.True(org.LockServerSettings);
        }
        finally
        {
            OrganizationDefaults.PolicyFile = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Windows_style_filters_become_picker_file_types()
    {
        var types = FileDialogs.ParseFilter("Письма (*.eml)|*.eml|Сертификаты (*.cer;*.pem)|*.cer;*.pem");

        Assert.NotNull(types);
        Assert.Equal(2, types.Count);
        Assert.Equal("Письма (*.eml)", types[0].Name);
        Assert.Equal(["*.cer", "*.pem"], types[1].Patterns!);
        Assert.Null(FileDialogs.ParseFilter(null));
    }

    [Fact]
    public void Dangerous_attachments_include_linux_executables()
    {
        Assert.True(DesktopIntegration.IsDangerousFile("setup.sh"));
        Assert.True(DesktopIntegration.IsDangerousFile("Счёт.desktop"));
        Assert.True(DesktopIntegration.IsDangerousFile("invoice.EXE"));
        Assert.False(DesktopIntegration.IsDangerousFile("договор.pdf"));
        Assert.True(DesktopIntegration.IsSafeExternalLink(new Uri("https://company.ru")));
        Assert.False(DesktopIntegration.IsSafeExternalLink(new Uri("file:///etc/passwd")));
    }

    [Fact]
    public async Task A_second_start_hands_its_arguments_to_the_running_copy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets of the Linux build.");
        var runtime = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runtime);
        var previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
        try
        {
            using var first = SingleInstance.TryAcquire([]);
            Assert.NotNull(first);
            var received = new TaskCompletionSource<string[]>();
            first.ArgumentsReceived += (_, args) => received.TrySetResult(args);

            var second = SingleInstance.TryAcquire(["mailto:petrov@company.ru?subject=Тест"]);

            Assert.Null(second);
            var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(["mailto:petrov@company.ru?subject=Тест"], args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
            Directory.Delete(runtime, true);
        }
    }
}
