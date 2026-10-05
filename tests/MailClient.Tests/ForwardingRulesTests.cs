using System.Diagnostics;
using System.Globalization;
using MailClient.Core.Models;
using MailClient.Core.Rules;
using MailClient.Core.Services;
using MailClient.Imap;
using Xunit;
using static MailClient.Tests.FakeEws;

namespace MailClient.Tests;

/// <summary>Forwarding rules: Exchange Inbox rules over EWS and Sieve scripts over ManageSieve.</summary>
public class ForwardingRulesTests
{
    // ================================================================== Exchange

    private const string ServerRules = """
        <GetInboxRulesResponse ResponseClass="Success" xmlns="http://schemas.microsoft.com/exchange/services/2006/messages">
          <ResponseCode>NoError</ResponseCode>
          <OutlookRuleBlobExists>true</OutlookRuleBlobExists>
          <InboxRules>
            <Rule xmlns="http://schemas.microsoft.com/exchange/services/2006/types">
              <RuleId>R-move</RuleId><DisplayName>В папку «Отчёты»</DisplayName><Priority>2</Priority><IsEnabled>true</IsEnabled>
              <Conditions><SentToMe>true</SentToMe></Conditions>
              <Actions><MoveToFolder><FolderId Id="F1" ChangeKey="CK1"/></MoveToFolder></Actions>
            </Rule>
            <Rule xmlns="http://schemas.microsoft.com/exchange/services/2006/types">
              <RuleId>R-bank</RuleId><DisplayName>Счета в бухгалтерию</DisplayName><Priority>1</Priority><IsEnabled>true</IsEnabled>
              <Conditions>
                <ContainsSubjectStrings><String>счёт</String><String>оплата</String></ContainsSubjectStrings>
                <FromAddresses><Address><Name>Банк</Name><EmailAddress>bank@bank.ru</EmailAddress><RoutingType>SMTP</RoutingType></Address></FromAddresses>
              </Conditions>
              <Actions>
                <RedirectToRecipients><Address><Name>Бухгалтерия</Name><EmailAddress>buh@corp.ru</EmailAddress></Address></RedirectToRecipients>
                <StopProcessingRules>true</StopProcessingRules>
              </Actions>
            </Rule>
            <Rule xmlns="http://schemas.microsoft.com/exchange/services/2006/types">
              <RuleId>R-all</RuleId><DisplayName>Всё помощнику</DisplayName><Priority>3</Priority><IsEnabled>false</IsEnabled>
              <Actions>
                <Delete>true</Delete>
                <ForwardToRecipients><Address><EmailAddress>helper@corp.ru</EmailAddress></Address></ForwardToRecipients>
              </Actions>
            </Rule>
          </InboxRules>
        </GetInboxRulesResponse>
        """;

    private const string UpdateOk = """
        <UpdateInboxRulesResponse ResponseClass="Success" xmlns="http://schemas.microsoft.com/exchange/services/2006/messages">
          <ResponseCode>NoError</ResponseCode>
        </UpdateInboxRulesResponse>
        """;

    [Fact]
    public async Task Exchange_rules_are_read_in_priority_order()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules);
        using var p = fake.CreateProvider();

        var set = await p.GetForwardingRulesAsync(TestContext.Current.CancellationToken);

        Assert.True(set.OutlookRulesPresent);
        Assert.Equal(["R-bank", "R-move", "R-all"], set.Rules.Select(r => r.Id));
        var bank = set.Rules[0];
        Assert.True(bank.IsEditable);
        Assert.Equal(ForwardingMode.Redirect, bank.Mode);
        Assert.Equal(["bank@bank.ru"], bank.FromAddresses);
        Assert.Equal(["счёт", "оплата"], bank.SubjectContains);
        Assert.Equal(new EmailAddress("Бухгалтерия", "buh@corp.ru"), bank.Recipients.Single());
        Assert.True(bank.KeepCopy);
        Assert.True(bank.StopProcessing);

        Assert.False(set.Rules[1].IsEditable); // a folder move made in Outlook

        var all = set.Rules[2];
        Assert.True(all.IsEditable);
        Assert.False(all.IsEnabled);
        Assert.False(all.HasConditions);
        Assert.Equal(ForwardingMode.Forward, all.Mode);
        Assert.False(all.KeepCopy);
        Assert.Empty(fake.ValidationErrors);
    }

    [Fact]
    public async Task Exchange_save_sends_only_the_differences()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules).On("GetInboxRules", ServerRules).On("UpdateInboxRules", UpdateOk);
        using var p = fake.CreateProvider();
        var ct = TestContext.Current.CancellationToken;
        var rules = (await p.GetForwardingRulesAsync(ct)).Rules;

        // New rule on top, the bank rule gets a second recipient, the Outlook rule is switched off, the third is deleted.
        var bank = rules[0];
        bank.Recipients.Add(new EmailAddress("", "director@corp.ru"));
        var move = rules[1];
        move.IsEnabled = false;
        var created = new ForwardingRule
        {
            Name = "Договоры юристу", Mode = ForwardingMode.ForwardAsAttachment, BodyContains = ["договор"],
            Recipients = [new EmailAddress("Юрист", "law@corp.ru")], KeepCopy = true,
        };
        await p.SaveForwardingRulesAsync([created, bank, move], replaceOutlookRules: true, ct);

        Assert.Empty(fake.ValidationErrors);
        var update = fake.Last("UpdateInboxRules");
        Assert.Equal("true", update.Element(M + "RemoveOutlookRuleBlob")?.Value);
        var ops = update.Element(M + "Operations")!.Elements().ToList();
        Assert.Equal(["DeleteRuleOperation", "CreateRuleOperation", "SetRuleOperation", "SetRuleOperation"], ops.Select(o => o.Name.LocalName));
        Assert.Equal("R-all", ops[0].Element(T + "RuleId")?.Value);

        var create = ops[1].Element(T + "Rule")!;
        Assert.Null(create.Element(T + "RuleId"));
        Assert.Equal("1", create.Element(T + "Priority")?.Value);
        Assert.Equal("договор", create.Descendants(T + "ContainsBodyStrings").Single().Value);
        Assert.Equal("law@corp.ru", create.Descendants(T + "ForwardAsAttachmentToRecipients").Single().Descendants(T + "EmailAddress").Single().Value);
        Assert.Null(create.Descendants(T + "Delete").SingleOrDefault());

        var setBank = ops[2].Element(T + "Rule")!;
        Assert.Equal("R-bank", setBank.Element(T + "RuleId")?.Value);
        Assert.Equal("2", setBank.Element(T + "Priority")?.Value);
        Assert.Equal(2, setBank.Descendants(T + "RedirectToRecipients").Single().Elements().Count());

        // The Outlook rule is written back as the server had it, only switched off and renumbered.
        var setMove = ops[3].Element(T + "Rule")!;
        Assert.Equal("false", setMove.Element(T + "IsEnabled")?.Value);
        Assert.Equal("3", setMove.Element(T + "Priority")?.Value);
        Assert.Equal("F1", (string?)setMove.Descendants(T + "FolderId").Single().Attribute("Id"));
        Assert.Equal("true", setMove.Descendants(T + "SentToMe").Single().Value);
    }

    [Fact]
    public async Task Exchange_save_without_changes_sends_nothing()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules).On("GetInboxRules", ServerRules);
        using var p = fake.CreateProvider();
        var ct = TestContext.Current.CancellationToken;
        var rules = (await p.GetForwardingRulesAsync(ct)).Rules;

        await p.SaveForwardingRulesAsync(rules, replaceOutlookRules: false, ct);

        Assert.Empty(fake.All("UpdateInboxRules"));
    }

    [Fact]
    public async Task Exchange_rule_edited_to_every_message_drops_its_conditions()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules).On("GetInboxRules", ServerRules).On("UpdateInboxRules", UpdateOk);
        using var p = fake.CreateProvider();
        var ct = TestContext.Current.CancellationToken;
        var rules = (await p.GetForwardingRulesAsync(ct)).Rules;
        rules[0].FromAddresses.Clear();
        rules[0].SubjectContains.Clear();

        await p.SaveForwardingRulesAsync(rules, replaceOutlookRules: true, ct);

        var rule = fake.Last("UpdateInboxRules").Descendants(T + "Rule").Single();
        Assert.False(rule.Element(T + "Conditions")!.HasElements);
        Assert.Empty(fake.ValidationErrors);
    }

    [Fact]
    public async Task Exchange_validation_errors_reach_the_user()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules).On("UpdateInboxRules", """
            <UpdateInboxRulesResponse ResponseClass="Error" xmlns="http://schemas.microsoft.com/exchange/services/2006/messages">
              <MessageText>Validation error occurred while processing inbox rules.</MessageText>
              <ResponseCode>ErrorInboxRulesValidationError</ResponseCode>
              <RuleOperationErrors>
                <RuleOperationError xmlns="http://schemas.microsoft.com/exchange/services/2006/types">
                  <OperationIndex>0</OperationIndex>
                  <ValidationErrors><Error><FieldURI>RedirectToRecipients</FieldURI><ErrorCode>InvalidAddress</ErrorCode>
                    <ErrorMessage>The e-mail address "nobody@nowhere" isn't valid.</ErrorMessage><FieldValue>nobody@nowhere</FieldValue></Error></ValidationErrors>
                </RuleOperationError>
              </RuleOperationErrors>
            </UpdateInboxRulesResponse>
            """);
        using var p = fake.CreateProvider();

        var rule = new ForwardingRule { Name = "x", Recipients = [new EmailAddress("", "nobody@nowhere")] };
        var ex = await Assert.ThrowsAsync<Exchange.Ews.EwsResponseException>(() =>
            p.SaveForwardingRulesAsync([rule], true, TestContext.Current.CancellationToken));
        Assert.Contains("nobody@nowhere", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("Сервер не принял правило", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_rule_deleted_meanwhile_is_reported()
    {
        var fake = new FakeEws().On("GetInboxRules", ServerRules);
        using var p = fake.CreateProvider();
        var gone = new ForwardingRule { Id = "R-gone", Name = "Старое", Recipients = [new EmailAddress("", "a@corp.ru")] };

        var ex = await Assert.ThrowsAsync<MailServiceException>(() => p.SaveForwardingRulesAsync([gone], true, TestContext.Current.CancellationToken));

        Assert.Equal("ErrorRuleNotFound", ex.ErrorCode);
        Assert.Empty(fake.All("UpdateInboxRules"));
    }

    // ================================================================== Sieve script

    private static readonly SieveRules.Extensions Dovecot =
        SieveRules.Extensions.Parse("fileinto reject envelope encoded-character vacation subaddress relational regex imap4flags copy include variables body");

    private static ForwardingRule BankRule() => new()
    {
        Id = "a1", Name = "Счета \"банка\" в бухгалтерию",
        FromAddresses = ["bank@bank.ru"], SubjectContains = ["Счёт"],
        Recipients = [new EmailAddress("Бухгалтерия", "buh@corp.ru")], KeepCopy = true, StopProcessing = true,
    };

    [Fact]
    public void Sieve_block_round_trips_every_rule()
    {
        var disabled = new ForwardingRule { Id = "b2", Name = "Выключенное", IsEnabled = false, BodyContains = ["договор"], Recipients = [new EmailAddress("", "law@corp.ru")], KeepCopy = false };
        var script = SieveRules.Apply(null, [BankRule(), disabled], Dovecot);

        var back = SieveRules.Parse(script);

        Assert.Equal(2, back.Count);
        Assert.Equal("Счета \"банка\" в бухгалтерию", back[0].Name);
        Assert.Equal(["bank@bank.ru"], back[0].FromAddresses);
        Assert.Equal(new EmailAddress("Бухгалтерия", "buh@corp.ru"), back[0].Recipients.Single());
        Assert.True(back[0].StopProcessing && back[0].KeepCopy && back[0].IsEnabled);
        Assert.False(back[1].IsEnabled);
        Assert.False(back[1].KeepCopy);
        Assert.Equal(["договор"], back[1].BodyContains);
        Assert.All(back, r => Assert.Equal(ForwardingMode.Redirect, r.Mode));
        // A switched-off rule produces no code (and needs no "body" extension).
        Assert.DoesNotContain("law@corp.ru\";", script, StringComparison.Ordinal);
        Assert.Contains("require [\"copy\"];", script, StringComparison.Ordinal);
        Assert.Contains("redirect :copy \"buh@corp.ru\";", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sieve_block_goes_after_the_requires_of_an_existing_script_and_leaves_it_intact()
    {
        const string existing = "# Фильтры из веб-почты\r\nrequire [\"fileinto\"];\r\nrequire \"imap4flags\"; # флаги\r\n\r\nif header :contains \"subject\" \"[SPAM]\" {\r\n  fileinto \"Junk\";\r\n}\r\n";

        var withRules = SieveRules.Apply(existing, [BankRule()], Dovecot);
        var withoutRules = SieveRules.Apply(withRules, [], Dovecot);

        var begin = withRules.IndexOf(SieveRules.BeginMarker, StringComparison.Ordinal);
        Assert.True(begin > withRules.IndexOf("imap4flags", StringComparison.Ordinal));
        Assert.True(begin < withRules.IndexOf("[SPAM]", StringComparison.Ordinal));
        Assert.Equal(existing, withoutRules);
        Assert.True(SieveRules.HasOtherContent(withRules));
        Assert.False(SieveRules.HasOtherContent(SieveRules.Apply(null, [BankRule()], Dovecot)));
        // Applying the same rules again changes nothing.
        Assert.Equal(withRules, SieveRules.Apply(withRules, SieveRules.Parse(withRules), Dovecot));
    }

    [Fact]
    public void Sieve_without_copy_extension_keeps_explicitly_and_unconditional_rule_has_no_if()
    {
        var rule = new ForwardingRule { Id = "c", Name = "Всё", Recipients = [new EmailAddress("", "all@corp.ru"), new EmailAddress("", "b@corp.ru")] };

        var script = SieveRules.Build([rule], SieveRules.Extensions.Parse("fileinto"));

        Assert.DoesNotContain("require", script, StringComparison.Ordinal);
        Assert.DoesNotContain("if ", script, StringComparison.Ordinal);
        Assert.Contains("redirect \"all@corp.ru\";\nredirect \"b@corp.ru\";\nkeep;\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sieve_strings_are_escaped()
    {
        Assert.Equal("\"a\\\"b\\\\c d\"", SieveRules.Quote("a\"b\\c\nd"));
        Assert.Equal(0, SieveRules.HeaderEnd("if true { keep; }"));
        Assert.Equal("require \"a;b\";".Length, SieveRules.HeaderEnd("require \"a;b\"; if true { keep; }"));
    }

    /// <summary>
    /// Runs the generated script through Pigeonhole's sieve-test against real messages (SIEVE_TEST_CONFIG: a
    /// Dovecot config for sieve-test, e.g. mail_uid=nobody, mail_gid=nogroup, first_valid_uid=0).
    /// </summary>
    [Fact]
    public void Sieve_script_does_what_the_rules_say()
    {
        var config = Environment.GetEnvironmentVariable("SIEVE_TEST_CONFIG");
        Assert.SkipUnless(!OperatingSystem.IsWindows() && File.Exists(config ?? "") && File.Exists("/usr/bin/sieve-test"),
            "Проверка скриптов через sieve-test включается переменной SIEVE_TEST_CONFIG (см. tests/imap-test-env.md).");
        if (OperatingSystem.IsWindows()) return; // unreachable after the skip; tells the analyzer too
        var everyone = new ForwardingRule { Id = "d", Name = "Всё помощнику", Recipients = [new EmailAddress("", "helper@corp.ru")], KeepCopy = false };
        var body = new ForwardingRule { Id = "e", Name = "Договоры", BodyContains = ["договор"], Recipients = [new EmailAddress("", "law@corp.ru")] };
        var script = SieveRules.Apply(null, [BankRule(), body, everyone], Dovecot);

        var bank = RunSieve(config!, script, "Банк <BANK@bank.ru>", "Счёт №5 за октябрь", "Добрый день");
        Assert.Contains("redirect message to: <buh@corp.ru>", bank, StringComparison.Ordinal);
        Assert.DoesNotContain("helper@corp.ru", bank, StringComparison.Ordinal); // stop
        Assert.Contains("store message in folder: INBOX", bank, StringComparison.Ordinal); // copy kept

        var other = RunSieve(config!, script, "Петров <petrov@corp.ru>", "Встреча", "Проект договора во вложении");
        Assert.Contains("redirect message to: <law@corp.ru>", other, StringComparison.Ordinal);
        Assert.Contains("redirect message to: <helper@corp.ru>", other, StringComparison.Ordinal);
        Assert.DoesNotContain("buh@corp.ru", other, StringComparison.Ordinal);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string RunSieve(string config, string script, string from, string subject, string text)
    {
        // sieve-test drops privileges, so the files live where any user can read them.
        var dir = Path.Combine(Path.GetTempPath(), "mc-sieve-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            var scriptPath = Path.Combine(dir, "rules.sieve");
            var mailPath = Path.Combine(dir, "mail.eml");
            File.WriteAllText(scriptPath, script);
            var message = new MimeKit.MimeMessage();
            message.From.Add(MimeKit.MailboxAddress.Parse(from));
            message.To.Add(MimeKit.MailboxAddress.Parse("ivanov@corp.ru"));
            message.Subject = subject;
            message.Body = new MimeKit.TextPart("plain") { Text = text };
            message.WriteTo(mailPath);
            foreach (var f in new[] { scriptPath, mailPath }) File.SetUnixFileMode(f, (UnixFileMode)0b110_100_100);

            using var process = Process.Start(new ProcessStartInfo("/usr/bin/sieve-test", ["-c", config, scriptPath, mailPath])
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
            return output;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ================================================================== ManageSieve (live)

    /// <summary>Against the Dovecot of ImapIntegrationTests with ManageSieve on SIEVE_TEST_PORT (STARTTLS).</summary>
    [Fact]
    public async Task ManageSieve_rules_round_trip_on_a_real_server()
    {
        var port = Environment.GetEnvironmentVariable("SIEVE_TEST_PORT");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_TEST") == "1" && port != null,
            "Тест ManageSieve включается переменными MAILCLIENT_IMAP_TEST=1 и SIEVE_TEST_PORT (см. tests/imap-test-env.md).");
        var ct = TestContext.Current.CancellationToken;
        var account = new AccountSettings
        {
            Protocol = MailProtocol.Imap,
            EmailAddress = Environment.GetEnvironmentVariable("IMAP_TEST_USER")!,
            ImapHost = Environment.GetEnvironmentVariable("IMAP_TEST_HOST") ?? "localhost",
            ImapPort = 993,
            ImapSecurity = ConnectionSecurity.SslOnConnect,
            SievePort = int.Parse(port!, CultureInfo.InvariantCulture),
            SmtpHost = "localhost",
            TrustedRootCertificatesPem = File.ReadAllText(Environment.GetEnvironmentVariable("IMAP_TEST_CA")!),
        };
        using var p = new ImapProvider(account, new FixedPassword(Environment.GetEnvironmentVariable("IMAP_TEST_PASSWORD")!));

        await p.SaveForwardingRulesAsync([], false, ct); // start clean
        var empty = await p.GetForwardingRulesAsync(ct);
        Assert.Empty(empty.Rules);
        Assert.Equal([ForwardingMode.Redirect], empty.SupportedModes);
        Assert.True(empty.SupportsBodyConditions);

        var body = new ForwardingRule { Name = "Договоры «юристу»", BodyContains = ["договор"], Recipients = [new EmailAddress("Юрист", "law@test.ru")] };
        await p.SaveForwardingRulesAsync([BankRule(), body], false, ct);
        var saved = await p.GetForwardingRulesAsync(ct);
        Assert.Equal(["Счета \"банка\" в бухгалтерию", "Договоры «юристу»"], saved.Rules.Select(r => r.Name));
        Assert.NotEmpty(saved.Rules[1].Id);

        saved.Rules[0].IsEnabled = false;
        await p.SaveForwardingRulesAsync([saved.Rules[1], saved.Rules[0]], false, ct);
        var reordered = await p.GetForwardingRulesAsync(ct);
        Assert.Equal(["Договоры «юристу»", "Счета \"банка\" в бухгалтерию"], reordered.Rules.Select(r => r.Name));
        Assert.False(reordered.Rules[1].IsEnabled);
        Assert.Equal(saved.Rules[1].Id, reordered.Rules[0].Id);

        await p.SaveForwardingRulesAsync([], false, ct);
        Assert.Empty((await p.GetForwardingRulesAsync(ct)).Rules);
    }

    [Fact]
    public async Task ManageSieve_missing_service_is_explained()
    {
        // Nothing listens on port 1 of the loopback interface.
        var account = new AccountSettings { Protocol = MailProtocol.Imap, EmailAddress = "a@b.ru", ImapHost = "127.0.0.1", SievePort = 1, SmtpHost = "127.0.0.1" };
        using var p = new ImapProvider(account, new FixedPassword("x"));

        var ex = await Assert.ThrowsAsync<MailServiceException>(() => p.GetForwardingRulesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ManageSieveUnavailable", ex.ErrorCode);
        Assert.Contains("веб-интерфейсе", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FixedPassword(string password) : ICredentialProvider
    {
        public string? GetPassword(Guid accountId) => password;
    }
}
