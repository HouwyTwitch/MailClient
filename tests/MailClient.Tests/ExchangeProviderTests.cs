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
    public async Task GetMessages_builds_valid_FindItem_and_parses_summaries()
    {
        var fake = new FakeEws().On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(MessageXml, last: false, total: 120))));
        using var p = fake.CreateProvider();

        var page = await p.GetMessagesAsync("inbox", 0, 50);

        Assert.Empty(fake.ValidationErrors);
        var req = fake.Last("FindItem");
        Assert.Equal("inbox", req.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("50", req.Element(M + "IndexedPageItemView")!.Attribute("MaxEntriesReturned")!.Value);

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
        var fake = new FakeEws().On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(
            """
            <t:Message><t:ItemId Id="X"/><t:Subject>s</t:Subject>
              <t:ExtendedProperty><t:ExtendedFieldURI PropertyTag="0x1090" PropertyType="Integer"/><t:Value>2</t:Value></t:ExtendedProperty>
              <t:IsRead>true</t:IsRead></t:Message>
            """))));
        using var p = fake.CreateProvider(ExchangeServerVersion.Exchange2010_SP2);
        var page = await p.GetMessagesAsync("inbox", 0, 10);
        Assert.Empty(fake.ValidationErrors);
        Assert.DoesNotContain(fake.Last("FindItem").Descendants(T + "FieldURI"), f => f.Attribute("FieldURI")!.Value == "item:Flag");
        Assert.Equal(FlagStatus.Flagged, page.Items[0].Flag);
    }

    [Fact]
    public async Task GetFolders_maps_well_known_folders_and_skips_hidden()
    {
        static string Folder(string id, string name, string parent = "ROOT", string cls = "IPF.Note", string extra = "") =>
            $"<t:Folder><t:FolderId Id=\"{id}\"/><t:ParentFolderId Id=\"{parent}\"/><t:FolderClass>{cls}</t:FolderClass>" +
            $"<t:DisplayName>{name}</t:DisplayName><t:TotalCount>10</t:TotalCount><t:ChildFolderCount>0</t:ChildFolderCount>{extra}<t:UnreadCount>3</t:UnreadCount></t:Folder>";

        var getFolderMessages = new List<string>();
        foreach (var name in Ews.DistinguishedFolders.Keys)
        {
            getFolderMessages.Add(name switch
            {
                "msgfolderroot" => Success("GetFolder", $"<m:Folders>{Folder("ROOT", "Top of Information Store", "X")}</m:Folders>"),
                "inbox" => Success("GetFolder", $"<m:Folders>{Folder("INBOX", "Inbox")}</m:Folders>"),
                "notes" => Error("GetFolder", "ErrorFolderNotFound"),
                _ => Success("GetFolder", $"<m:Folders>{Folder(name.ToUpperInvariant(), name)}</m:Folders>"),
            });
        }
        const string hidden = "<t:ExtendedProperty><t:ExtendedFieldURI PropertyTag=\"0x10f4\" PropertyType=\"Boolean\"/><t:Value>true</t:Value></t:ExtendedProperty>";
        var fake = new FakeEws()
            .On("GetFolder", Response("GetFolder", getFolderMessages.ToArray()))
            .On("FindFolder", Response("FindFolder", Success("FindFolder",
                "<m:RootFolder IncludesLastItemInRange=\"true\"><t:Folders>" +
                Folder("INBOX", "Inbox") +
                Folder("SUB", "Projects", parent: "INBOX") +
                Folder("HID", "Sync Issues", extra: hidden) +
                Folder("ORPHAN", "Conflicts", parent: "HID") +
                "<t:CalendarFolder><t:FolderId Id=\"CAL\"/><t:ParentFolderId Id=\"ROOT\"/><t:DisplayName>Calendar</t:DisplayName></t:CalendarFolder>" +
                "</t:Folders></m:RootFolder>")));
        using var p = fake.CreateProvider();

        var folders = await p.GetFoldersAsync();

        Assert.Empty(fake.ValidationErrors);
        Assert.Contains(folders, f => f.Id == "ROOT" && f.WellKnown == WellKnownFolder.Root && f.ParentId == null);
        var inbox = Assert.Single(folders, f => f.Id == "INBOX");
        Assert.Equal(WellKnownFolder.Inbox, inbox.WellKnown);
        Assert.Equal(3, inbox.UnreadCount);
        Assert.Contains(folders, f => f.Id == "SUB" && f.ParentId == "INBOX");
        Assert.DoesNotContain(folders, f => f.Id == "HID");
        Assert.DoesNotContain(folders, f => f.Id == "ORPHAN");
        Assert.Equal(FolderKind.Calendar, folders.Single(f => f.Id == "CAL").Kind);
    }

    [Fact]
    public async Task SyncFolderItems_parses_all_change_types()
    {
        var fake = new FakeEws().On("SyncFolderItems", Response("SyncFolderItems", Success("SyncFolderItems",
            $"""
            <m:SyncState>STATE2</m:SyncState>
            <m:IncludesLastItemInRange>true</m:IncludesLastItemInRange>
            <m:Changes>
              <t:Create>{MessageXml}</t:Create>
              <t:Delete><t:ItemId Id="GONE"/></t:Delete>
              <t:ReadFlagChange><t:ItemId Id="RD"/><t:IsRead>true</t:IsRead></t:ReadFlagChange>
            </m:Changes>
            """)));
        using var p = fake.CreateProvider();

        var r = await p.SyncFolderItemsAsync("FOLDER", "STATE1", 100);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("STATE1", fake.Last("SyncFolderItems").Element(M + "SyncState")!.Value);
        Assert.Equal("STATE2", r.SyncState);
        Assert.True(r.IncludesLastItem);
        Assert.Equal("AAA=", Assert.Single(r.CreatedOrUpdated).Id);
        Assert.Equal("FOLDER", r.CreatedOrUpdated[0].FolderId);
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
    public async Task GetMessage_parses_recipients_body_and_attachments()
    {
        var fake = new FakeEws().On("GetItem", Response("GetItem", Success("GetItem", """
            <m:Items><t:Message>
              <t:ItemId Id="AAA=" ChangeKey="CK"/>
              <t:Subject>Hi</t:Subject>
              <t:Body BodyType="HTML">&lt;p&gt;Hello &lt;img src="cid:logo@x"&gt;&lt;/p&gt;</t:Body>
              <t:Attachments>
                <t:FileAttachment><t:AttachmentId Id="ATT1"/><t:Name>report.pdf</t:Name><t:ContentType>application/pdf</t:ContentType><t:Size>12345</t:Size><t:IsInline>false</t:IsInline></t:FileAttachment>
                <t:FileAttachment><t:AttachmentId Id="ATT2"/><t:Name>logo.png</t:Name><t:ContentType>image/png</t:ContentType><t:ContentId>logo@x</t:ContentId><t:IsInline>true</t:IsInline></t:FileAttachment>
                <t:ItemAttachment><t:AttachmentId Id="ATT3"/><t:Name>Fwd mail</t:Name></t:ItemAttachment>
              </t:Attachments>
              <t:ToRecipients><t:Mailbox><t:Name>Jane</t:Name><t:EmailAddress>jane@contoso.com</t:EmailAddress></t:Mailbox><t:Mailbox><t:EmailAddress>al@contoso.com</t:EmailAddress></t:Mailbox></t:ToRecipients>
              <t:CcRecipients><t:Mailbox><t:EmailAddress>cc@contoso.com</t:EmailAddress></t:Mailbox></t:CcRecipients>
              <t:From><t:Mailbox><t:Name>Bob</t:Name><t:EmailAddress>bob@contoso.com</t:EmailAddress></t:Mailbox></t:From>
              <t:InternetMessageId>&lt;abc@contoso.com&gt;</t:InternetMessageId>
              <t:IsRead>true</t:IsRead>
            </t:Message></m:Items>
            """)));
        using var p = fake.CreateProvider();

        var m = await p.GetMessageAsync("AAA=");

        Assert.Empty(fake.ValidationErrors);
        Assert.True(m.BodyIsHtml);
        Assert.Contains("cid:logo@x", m.Body);
        Assert.Equal(2, m.To.Count);
        Assert.Equal("cc@contoso.com", m.Cc.Single().Address);
        Assert.Equal(3, m.Attachments.Count);
        Assert.True(m.Attachments[1].IsInline);
        Assert.Equal("logo@x", m.Attachments[1].ContentId);
        Assert.True(m.Attachments[2].IsItemAttachment);
        Assert.Equal("Fwd mail.eml", m.Attachments[2].Name);
        Assert.Equal("<abc@contoso.com>", m.InternetMessageId);
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
    public async Task Send_without_attachments_uses_single_CreateItem()
    {
        var fake = new FakeEws().On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items/>")));
        using var p = fake.CreateProvider();

        await p.SendAsync(new OutgoingMessage
        {
            To = { new EmailAddress("Bob", "bob@contoso.com") },
            Cc = { new EmailAddress("", "cc@contoso.com") },
            Subject = "Hello",
            Body = "<p>Hi</p>",
            Importance = Importance.High,
            RequestReadReceipt = true,
        });

        Assert.Empty(fake.ValidationErrors);
        var req = fake.Last("CreateItem");
        Assert.Equal("SendAndSaveCopy", req.Attribute("MessageDisposition")!.Value);
        var msg = req.Descendants(T + "Message").Single();
        Assert.Equal("Hello", msg.Element(T + "Subject")!.Value);
        Assert.Equal("HTML", msg.Element(T + "Body")!.Attribute("BodyType")!.Value);
        Assert.Equal("bob@contoso.com", msg.Element(T + "ToRecipients")!.Descendants(T + "EmailAddress").Single().Value);
    }

    [Fact]
    public async Task Send_with_attachments_creates_draft_uploads_then_sends_with_latest_changekey()
    {
        var fake = new FakeEws()
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"DRAFT\" ChangeKey=\"CK0\"/></t:Message></m:Items>")))
            .On("CreateAttachment", Response("CreateAttachment", Success("CreateAttachment",
                "<m:Attachments><t:FileAttachment><t:AttachmentId Id=\"A1\" RootItemId=\"DRAFT\" RootItemChangeKey=\"CK1\"/></t:FileAttachment></m:Attachments>")))
            .On("CreateAttachment", Response("CreateAttachment", Success("CreateAttachment",
                "<m:Attachments><t:FileAttachment><t:AttachmentId Id=\"A2\" RootItemId=\"DRAFT\" RootItemChangeKey=\"CK2\"/></t:FileAttachment></m:Attachments>")))
            .On("SendItem", Response("SendItem", Success("SendItem")));
        using var p = fake.CreateProvider();

        await p.SendAsync(new OutgoingMessage
        {
            To = { new EmailAddress("", "bob@contoso.com") },
            Subject = "Files",
            Body = "see attached",
            Attachments =
            {
                new OutgoingAttachment { Name = "a.txt", ContentType = "text/plain", Content = "hello"u8.ToArray() },
                new OutgoingAttachment { Name = "b.png", ContentType = "image/png", Content = new byte[] { 9, 9 }, IsInline = true, ContentId = "img1" },
            },
        });

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal(new[] { "CreateItem", "CreateAttachment", "CreateAttachment", "SendItem" }, fake.Requests.Select(r => r.Name.LocalName));
        Assert.Equal("SaveOnly", fake.Requests[0].Attribute("MessageDisposition")!.Value);
        Assert.Equal("CK0", fake.Requests[1].Element(M + "ParentItemId")!.Attribute("ChangeKey")!.Value);
        Assert.Equal("CK1", fake.Requests[2].Element(M + "ParentItemId")!.Attribute("ChangeKey")!.Value);
        Assert.Equal("aGVsbG8=", fake.Requests[1].Descendants(T + "Content").Single().Value);
        var sendId = fake.Last("SendItem").Descendants(T + "ItemId").Single();
        Assert.Equal("DRAFT", sendId.Attribute("Id")!.Value);
        Assert.Equal("CK2", sendId.Attribute("ChangeKey")!.Value);
    }

    [Theory]
    [InlineData(ComposeAction.Reply, "ReplyToItem")]
    [InlineData(ComposeAction.ReplyAll, "ReplyAllToItem")]
    [InlineData(ComposeAction.Forward, "ForwardItem")]
    public async Task Reply_and_forward_use_response_objects(ComposeAction action, string element)
    {
        var fake = new FakeEws()
            .On("GetItem", Response("GetItem", Success("GetItem", "<m:Items><t:Message><t:ItemId Id=\"ORIG\" ChangeKey=\"CKO\"/></t:Message></m:Items>")))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items/>")));
        using var p = fake.CreateProvider();

        await p.SendAsync(new OutgoingMessage
        {
            Action = action,
            ReferenceItemId = "ORIG",
            To = { new EmailAddress("Bob", "bob@contoso.com") },
            Subject = "RE: Hello",
            Body = "<p>Thanks!</p>",
        });

        Assert.Empty(fake.ValidationErrors);
        var resp = fake.Last("CreateItem").Descendants(T + element).Single();
        var refId = resp.Element(T + "ReferenceItemId")!;
        Assert.Equal("ORIG", refId.Attribute("Id")!.Value);
        Assert.Equal("CKO", refId.Attribute("ChangeKey")!.Value);
        Assert.Equal("<p>Thanks!</p>", resp.Element(T + "NewBodyContent")!.Value);
    }

    [Fact]
    public async Task Save_draft_for_shared_mailbox_sets_from_and_mailbox_scoped_folder()
    {
        var fake = new FakeEws().On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Message><t:ItemId Id=\"D\" ChangeKey=\"C\"/></t:Message></m:Items>")));
        var account = Account();
        account.SharedMailbox = "team@contoso.com";
        using var p = new ExchangeProvider(account, new HttpClient(fake));

        var id = await p.SaveDraftAsync(new OutgoingMessage { Subject = "draft", Body = "x" });

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("D", id);
        var req = fake.Last("CreateItem");
        Assert.Equal("team@contoso.com", req.Descendants(T + "DistinguishedFolderId").Single().Descendants(T + "EmailAddress").Single().Value);
        Assert.Equal("team@contoso.com", req.Descendants(T + "From").Single().Descendants(T + "EmailAddress").Single().Value);
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
    public async Task Folder_operations_produce_schema_valid_requests()
    {
        var fake = new FakeEws()
            .On("CreateFolder", Response("CreateFolder", Success("CreateFolder", "<m:Folders><t:Folder><t:FolderId Id=\"NEWF\" ChangeKey=\"K\"/></t:Folder></m:Folders>")))
            .On("UpdateFolder", Response("UpdateFolder", Success("UpdateFolder")))
            .On("MoveFolder", Response("MoveFolder", Success("MoveFolder")))
            .On("DeleteFolder", Response("DeleteFolder", Success("DeleteFolder")))
            .On("EmptyFolder", Response("EmptyFolder", Success("EmptyFolder")));
        using var p = fake.CreateProvider();

        var f = await p.CreateFolderAsync("inbox", "Projects");
        await p.CreateFolderAsync("msgfolderroot", "Team calendar", FolderKind.Calendar);
        await p.RenameFolderAsync("NEWF", "Projects 2026");
        await p.MoveFolderAsync("NEWF", "msgfolderroot");
        await p.EmptyFolderAsync("deleteditems", true);
        await p.DeleteFolderAsync("NEWF", false);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("NEWF", f.Id);
        Assert.Equal("HardDelete", fake.Last("EmptyFolder").Attribute("DeleteType")!.Value);
        Assert.Equal("MoveToDeletedItems", fake.Last("DeleteFolder").Attribute("DeleteType")!.Value);
    }

    [Fact]
    public async Task Search_falls_back_to_restriction_when_aqs_is_rejected()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Error("FindItem", "ErrorInvalidRequest", "QueryString not supported")))
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(MessageXml))));
        using var p = fake.CreateProvider();

        var page = await p.SearchMessagesAsync("inbox", "report", 0, 20);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("report", fake.Requests[0].Element(M + "QueryString")!.Value);
        Assert.NotNull(fake.Requests[1].Element(M + "Restriction"));
        Assert.Single(page.Items);
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
    public async Task Contacts_calendar_tasks_produce_schema_valid_requests()
    {
        var fake = new FakeEws()
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(
                """
                <t:Contact><t:ItemId Id="C1"/><t:DisplayName>Alice</t:DisplayName><t:GivenName>Alice</t:GivenName>
                  <t:EmailAddresses><t:Entry Key="EmailAddress1">alice@contoso.com</t:Entry></t:EmailAddresses></t:Contact>
                """))))
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(
                """
                <t:CalendarItem><t:ItemId Id="E1"/><t:Subject>Standup</t:Subject><t:Start>2026-10-01T09:00:00Z</t:Start><t:End>2026-10-01T09:15:00Z</t:End>
                  <t:IsAllDayEvent>false</t:IsAllDayEvent><t:LegacyFreeBusyStatus>Busy</t:LegacyFreeBusyStatus><t:Location>Room 1</t:Location>
                  <t:IsMeeting>true</t:IsMeeting><t:MyResponseType>Accept</t:MyResponseType>
                  <t:Organizer><t:Mailbox><t:Name>Bob</t:Name><t:EmailAddress>bob@contoso.com</t:EmailAddress></t:Mailbox></t:Organizer></t:CalendarItem>
                """))))
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(
                """
                <t:Task><t:ItemId Id="T1"/><t:Subject>Write spec</t:Subject><t:DueDate>2026-10-05T00:00:00Z</t:DueDate>
                  <t:PercentComplete>50</t:PercentComplete><t:Status>InProgress</t:Status></t:Task>
                """))))
            .On("CreateItem", Response("CreateItem", Success("CreateItem", "<m:Items><t:Contact><t:ItemId Id=\"NEW\"/></t:Contact></m:Items>")))
            .On("UpdateItem", Response("UpdateItem", Success("UpdateItem")))
            .On("DeleteItem", Response("DeleteItem", Success("DeleteItem")))
            .On("GetItem", Response("GetItem", Success("GetItem", "<m:Items><t:MeetingRequest><t:ItemId Id=\"MR\" ChangeKey=\"CK\"/></t:MeetingRequest></m:Items>")));
        using var p = fake.CreateProvider();

        var contacts = await p.GetContactsAsync();
        var events = await p.GetEventsAsync(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var tasks = await p.GetTasksAsync();

        await p.CreateContactAsync(new Contact
        {
            GivenName = "Carol", Surname = "Jones", CompanyName = "Fabrikam", JobTitle = "CTO", Department = "IT",
            EmailAddresses = { "carol@fabrikam.com" }, MobilePhone = "+1 555 0101", Notes = "met at conf",
        });
        await p.UpdateContactAsync(new Contact { Id = "C1", DisplayName = "Alice A", EmailAddresses = { "alice@contoso.com" } });
        await p.CreateEventAsync(new CalendarEvent
        {
            Subject = "Review", Location = "Room 2",
            Start = DateTimeOffset.Parse("2026-10-02T10:00:00Z"), End = DateTimeOffset.Parse("2026-10-02T11:00:00Z"),
            RequiredAttendees = { new EmailAddress("Bob", "bob@contoso.com") },
        });
        await p.CreateTaskAsync(new TaskItem { Subject = "Do it", DueDate = DateTimeOffset.Parse("2026-10-03T00:00:00Z") });
        await p.SetTaskCompleteAsync("T1", true);
        await p.RespondToMeetingAsync("MR", MeetingResponse.Tentative, "Might be late");
        await p.CancelOrDeleteEventAsync("E1", isOrganizerOfMeeting: true);

        Assert.Empty(fake.ValidationErrors);
        Assert.Equal("alice@contoso.com", contacts.Single().PrimaryEmail);
        var e = events.Single();
        Assert.Equal("Standup", e.Subject);
        Assert.Equal(ResponseStatus.Accept, e.MyResponse);
        Assert.Equal("bob@contoso.com", e.Organizer!.Address);
        var t = tasks.Single();
        Assert.Equal(TaskItemStatus.InProgress, t.Status);
        Assert.Equal(50, t.PercentComplete);
        Assert.Equal("SendToAllAndSaveCopy", fake.Requests.Single(r => r.Descendants(T + "CalendarItem").Any()).Attribute("SendMeetingInvitations")!.Value);
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
            .On("FindItem", Response("FindItem", Success("FindItem", ItemsRoot(MessageXml))));
        using var p = fake.CreateProvider();

        var page = await p.GetMessagesAsync("inbox", 0, 10);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Single(page.Items);
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
