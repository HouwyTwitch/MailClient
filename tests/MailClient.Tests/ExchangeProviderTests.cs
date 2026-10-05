using System.Net;
using System.Xml.Linq;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Ews;
using Xunit;
using static MailClient.Tests.FakeEws;

namespace MailClient.Tests;

public class ExchangeProviderTests
{
    private const string MessageXml = """
        <t:Message>
          <t:ItemId Id="AAA=" ChangeKey="CK1"/>
          <t:ItemClass>IPM.Note</t:ItemClass>
          <t:Subject>Quarterly report</t:Subject>
          <t:DateTimeReceived>2026-09-30T08:15:00Z</t:DateTimeReceived>
          <t:Size>2048</t:Size>
          <t:Categories><t:String>Red</t:String></t:Categories>
          <t:Importance>High</t:Importance>
          <t:DateTimeSent>2026-09-30T08:14:00Z</t:DateTimeSent>
          <t:DisplayCc></t:DisplayCc>
          <t:DisplayTo>Jane Doe</t:DisplayTo>
          <t:HasAttachments>true</t:HasAttachments>
          <t:Preview>Please find attached</t:Preview>
          <t:ConversationId Id="CONV1"/>
          <t:Flag><t:FlagStatus>Flagged</t:FlagStatus></t:Flag>
          <t:From><t:Mailbox><t:Name>Bob Smith</t:Name><t:EmailAddress>bob@contoso.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox></t:From>
          <t:IsRead>false</t:IsRead>
        </t:Message>
        """;

    private static string ItemsRoot(string items, bool last = true, int total = 1) =>
        $"<m:RootFolder TotalItemsInView=\"{total}\" IncludesLastItemInRange=\"{(last ? "true" : "false")}\"><t:Items>{items}</t:Items></m:RootFolder>";

    [Fact]
    public async Task GetMessages_finds_ids_then_fetches_headers_like_thunderbird()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(IdOnly("AAA="), last: false, total: 120))))
            .ServeItems(new Dictionary<string, string> { ["AAA="] = MessageXml });
        using var p = fake.CreateProvider();

        var page = await p.GetMessagesAsync("inbox", 0, 50);

        Assert.Empty(fake.ValidationErrors);
        var find = fake.Last("FindItem");
        Assert.Equal("inbox", find.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("IdOnly", find.Descendants(T + "BaseShape").Single().Value);
        Assert.Empty(find.Descendants(T + "AdditionalProperties"));
        var get = fake.Last("GetItem");
        Assert.Equal("IdOnly", get.Descendants(T + "BaseShape").Single().Value);
        Assert.Contains(get.Descendants(T + "FieldURI"), f => f.Attribute("FieldURI")!.Value == "item:Preview");

        Assert.True(page.HasMore);
        Assert.Equal(120, page.TotalCount);
        var m = Assert.Single(page.Items);
        Assert.Equal("AAA=", m.Id);
        Assert.Equal("Quarterly report", m.Subject);
        Assert.Equal("bob@contoso.com", m.From!.Address);
        Assert.Equal("Bob Smith", m.From.Name);
        Assert.False(m.IsRead);
        Assert.True(m.HasAttachments);
        Assert.Equal(Importance.High, m.Importance);
        Assert.Equal(FlagStatus.Flagged, m.Flag);
        Assert.Equal("Please find attached", m.Preview);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 15, 0, TimeSpan.Zero), m.DateReceived);
        Assert.Equal(new[] { "Red" }, m.Categories);
    }
    [Fact]
    public async Task Exchange2010_uses_extended_flag_property_instead_of_item_Flag()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(IdOnly("X")))))
            .ServeItems(new Dictionary<string, string>
            {
                ["X"] = """
                    <t:Message><t:ItemId Id="X"/><t:Subject>s</t:Subject>
                      <t:ExtendedProperty><t:ExtendedFieldURI PropertyTag="0x1090" PropertyType="Integer"/><t:Value>2</t:Value></t:ExtendedProperty>
                      <t:IsRead>true</t:IsRead></t:Message>
                    """,
            });
        using var p = fake.CreateProvider(ExchangeServerVersion.Exchange2010_SP2);
        var page = await p.GetMessagesAsync("inbox", 0, 10);
        Assert.Empty(fake.ValidationErrors);
        Assert.DoesNotContain(fake.Last("GetItem").Descendants(T + "FieldURI"), f => f.Attribute("FieldURI")!.Value is "item:Flag" or "item:Preview");
        Assert.Equal(FlagStatus.Flagged, page.Items[0].Flag);
    }
    private static string FolderXml(string id, string name, string parent = "ROOT", string cls = "IPF.Note", string extra = "") =>
        $"<t:Folder><t:FolderId Id=\"{id}\"/><t:ParentFolderId Id=\"{parent}\"/><t:FolderClass>{cls}</t:FolderClass>" +
        $"<t:DisplayName>{name}</t:DisplayName><t:TotalCount>10</t:TotalCount><t:ChildFolderCount>0</t:ChildFolderCount>{extra}<t:UnreadCount>3</t:UnreadCount></t:Folder>";

    private const string HiddenProp = "<t:ExtendedProperty><t:ExtendedFieldURI PropertyTag=\"0x10f4\" PropertyType=\"Boolean\"/><t:Value>true</t:Value></t:ExtendedProperty>";

    private static string WellKnownResponse() => Response("GetFolder",
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"ROOT\"/></t:Folder></m:Folders>"),
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>"),
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"TRASH\"/></t:Folder></m:Folders>"),
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"DRAFTS\"/></t:Folder></m:Folders>"),
        Error("GetFolder", "ErrorFolderNotFound"),
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"SENT\"/></t:Folder></m:Folders>"),
        Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"JUNK\"/></t:Folder></m:Folders>"));

    [Fact]
    public async Task GetFolders_follows_thunderbird_hierarchy_sync()
    {
        var details = new Dictionary<string, string>
        {
            ["INBOX"] = FolderXml("INBOX", "Inbox"),
            ["SUB"] = FolderXml("SUB", "Projects", parent: "INBOX"),
            ["HID"] = FolderXml("HID", "Sync Issues", extra: HiddenProp),
            ["ORPHAN"] = FolderXml("ORPHAN", "Conflicts", parent: "HID"),
            ["SENT"] = FolderXml("SENT", "Sent Items"),
        };
        for (int i = 0; i < 12; i++) details[$"F{i}"] = FolderXml($"F{i}", $"Folder {i}");
        var creates = string.Concat(details.Keys.Select(id => $"<t:Create><t:Folder><t:FolderId Id=\"{id}\"/></t:Folder></t:Create>"));
        var fake = new FakeEws()
            .On("GetFolder", WellKnownResponse())
            .On("SyncFolderHierarchy", Response("SyncFolderHierarchy", Success("SyncFolderHierarchy",
                $"<m:SyncState>H1</m:SyncState><m:IncludesLastFolderInRange>true</m:IncludesLastFolderInRange><m:Changes>{creates}" +
                "<t:Create><t:CalendarFolder><t:FolderId Id=\"CAL\"/></t:CalendarFolder></t:Create></m:Changes>")))
            .On("SyncFolderHierarchy", Response("SyncFolderHierarchy", Success("SyncFolderHierarchy",
                "<m:SyncState>H2</m:SyncState><m:IncludesLastFolderInRange>true</m:IncludesLastFolderInRange><m:Changes>" +
                "<t:Update><t:Folder><t:FolderId Id=\"SUB\"/></t:Folder></t:Update><t:Delete><t:FolderId Id=\"F0\"/></t:Delete></m:Changes>")))
            .ServeFolders(details);
        using var p = fake.CreateProvider();

        var folders = await p.GetFoldersAsync();

        Assert.Empty(fake.ValidationErrors);
        var getFolders = fake.All("GetFolder").ToList();
        // Well-known folders: IdOnly, the Thunderbird list.
        Assert.Equal("IdOnly", getFolders[0].Descendants(T + "BaseShape").Single().Value);
        Assert.Equal(new[] { "msgfolderroot", "inbox", "deleteditems", "drafts", "outbox", "sentitems", "junkemail" },
            getFolders[0].Descendants(T + "DistinguishedFolderId").Select(e => e.Attribute("Id")!.Value));
        // Details in batches of at most 10, never for calendar/contacts folders.
        Assert.All(getFolders.Skip(1), g => Assert.True(g.Descendants(T + "FolderId").Count() <= 10));
        Assert.DoesNotContain(getFolders.SelectMany(g => g.Descendants(T + "FolderId")), e => e.Attribute("Id")!.Value == "CAL");
        Assert.Equal("IdOnly", fake.Last("SyncFolderHierarchy").Descendants(T + "BaseShape").Single().Value);

        Assert.Contains(folders, f => f.Id == "ROOT" && f.WellKnown == WellKnownFolder.Root && f.ParentId == null);
        var inbox = Assert.Single(folders, f => f.Id == "INBOX");
        Assert.Equal(WellKnownFolder.Inbox, inbox.WellKnown);
        Assert.Equal(3, inbox.UnreadCount);
        Assert.Equal(WellKnownFolder.SentItems, folders.Single(f => f.Id == "SENT").WellKnown);
        Assert.Contains(folders, f => f.Id == "SUB" && f.ParentId == "INBOX");
        Assert.DoesNotContain(folders, f => f.Id is "HID" or "ORPHAN" or "CAL");

        // Second call: incremental, only the changes travel.
        int before = fake.All("GetFolder").Count();
        var again = await p.GetFoldersAsync();
        Assert.Equal("H1", fake.Last("SyncFolderHierarchy").Element(M + "SyncState")!.Value);
        Assert.Equal(new[] { "SUB" }, fake.All("GetFolder").Skip(before).SelectMany(g => g.Descendants(T + "FolderId")).Select(e => e.Attribute("Id")!.Value));
        Assert.DoesNotContain(again, f => f.Id == "F0");
        Assert.Contains(again, f => f.Id == "F1");
    }
    [Fact]
    public async Task SyncFolderItems_is_id_only_then_getitem_in_batches_of_ten()
    {
        var items = new Dictionary<string, string> { ["AAA="] = MessageXml };
        var creates = new System.Text.StringBuilder($"<t:Create>{IdOnly("AAA=")}</t:Create>");
        for (int i = 0; i < 14; i++)
        {
            items[$"N{i}"] = $"<t:Message><t:ItemId Id=\"N{i}\"/><t:Subject>Письмо {i}</t:Subject><t:IsRead>false</t:IsRead></t:Message>";
            creates.Append($"<t:Create>{IdOnly($"N{i}")}</t:Create>");
        }
        creates.Append($"<t:Update>{IdOnly("GONE_LATER")}</t:Update>"); // deleted between the two calls
        var fake = new FakeEws()
            .On("SyncFolderItems", Response("SyncFolderItems", Success("SyncFolderItems",
                $"""
                <m:SyncState>STATE2</m:SyncState>
                <m:IncludesLastItemInRange>true</m:IncludesLastItemInRange>
                <m:Changes>
                  {creates}
                  <t:Delete><t:ItemId Id="GONE"/></t:Delete>
                  <t:ReadFlagChange><t:ItemId Id="RD"/><t:IsRead>true</t:IsRead></t:ReadFlagChange>
                </m:Changes>
                """)))
            .ServeItems(items);
        using var p = fake.CreateProvider();

        var r = await p.SyncFolderItemsAsync("FOLDER", "STATE1", 256);

        Assert.Empty(fake.ValidationErrors);
        var sync = fake.Last("SyncFolderItems");
        Assert.Equal("STATE1", sync.Element(M + "SyncState")!.Value);
        Assert.Equal("IdOnly", sync.Descendants(T + "BaseShape").Single().Value);
        Assert.Empty(sync.Descendants(T + "AdditionalProperties"));
        Assert.Null(sync.Element(M + "SyncScope"));
        Assert.Equal("256", sync.Element(M + "MaxChangesReturned")!.Value);
        var gets = fake.All("GetItem").ToList();
        Assert.Equal(2, gets.Count);
        Assert.All(gets, g => Assert.True(g.Descendants(T + "ItemId").Count() <= 10));

        Assert.Equal("STATE2", r.SyncState);
        Assert.True(r.IncludesLastItem);
        Assert.Equal(15, r.CreatedOrUpdated.Count);
        var a = r.CreatedOrUpdated.Single(m => m.Id == "AAA=");
        Assert.Equal("Quarterly report", a.Subject);
        Assert.Equal("FOLDER", a.FolderId);
        Assert.Equal(new[] { "GONE" }, r.Deleted);
        Assert.True(r.ReadFlagChanges["RD"]);
    }
    [Fact]
    public async Task SyncFolderItems_invalid_state_raises_specific_exception()
    {
        var fake = new FakeEws().On("SyncFolderItems",
            Response("SyncFolderItems", Error("SyncFolderItems", "ErrorInvalidSyncStateData")));
        using var p = fake.CreateProvider();
        await Assert.ThrowsAsync<SyncStateInvalidException>(() => p.SyncFolderItemsAsync("F", "bad", 10));
    }

    [Fact]
    public async Task GetAttachment_decodes_file_content()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var fake = new FakeEws().On("GetAttachment", Response("GetAttachment", Success("GetAttachment",
            $"<m:Attachments><t:FileAttachment><t:AttachmentId Id=\"ATT1\"/><t:Name>a.bin</t:Name><t:ContentType>application/octet-stream</t:ContentType><t:Content>{Convert.ToBase64String(data)}</t:Content></t:FileAttachment></m:Attachments>")));
        using var p = fake.CreateProvider();

        var a = await p.GetAttachmentAsync("ATT1");

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal(data, a.Content);
        Assert.Equal("a.bin", a.Info.Name);
    }

    [Fact]
    public async Task Item_operations_produce_schema_valid_requests()
    {
        var fake = new FakeEws()
            .On("UpdateItem", Response("UpdateItem", Success("UpdateItem")))
            .On("MoveItem", Response("MoveItem", Success("MoveItem", "<m:Items><t:Message><t:ItemId Id=\"NEW1\"/></t:Message></m:Items>"), Success("MoveItem", "<m:Items/>")))
            .On("CopyItem", Response("CopyItem", Success("CopyItem", "<m:Items><t:Message><t:ItemId Id=\"COPY\"/></t:Message></m:Items>")))
            .On("DeleteItem", Response("DeleteItem", Success("DeleteItem")))
            .On("GetItem", Response("GetItem", Success("GetItem", "<m:Items><t:Message><t:ItemId Id=\"I\" ChangeKey=\"C\"/><t:MimeContent CharacterSet=\"UTF-8\">U3ViamVjdDogaGk=</t:MimeContent></t:Message></m:Items>")))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"IMP\"/></t:Message></m:Items>")));
        using var p = fake.CreateProvider();

        await p.SetReadStateAsync(new[] { "A", "B" }, true);
        await p.SetFlagAsync(new[] { "A" }, FlagStatus.Flagged);
        await p.SetCategoriesAsync("A", new[] { "Blue", "Work" });
        await p.SetCategoriesAsync("A", Array.Empty<string>());
        var moved = await p.MoveItemsAsync(new[] { "A", "B" }, "deleteditems");
        var copied = await p.CopyItemsAsync(new[] { "A" }, "FOLDERX");
        await p.DeleteItemsAsync(new[] { "A" }, permanent: true);
        var mime = await p.GetMimeContentAsync("I");
        var imported = await p.ImportMimeAsync("inbox", "Subject: hi\r\n\r\nbody"u8.ToArray());

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal(new string?[] { "NEW1", null }, moved);
        Assert.Equal("COPY", copied.Single());
        Assert.Equal("Subject: hi", System.Text.Encoding.UTF8.GetString(mime));
        Assert.Equal("IMP", imported);
        Assert.Equal("HardDelete", fake.Last("DeleteItem").Attribute("DeleteType")!.Value);
    }

    [Fact]
    public async Task Exchange2010_flag_update_is_schema_valid()
    {
        var fake = new FakeEws().On("UpdateItem", Response("UpdateItem", Success("UpdateItem")));
        using var p = fake.CreateProvider(ExchangeServerVersion.Exchange2010_SP2);
        await p.SetFlagAsync(new[] { "A" }, FlagStatus.Flagged);
        await p.SetFlagAsync(new[] { "A" }, FlagStatus.NotFlagged);
        Assert.Empty(fake.ValidationErrors);
    }

    [Fact]
    public async Task Search_falls_back_to_restriction_when_aqs_is_rejected()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Error("FindItem", "ErrorInvalidRequest", "QueryString not supported")))
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(IdOnly("AAA=")))))
            .ServeItems(new Dictionary<string, string> { ["AAA="] = MessageXml });
        using var p = fake.CreateProvider();

        var page = await p.SearchMessagesAsync("inbox", "report", 0, 20);

        Assert.Empty(fake.ValidationErrors);
        var finds = fake.All("FindItem").ToList();
        Assert.Equal("report", finds[0].Element(M + "QueryString")!.Value);
        Assert.NotNull(finds[1].Element(M + "Restriction"));
        Assert.Equal("Quarterly report", Assert.Single(page.Items).Subject);
    }
    [Fact]
    public async Task ResolveNames_parses_directory_entries_and_tolerates_multiple_results_warning()
    {
        var fake = new FakeEws().On("ResolveNames", Response("ResolveNames",
            """
            <m:ResolveNamesResponseMessage ResponseClass="Warning">
              <m:MessageText>Multiple results were found.</m:MessageText>
              <m:ResponseCode>ErrorNameResolutionMultipleResults</m:ResponseCode>
              <m:ResolutionSet TotalItemsInView="2" IncludesLastItemInRange="true">
                <t:Resolution>
                  <t:Mailbox><t:Name>Bob Smith</t:Name><t:EmailAddress>bob@contoso.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType><t:MailboxType>Mailbox</t:MailboxType></t:Mailbox>
                  <t:Contact><t:DisplayName>Bob Smith</t:DisplayName><t:CompanyName>Contoso</t:CompanyName>
                    <t:EmailAddresses><t:Entry Key="EmailAddress1">SMTP:bob@contoso.com</t:Entry></t:EmailAddresses>
                    <t:PhoneNumbers><t:Entry Key="BusinessPhone">+1 555 0100</t:Entry></t:PhoneNumbers>
                    <t:JobTitle>Engineer</t:JobTitle></t:Contact>
                </t:Resolution>
                <t:Resolution>
                  <t:Mailbox><t:Name>Bobby Tables</t:Name><t:EmailAddress>bobby@contoso.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType><t:MailboxType>Contact</t:MailboxType></t:Mailbox>
                </t:Resolution>
              </m:ResolutionSet>
            </m:ResolveNamesResponseMessage>
            """));
        using var p = fake.CreateProvider();

        var r = await p.ResolveNamesAsync("bob");

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal(2, r.Count);
        Assert.Equal("bob@contoso.com", r[0].PrimaryEmail);
        Assert.Single(r[0].EmailAddresses);
        Assert.Equal("Contoso", r[0].CompanyName);
        Assert.Equal("+1 555 0100", r[0].BusinessPhone);
        Assert.True(r[0].IsDirectoryEntry);
        Assert.False(r[1].IsDirectoryEntry);
    }

    [Fact]
    public async Task ResolveNames_no_results_is_empty_not_error()
    {
        var fake = new FakeEws().On("ResolveNames", Response("ResolveNames", Error("ResolveNames", "ErrorNameResolutionNoResults")));
        using var p = fake.CreateProvider();
        Assert.Empty(await p.ResolveNamesAsync("zzz"));
    }

    [Fact]
    public async Task Contacts_and_meeting_response_produce_schema_valid_requests()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(
                """
                <t:Contact><t:ItemId Id="C1"/><t:DisplayName>Alice</t:DisplayName><t:GivenName>Alice</t:GivenName>
                  <t:EmailAddresses><t:Entry Key="EmailAddress1">alice@contoso.com</t:Entry></t:EmailAddresses></t:Contact>
                """))))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Contact><t:ItemId Id=\"NEW\"/></t:Contact></m:Items>")))
            .On("UpdateItem", Response("UpdateItem", Success("UpdateItem")))
            .On("GetItem", Response("GetItem", Success("GetItem", "<m:Items><t:MeetingRequest><t:ItemId Id=\"MR\" ChangeKey=\"CK\"/></t:MeetingRequest></m:Items>")));
        using var p = fake.CreateProvider();

        var contacts = await p.GetContactsAsync();
        await p.CreateContactAsync(new Contact
        {
            GivenName = "Carol", Surname = "Jones", CompanyName = "Fabrikam", JobTitle = "CTO", Department = "IT",
            EmailAddresses = { "carol@fabrikam.com" }, MobilePhone = "+1 555 0101", Notes = "met at conf",
        });
        await p.UpdateContactAsync(new Contact { Id = "C1", DisplayName = "Alice A", EmailAddresses = { "alice@contoso.com" } });
        await p.RespondToMeetingAsync("MR", MeetingResponse.Tentative, "Might be late");

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("alice@contoso.com", contacts.Single().PrimaryEmail);
        Assert.NotNull(fake.Requests.Last(r => r.Name.LocalName == "CreateItem").Descendants(T + "TentativelyAcceptItem").SingleOrDefault());
    }

    [Fact]
    public async Task OutOfOffice_roundtrip_is_schema_valid()
    {
        var fake = new FakeEws()
            .On("GetUserOofSettingsRequest",
                """
                <GetUserOofSettingsResponse xmlns="http://schemas.microsoft.com/exchange/services/2006/messages">
                  <ResponseMessage ResponseClass="Success"><ResponseCode>NoError</ResponseCode></ResponseMessage>
                  <OofSettings xmlns="http://schemas.microsoft.com/exchange/services/2006/types">
                    <OofState>Scheduled</OofState><ExternalAudience>Known</ExternalAudience>
                    <Duration><StartTime>2026-10-10T00:00:00Z</StartTime><EndTime>2026-10-20T00:00:00Z</EndTime></Duration>
                    <InternalReply><Message>On holiday</Message></InternalReply>
                    <ExternalReply><Message>Away</Message></ExternalReply>
                  </OofSettings>
                  <AllowExternalOof>All</AllowExternalOof>
                </GetUserOofSettingsResponse>
                """)
            .On("SetUserOofSettingsRequest",
                """
                <SetUserOofSettingsResponse xmlns="http://schemas.microsoft.com/exchange/services/2006/messages">
                  <ResponseMessage ResponseClass="Success"><ResponseCode>NoError</ResponseCode></ResponseMessage>
                </SetUserOofSettingsResponse>
                """);
        using var p = fake.CreateProvider();

        var oof = await p.GetOutOfOfficeAsync();
        Assert.Equal(OofState.Scheduled, oof.State);
        Assert.Equal(OofExternalAudience.Known, oof.ExternalAudience);
        Assert.Equal("On holiday", oof.InternalReply);

        oof.State = OofState.Enabled;
        await p.SetOutOfOfficeAsync(oof);
        Assert.Empty(fake.ValidationErrors);
    }

    [Fact]
    public async Task Server_busy_fault_is_retried_with_backoff_hint()
    {
        const string busy = """
            <s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:ErrorServerBusy</faultcode>
              <faultstring xml:lang="en-US">The server cannot service this request right now.</faultstring>
              <detail>
                <e:ResponseCode xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">ErrorServerBusy</e:ResponseCode>
                <e:MessageXml xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">
                  <t:Value Name="BackOffMilliseconds" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">5</t:Value>
                </e:MessageXml>
              </detail>
            </s:Fault>
            """;
        var fake = new FakeEws()
            .On("FindItem", busy, HttpStatusCode.InternalServerError)
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(IdOnly("AAA=")))))
            .ServeItems(new Dictionary<string, string> { ["AAA="] = MessageXml });
        using var p = fake.CreateProvider();

        var page = await p.GetMessagesAsync("inbox", 0, 10);

        Assert.Equal(2, fake.All("FindItem").Count());
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Server_busy_response_message_uses_message_xml_backoff()
    {
        const string busy = """
            <m:FindItemResponseMessage ResponseClass="Error"><m:MessageText>busy</m:MessageText><m:ResponseCode>ErrorServerBusy</m:ResponseCode>
              <m:DescriptiveLinkKey>0</m:DescriptiveLinkKey>
              <m:MessageXml><t:Value Name="BackOffMilliseconds">10</t:Value></m:MessageXml></m:FindItemResponseMessage>
            """;
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", busy))
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(IdOnly("AAA=")))))
            .ServeItems(new Dictionary<string, string> { ["AAA="] = MessageXml });
        using var p = fake.CreateProvider();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = await p.GetMessagesAsync("inbox", 0, 10);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed} instead of the server's 10 ms");
        Assert.Equal(2, fake.All("FindItem").Count());
        Assert.Single(page.Items);
    }

    private sealed class DropOnce : HttpMessageHandler
    {
        private readonly FakeEws _inner;
        public int Drops;
        public DropOnce(FakeEws inner) => _inner = inner;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Drops++ == 0) throw new HttpRequestException("The response ended prematurely.", new IOException("connection reset"));
            return new HttpMessageInvoker(_inner).SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Read_operations_are_retried_after_a_dropped_connection()
    {
        var fake = new FakeEws().On("GetFolder", Response("GetFolder", Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"R\"/></t:Folder></m:Folders>")));
        var drop = new DropOnce(fake);
        using var p = new ExchangeProvider(Account(), new HttpClient(drop));

        await p.ConnectAsync();

        Assert.Equal(2, drop.Drops);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Sending_is_never_retried_automatically_to_avoid_duplicates()
    {
        var fake = new FakeEws().On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items/>")));
        var drop = new DropOnce(fake);
        using var p = new ExchangeProvider(Account(), new HttpClient(drop));

        var ex = await Assert.ThrowsAsync<MailConnectionException>(() =>
            p.SendAsync(new OutgoingMessage { To = { new EmailAddress("", "a@b.ru") }, Subject = "x", Body = "y" }));

        Assert.Equal(1, drop.Drops);
        Assert.Empty(fake.Requests);
        Assert.Contains("CreateItem", ex.Message);
    }

    [Fact]
    public async Task Folder_operations_follow_thunderbird()
    {
        var fake = new FakeEws()
            .On("CreateFolder", Response("CreateFolder", Success("CreateFolder", "<m:Folders><t:Folder><t:FolderId Id=\"NEWF\" ChangeKey=\"K\"/></t:Folder></m:Folders>")))
            .On("UpdateFolder", Response("UpdateFolder", Success("UpdateFolder")))
            .On("MoveFolder", Response("MoveFolder", Success("MoveFolder")))
            .On("DeleteFolder", Response("DeleteFolder", Success("DeleteFolder")))
            .On("EmptyFolder", Response("EmptyFolder", Success("EmptyFolder")));
        using var p = fake.CreateProvider();

        var f = await p.CreateFolderAsync("inbox", "Проекты");
        await p.RenameFolderAsync("NEWF", "Проекты 2026");
        await p.DeleteFolderAsync("NEWF", permanent: false);
        await p.DeleteFolderAsync("NEWF", permanent: true);
        await p.EmptyFolderAsync("deleteditems", false);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("NEWF", f.Id);
        Assert.Equal("IPF.Note", fake.Last("CreateFolder").Descendants(T + "FolderClass").Single().Value);
        // Not permanent: moved to Deleted Items; permanent: HardDelete (erase_folder.rs).
        Assert.Equal("deleteditems", fake.Last("MoveFolder").Element(M + "ToFolderId")!.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("HardDelete", fake.Last("DeleteFolder").Attribute("DeleteType")!.Value);
        var empty = fake.Last("EmptyFolder");
        Assert.Equal("HardDelete", empty.Attribute("DeleteType")!.Value);
        Assert.Equal("true", empty.Attribute("DeleteSubFolders")!.Value);
    }

    // ------------------------------------------------------------------ MIME-based messages (Thunderbird)

    private static byte[] Mime(string subject, string html, string? attachment = null, string? messageId = null)
    {
        var m = new MimeKit.MimeMessage();
        m.From.Add(new MimeKit.MailboxAddress("Петров Пётр", "petrov@contoso.ru"));
        m.To.Add(new MimeKit.MailboxAddress("Иванов Иван", "ivanov@contoso.ru"));
        m.Subject = subject;
        m.MessageId = messageId ?? MimeKit.Utils.MimeUtils.GenerateMessageId("contoso.ru");
        var b = new MimeKit.BodyBuilder { HtmlBody = html };
        var logo = b.LinkedResources.Add("logo.png", new byte[] { 137, 80, 78, 71 }, new MimeKit.ContentType("image", "png"));
        logo.ContentId = "logo@x";
        if (attachment != null) b.Attachments.Add(attachment, "данные"u8.ToArray(), new MimeKit.ContentType("text", "plain"));
        m.Body = b.ToMessageBody();
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    private static string MimeItem(string id, byte[] mime, bool read = false) =>
        $"<t:Message><t:MimeContent CharacterSet=\"UTF-8\">{Convert.ToBase64String(mime)}</t:MimeContent><t:ItemId Id=\"{id}\" ChangeKey=\"CK\"/>" +
        $"<t:ItemClass>IPM.Note</t:ItemClass><t:Subject>s</t:Subject><t:IsRead>{(read ? "true" : "false")}</t:IsRead></t:Message>";

    private static MimeKit.MimeMessage SentMime(XElement createItem) =>
        MimeKit.MimeMessage.Load(new MemoryStream(Convert.FromBase64String(createItem.Descendants(T + "MimeContent").Single().Value)));

    [Fact]
    public async Task GetMessage_reads_mime_like_thunderbird_and_serves_attachments_from_it()
    {
        var fake = new FakeEws().ServeItems(new Dictionary<string, string>
        {
            ["M1"] = MimeItem("M1", Mime("Отчёт", "<p>Привет <img src=\"cid:logo@x\"></p>", "Отчёт 2026.txt")),
        });
        using var p = fake.CreateProvider();

        var m = await p.GetMessageAsync("M1");

        Assert.Empty(fake.ValidationErrors);
        var get = fake.Last("GetItem");
        Assert.Equal("IdOnly", get.Descendants(T + "BaseShape").Single().Value);
        Assert.Equal("true", get.Descendants(T + "IncludeMimeContent").Single().Value);
        Assert.Equal("Отчёт", m.Subject);
        Assert.True(m.BodyIsHtml);
        Assert.Contains("cid:logo@x", m.Body);
        Assert.Equal("petrov@contoso.ru", m.From!.Address);
        Assert.Equal("ivanov@contoso.ru", m.To.Single().Address);
        Assert.False(m.IsRead);
        Assert.Equal(2, m.Attachments.Count);
        var inline = m.Attachments.Single(a => a.IsInline);
        Assert.Equal("logo@x", inline.ContentId);
        var file = m.Attachments.Single(a => !a.IsInline);
        Assert.Equal("Отчёт 2026.txt", file.Name);

        // Attachments come from the already downloaded MIME: no extra server round trip.
        int before = fake.Requests.Count;
        var content = await p.GetAttachmentsAsync(new[] { file.Id, inline.Id });
        Assert.Equal(before, fake.Requests.Count);
        Assert.Equal("данные", System.Text.Encoding.UTF8.GetString(content[0].Content));
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, content[1].Content);
    }

    [Fact]
    public async Task Send_uses_mime_sendonly_with_separate_bcc_then_saves_sent_copy()
    {
        var fake = new FakeEws()
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items/>")))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"SENT1\"/></t:Message></m:Items>")));
        using var p = fake.CreateProvider();

        await p.SendAsync(new OutgoingMessage
        {
            To = { new EmailAddress("Боб", "bob@contoso.ru") },
            Bcc = { new EmailAddress("", "secret@contoso.ru") },
            Subject = "Договор",
            Body = "<p>Добрый день</p>",
            Importance = Importance.High,
            RequestDeliveryReceipt = true,
            Attachments = { new OutgoingAttachment { Name = "Договор №5.pdf", ContentType = "application/pdf", Content = new byte[] { 37, 80, 68, 70 } } },
        });

        Assert.Empty(fake.ValidationErrors);
        var creates = fake.All("CreateItem").ToList();
        Assert.Equal(2, creates.Count);
        Assert.Empty(fake.All("CreateAttachment"));

        var send = creates[0];
        Assert.Equal("SendOnly", send.Attribute("MessageDisposition")!.Value);
        Assert.Null(send.Element(M + "SavedItemFolderId"));
        var transmitted = SentMime(send);
        Assert.Equal("Договор", transmitted.Subject);
        Assert.Equal("bob@contoso.ru", transmitted.To.Mailboxes.Single().Address);
        Assert.Empty(transmitted.Bcc);                       // never visible to recipients
        Assert.Equal("secret@contoso.ru", send.Descendants(T + "BccRecipients").Single().Descendants(T + "EmailAddress").Single().Value);
        Assert.Equal("true", send.Descendants(T + "IsDeliveryReceiptRequested").Single().Value);
        Assert.Equal($"<{transmitted.MessageId}>", send.Descendants(T + "InternetMessageId").Single().Value);
        Assert.Equal(MimeKit.MessageImportance.High, transmitted.Importance);
        Assert.Equal("Договор №5.pdf", transmitted.Attachments.OfType<MimeKit.MimePart>().Single().FileName);

        var copy = creates[1];
        Assert.Equal("SaveOnly", copy.Attribute("MessageDisposition")!.Value);
        Assert.Equal("sentitems", copy.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("3", copy.Descendants(T + "ExtendedProperty").Single().Element(T + "Value")!.Value); // READ|UNMODIFIED
        Assert.Equal("secret@contoso.ru", SentMime(copy).Bcc.Mailboxes.Single().Address); // the sender keeps the Bcc list
    }

    [Theory]
    [InlineData(ComposeAction.Reply)]
    [InlineData(ComposeAction.ReplyAll)]
    [InlineData(ComposeAction.Forward)]
    public async Task Reply_and_forward_are_composed_locally_from_the_original_mime(ComposeAction action)
    {
        var originalId = MimeKit.Utils.MimeUtils.GenerateMessageId("contoso.ru");
        var fake = new FakeEws()
            .ServeItems(new Dictionary<string, string> { ["ORIG"] = MimeItem("ORIG", Mime("Вопрос", "<p>Подскажите сроки</p>", "ТЗ.txt", originalId)) })
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items/>")))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"S\"/></t:Message></m:Items>")));
        using var p = fake.CreateProvider();

        await p.SendAsync(new OutgoingMessage
        {
            Action = action,
            ReferenceItemId = "ORIG",
            To = { new EmailAddress("Петров Пётр", "petrov@contoso.ru") },
            Subject = action == ComposeAction.Forward ? "FW: Вопрос" : "RE: Вопрос",
            Body = "<p>До пятницы.</p>",
        });

        Assert.Empty(fake.ValidationErrors);
        Assert.Empty(fake.Requests.Where(r => r.Descendants(T + "ReplyToItem").Any() || r.Descendants(T + "ForwardItem").Any()));
        var sent = SentMime(fake.All("CreateItem").First());
        Assert.Contains("До пятницы.", sent.HtmlBody);
        Assert.Contains("Подскажите сроки", sent.HtmlBody);
        if (action == ComposeAction.Forward)
        {
            Assert.Contains("Пересылаемое сообщение", sent.HtmlBody);
            Assert.Equal("ТЗ.txt", sent.Attachments.OfType<MimeKit.MimePart>().Single().FileName);
        }
        else
        {
            Assert.Contains("Исходное сообщение", sent.HtmlBody);
            Assert.Equal(originalId, sent.InReplyTo);
            Assert.Contains(originalId, sent.References);
        }
    }

    [Fact]
    public async Task Drafts_and_imports_use_thunderbird_message_flags()
    {
        var fake = new FakeEws().On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"D\" ChangeKey=\"C\"/></t:Message></m:Items>")));
        var account = Account();
        account.SharedMailbox = "team@contoso.ru";
        using var p = new ExchangeProvider(account, new HttpClient(fake));

        var id = await p.SaveDraftAsync(new OutgoingMessage { Subject = "черновик", Body = "x", Bcc = { new EmailAddress("", "b@contoso.ru") } });
        await p.ImportMimeAsync("inbox", Mime("Импорт", "<p>x</p>"));

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("D", id);
        var (draft, import) = (fake.All("CreateItem").First(), fake.All("CreateItem").Last());
        Assert.Equal("SaveOnly", draft.Attribute("MessageDisposition")!.Value);
        var draftFolder = draft.Descendants(T + "DistinguishedFolderId").Single();
        Assert.Equal("drafts", draftFolder.Attribute("Id")!.Value);
        Assert.Equal("team@contoso.ru", draftFolder.Descendants(T + "EmailAddress").Single().Value);
        Assert.Equal("9", draft.Descendants(T + "ExtendedProperty").Single().Element(T + "Value")!.Value);  // READ|UNSENT
        Assert.Equal("team@contoso.ru", SentMime(draft).From.Mailboxes.Single().Address);
        Assert.Equal("b@contoso.ru", SentMime(draft).Bcc.Mailboxes.Single().Address);
        Assert.Equal("3", import.Descendants(T + "ExtendedProperty").Single().Element(T + "Value")!.Value); // READ|UNMODIFIED
    }

    [Fact]
    public async Task Read_status_is_updated_like_thunderbird_without_changekey()
    {
        var fake = new FakeEws().On("UpdateItem", Response("UpdateItem", Success("UpdateItem"), Success("UpdateItem")));
        using var p = fake.CreateProvider();

        await p.SetReadStateAsync(new[] { "A", "B" }, true);

        Assert.Empty(fake.ValidationErrors);
        var req = fake.Last("UpdateItem");
        Assert.Equal("AlwaysOverwrite", req.Attribute("ConflictResolution")!.Value);
        Assert.Equal("SaveOnly", req.Attribute("MessageDisposition")!.Value);
        Assert.All(req.Descendants(T + "ItemId"), id => Assert.Null(id.Attribute("ChangeKey")));
        Assert.Equal(2, req.Descendants(T + "ItemChange").Count());
    }

    [Fact]
    public async Task Read_status_error_for_a_message_deleted_meanwhile_is_ignored()
    {
        var fake = new FakeEws().On("UpdateItem", Response("UpdateItem", Success("UpdateItem"), Error("UpdateItem", "ErrorItemNotFound")));
        using var p = fake.CreateProvider();
        await p.SetReadStateAsync(new[] { "A", "GONE" }, true);
    }

    [Theory]
    [InlineData(ExchangeServerVersion.Exchange2016, FlagStatus.Flagged, "2", true)]
    [InlineData(ExchangeServerVersion.Exchange2016, FlagStatus.NotFlagged, "0", true)]
    [InlineData(ExchangeServerVersion.Exchange2010_SP2, FlagStatus.Flagged, "2", false)]
    public async Task Flag_sets_item_flag_and_pr_flag_status_like_thunderbird(ExchangeServerVersion version, FlagStatus flag, string pid, bool itemFlag)
    {
        var fake = new FakeEws().On("UpdateItem", Response("UpdateItem", Success("UpdateItem")));
        using var p = fake.CreateProvider(version);

        await p.SetFlagAsync(new[] { "A" }, flag);

        Assert.Empty(fake.ValidationErrors);
        var req = fake.Last("UpdateItem");
        Assert.Equal("AlwaysOverwrite", req.Attribute("ConflictResolution")!.Value);
        Assert.Equal(pid, req.Descendants(T + "ExtendedProperty").Single().Element(T + "Value")!.Value);
        Assert.Equal(itemFlag, req.Descendants(T + "FlagStatus").Any());
    }

    [Fact]
    public async Task Mark_all_read_and_junk_use_the_dedicated_operations()
    {
        var fake = new FakeEws()
            .On("MarkAllItemsAsRead", Response("MarkAllItemsAsRead", Success("MarkAllItemsAsRead")))
            .On("MarkAsJunk", Response("MarkAsJunk",
                Success("MarkAsJunk", "<m:MovedItemId Id=\"J1\"/>"), Success("MarkAsJunk", "<m:MovedItemId Id=\"J2\"/>")));
        using var p = fake.CreateProvider();

        Assert.True(await p.MarkAllReadAsync("inbox", true));
        var moved = await p.MarkAsJunkAsync(new[] { "A", "B" }, isJunk: true);

        Assert.Empty(fake.ValidationErrors);
        var all = fake.Last("MarkAllItemsAsRead");
        Assert.Equal("true", all.Element(M + "ReadFlag")!.Value);
        Assert.Equal("true", all.Element(M + "SuppressReadReceipts")!.Value);
        var junk = fake.Last("MarkAsJunk");
        Assert.Equal("true", junk.Attribute("IsJunk")!.Value);
        Assert.Equal("true", junk.Attribute("MoveItem")!.Value);
        Assert.Equal(new[] { "J1", "J2" }, moved);
    }

    [Fact]
    public async Task Exchange2010_falls_back_for_mark_all_read_and_junk()
    {
        var fake = new FakeEws().On("MoveItem", Response("MoveItem", Success("MoveItem", "<m:Items><t:Message><t:ItemId Id=\"N\"/></t:Message></m:Items>")));
        using var p = fake.CreateProvider(ExchangeServerVersion.Exchange2010_SP2);

        Assert.False(await p.MarkAllReadAsync("inbox", true));
        var moved = await p.MarkAsJunkAsync(new[] { "A" }, isJunk: true);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("junkemail", fake.Last("MoveItem").Element(M + "ToFolderId")!.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("N", moved.Single());
    }

    [Fact]
    public async Task Delete_moves_to_deleted_items_permanent_delete_is_hard_delete()
    {
        var fake = new FakeEws()
            .On("MoveItem", Response("MoveItem", Success("MoveItem", "<m:Items><t:Message><t:ItemId Id=\"N\"/></t:Message></m:Items>")))
            .On("DeleteItem", Response("DeleteItem", Success("DeleteItem"), Error("DeleteItem", "ErrorItemNotFound")));
        using var p = fake.CreateProvider();

        await p.DeleteItemsAsync(new[] { "A" }, permanent: false);
        await p.DeleteItemsAsync(new[] { "A", "GONE" }, permanent: true);

        Assert.Empty(fake.ValidationErrors);
        var move = fake.Last("MoveItem");
        Assert.Equal("deleteditems", move.Element(M + "ToFolderId")!.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("true", move.Element(M + "ReturnNewItemIds")!.Value);
        Assert.Equal("HardDelete", fake.Last("DeleteItem").Attribute("DeleteType")!.Value);
    }

    [Fact]
    public async Task Http_401_raises_authentication_exception()
    {
        var fake = new FakeEws().On("GetFolder", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var p = fake.CreateProvider();
        await Assert.ThrowsAsync<MailAuthenticationException>(() => p.ConnectAsync());
    }

    [Fact]
    public async Task Response_error_surfaces_code_and_message()
    {
        var fake = new FakeEws().On("GetItem", Response("GetItem", Error("GetItem", "ErrorItemNotFound", "The specified object was not found in the store.")));
        using var p = fake.CreateProvider();
        var ex = await Assert.ThrowsAsync<EwsResponseException>(() => p.GetMessageAsync("missing"));
        Assert.Equal("ErrorItemNotFound", ex.ErrorCode);
        Assert.Contains("не найден", ex.Message);
    }

    [Fact]
    public async Task Connect_reports_server_version()
    {
        var fake = new FakeEws().On("GetFolder", Response("GetFolder", Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>")));
        using var p = fake.CreateProvider();
        var info = await p.ConnectAsync();
        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("15.2.1544.4", info.ServerVersion);
        Assert.Equal("jane@contoso.com", info.EmailAddress);
    }
}
