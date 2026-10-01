using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MailClient.Core.Services;
using static MailClient.Exchange.Ews.Ews;

namespace MailClient.Exchange.Ews;

/// <summary>A response message (e.g. FindItemResponseMessage) that reported an error.</summary>
public sealed class EwsResponseException : MailServiceException
{
    public EwsResponseException(string code, string message) : base(Translate(code, message), code) { }

    /// <summary>Русские сообщения для наиболее частых кодов ошибок EWS.</summary>
    internal static string Translate(string code, string serverText) => code switch
    {
        "ErrorItemNotFound" => "Объект не найден — возможно, он уже удалён или перемещён.",
        "ErrorFolderNotFound" => "Папка не найдена — возможно, она удалена или перемещена.",
        "ErrorAccessDenied" => "Недостаточно прав для выполнения операции.",
        "ErrorFolderExists" => "Папка с таким именем уже существует.",
        "ErrorQuotaExceeded" => "Почтовый ящик переполнен. Удалите ненужные письма и очистите папку «Удалённые».",
        "ErrorMessageSizeExceeded" => "Письмо превышает максимальный размер, разрешённый на сервере. Уменьшите объём вложений.",
        "ErrorSendAsDenied" => "Нет прав на отправку от имени этого почтового ящика.",
        "ErrorInvalidRecipients" => "Один или несколько адресов получателей недействительны.",
        "ErrorNonExistentMailbox" => "Почтовый ящик не существует.",
        "ErrorMailboxStoreUnavailable" or "ErrorMailboxMoveInProgress" => "Почтовый ящик временно недоступен. Повторите попытку позже.",
        "ErrorServerBusy" or "ErrorExceededConnectionCount" or "ErrorTooManyObjectsOpened" => "Сервер перегружен. Повторите попытку через несколько минут.",
        "ErrorCannotDeleteObject" => "Не удалось удалить объект.",
        "ErrorDeleteDistinguishedFolder" or "ErrorCannotDeleteFolder" => "Системную папку удалить нельзя.",
        "ErrorMoveDistinguishedFolder" => "Системную папку переместить нельзя.",
        "ErrorInvalidServerVersion" => "Сервер не поддерживает выбранную версию протокола. Выберите более раннюю версию Exchange в настройках учётной записи.",
        "ErrorIrresolvableConflict" or "ErrorChangeKeyRequiredForWriteOperations" => "Объект был изменён на сервере. Обновите папку и повторите действие.",
        _ => $"{serverText} ({code})",
    };
}

/// <summary>
/// Low-level EWS SOAP transport: wraps operations in an envelope, posts them, handles
/// SOAP faults, HTTP errors and server throttling (ErrorServerBusy back-off).
/// </summary>
public sealed class EwsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _version;
    private readonly int _maxRetries;

    public EwsClient(HttpClient http, Uri endpoint, string requestServerVersion, int maxRetries = 3)
    {
        _http = http;
        _endpoint = endpoint;
        _version = requestServerVersion;
        _maxRetries = maxRetries;
    }

    /// <summary>Server version reported in the last response header (e.g. "15.2.1544.4").</summary>
    public string? LastServerVersion { get; private set; }

    /// <summary>Sends an operation and returns the operation response element (first child of soap:Body).</summary>
    /// <param name="timeZoneId">Windows time zone id for a TimeZoneContext header (calendar operations).</param>
    public async Task<XElement> SendAsync(XElement operation, CancellationToken ct, string? timeZoneId = null)
    {
        var header = new XElement(Soap + "Header",
            new XElement(T + "RequestServerVersion", new XAttribute("Version", _version)));
        if (!string.IsNullOrEmpty(timeZoneId))
        {
            header.Add(new XElement(T + "TimeZoneContext",
                new XElement(T + "TimeZoneDefinition", new XAttribute("Id", timeZoneId))));
        }

        var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap),
                new XAttribute(XNamespace.Xmlns + "t", T),
                new XAttribute(XNamespace.Xmlns + "m", M),
                header,
                new XElement(Soap + "Body", operation)));
        var payload = Serialize(envelope);

        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
            request.Headers.Accept.ParseAdd("text/xml");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (ex.InnerException is System.Security.Authentication.AuthenticationException)
                    throw new MailConnectionException(
                        $"Сертификат сервера {_endpoint.Host} не является доверенным. Если в организации используется " +
                        "собственный удостоверяющий центр или «Russian Trusted Root CA», импортируйте корневой сертификат " +
                        "в настройках учётной записи (раздел «Безопасность»).", ex);
                throw new MailConnectionException($"Не удаётся подключиться к серверу Exchange {_endpoint.Host}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new MailConnectionException($"Сервер Exchange {_endpoint.Host} не ответил вовремя. Проверьте сетевое подключение или VPN.", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new MailAuthenticationException("Сервер отклонил учётные данные (HTTP 401). Проверьте имя пользователя, пароль и способ входа.");
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new MailAuthenticationException("Доступ запрещён (HTTP 403). Возможно, для этого почтового ящика отключён доступ по EWS — обратитесь к администратору.");
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new MailConnectionException($"Сервер перенаправил запрос на {response.Headers.Location}. Воспользуйтесь автообнаружением или исправьте адрес EWS.");

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                XDocument doc;
                try
                {
                    doc = XDocument.Load(new MemoryStream(bytes));
                }
                catch (XmlException)
                {
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < _maxRetries)
                    {
                        await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new MailServiceException($"Непредвиденный ответ сервера (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).", ((int)response.StatusCode).ToString());
                }

                var serverVersion = doc.Root?.Element(Soap + "Header")?.Element(T + "ServerVersionInfo");
                if (serverVersion != null)
                {
                    LastServerVersion = string.Join(".",
                        new[] { "MajorVersion", "MinorVersion", "MajorBuildNumber", "MinorBuildNumber" }
                            .Select(a => serverVersion.Attribute(a)?.Value ?? "0"));
                }

                var body = doc.Root?.Element(Soap + "Body")
                    ?? throw new MailServiceException("Некорректный ответ сервера (нет тела SOAP).");
                var fault = body.Element(Soap + "Fault");
                if (fault != null)
                {
                    var code = fault.Descendants(T + "ResponseCode").FirstOrDefault()?.Value
                               ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResponseCode")?.Value
                               ?? fault.Element("faultcode")?.Value ?? "SoapFault";
                    var text = fault.Element("faultstring")?.Value ?? "SOAP fault";
                    if (code == "ErrorServerBusy" && attempt < _maxRetries)
                    {
                        var backoff = fault.Descendants().FirstOrDefault(e =>
                            e.Name.LocalName == "Value" && (string?)e.Attribute("Name") == "BackOffMilliseconds")?.Value;
                        await Task.Delay(Backoff(attempt, backoff), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new EwsResponseException(code, text);
                }

                var result = body.Elements().FirstOrDefault()
                    ?? throw new MailServiceException("Некорректный ответ сервера (пустое тело SOAP).");

                // Throttling can also surface as a response message error.
                if (attempt < _maxRetries && result.Descendants(M + "ResponseCode").Any(c => c.Value == "ErrorServerBusy"))
                {
                    await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                    continue;
                }
                return result;
            }
        }
    }

    /// <summary>Returns all response messages (elements carrying a ResponseClass attribute).</summary>
    public static IEnumerable<XElement> ResponseMessages(XElement response) =>
        response.Element(M + "ResponseMessages")?.Elements() ?? response.Elements().Where(e => e.Attribute("ResponseClass") != null);

    /// <summary>Throws for the first response message whose ResponseClass is Error, unless its code is in <paramref name="ignore"/>.</summary>
    public static void ThrowOnError(XElement response, params string[] ignore)
    {
        foreach (var msg in response.DescendantsAndSelf().Where(e => e.Attribute("ResponseClass") != null))
            ThrowIfError(msg, ignore);
    }

    public static void ThrowIfError(XElement responseMessage, params string[] ignore)
    {
        if ((string?)responseMessage.Attribute("ResponseClass") != "Error") return;
        var code = responseMessage.Element(M + "ResponseCode")?.Value ?? "Unknown";
        if (ignore.Contains(code)) return;
        var text = responseMessage.Element(M + "MessageText")?.Value ?? code;
        if (code == "ErrorInvalidSyncStateData") throw new SyncStateInvalidException(text);
        throw new EwsResponseException(code, text);
    }

    private static TimeSpan Backoff(int attempt, string? serverHintMs)
    {
        if (int.TryParse(serverHintMs, out var ms) && ms > 0) return TimeSpan.FromMilliseconds(Math.Min(ms, 60_000));
        return TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(w);
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
