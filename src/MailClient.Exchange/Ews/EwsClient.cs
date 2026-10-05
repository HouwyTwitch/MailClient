using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MailClient.Core.Diagnostics;
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
        "ErrorInboxRulesValidationError" => $"Сервер не принял правило: {serverText}",
        "ErrorOutlookRuleBlobExists" => "Правила были изменены в Outlook. Откройте список правил заново и сохраните ещё раз.",
        "ErrorRuleNotFound" => "Правило уже удалено на сервере. Откройте список правил заново.",
        "ErrorIrresolvableConflict" or "ErrorChangeKeyRequiredForWriteOperations" => "Объект был изменён на сервере. Обновите папку и повторите действие.",
        _ => $"{serverText} ({code})",
    };
}

/// <summary>
/// Low-level EWS SOAP transport: wraps operations in an envelope, posts them, handles SOAP faults, HTTP
/// errors and server throttling: a limited number of requests in flight per account (Exchange throttles
/// clients that open many parallel connections), one shared back-off for everybody when the server throttles (ErrorServerBusy),
/// and transparent retries of read-only operations when a connection drops (proxies and load balancers
/// in front of Exchange routinely close idle or long-running connections).
/// </summary>
public sealed class EwsClient : IDisposable
{
    private static readonly string[] VersionParts = ["MajorVersion", "MinorVersion", "MajorBuildNumber", "MinorBuildNumber"];

    /// <summary>Operations that only read data and can be safely sent again after a network failure.</summary>
    private static readonly HashSet<string> IdempotentOperations = new()
    {
        "GetFolder", "FindFolder", "SyncFolderHierarchy", "FindItem", "GetItem", "SyncFolderItems",
        "GetAttachment", "ResolveNames", "GetUserOofSettingsRequest", "ExpandDL", "GetUserAvailabilityRequest",
        "GetInboxRules",
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _version;
    private readonly int _maxRetries;
    private readonly SemaphoreSlim _inFlight;
    private readonly object _throttleSync = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;

    public EwsClient(HttpClient http, Uri endpoint, string requestServerVersion, int maxRetries = 3, int maxConcurrentRequests = 4)
    {
        _http = http;
        _endpoint = endpoint;
        _version = requestServerVersion;
        _maxRetries = maxRetries;
        _inFlight = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    /// <summary>Server version reported in the last response header (e.g. "15.2.1544.4").</summary>
    public string? LastServerVersion { get; private set; }

    /// <summary>Delay applied by the network retry (tests shorten it).</summary>
    internal Func<int, TimeSpan> NetworkRetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(attempt == 0 ? 1 : 4);

    /// <summary>Sends an operation and returns the operation response element (first child of soap:Body).</summary>
    /// <param name="operation">The operation element (m:GetItem, m:CreateItem…).</param>
    /// <param name="ct">Cancels the request, including retries and throttling pauses.</param>
    /// <param name="timeZoneId">Windows time zone id for a TimeZoneContext header (meeting times).</param>
    /// <param name="requestVersion">Overrides the RequestServerVersion for this call.</param>
    public async Task<XElement> SendAsync(XElement operation, CancellationToken ct, string? timeZoneId = null, string? requestVersion = null)
    {
        var header = new XElement(Soap + "Header",
            new XElement(T + "RequestServerVersion", new XAttribute("Version", requestVersion ?? _version)));
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
        var name = operation.Name.LocalName;
        bool idempotent = IdempotentOperations.Contains(name);

        int busyRetries = 0, networkRetries = 0;
        while (true)
        {
            await WaitWhileThrottledAsync(ct).ConfigureAwait(false);
            await _inFlight.WaitAsync(ct).ConfigureAwait(false);
            (XElement? result, TimeSpan? busy, Exception? network) outcome;
            try
            {
                outcome = await SendOnceAsync(name, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.Release();
            }

            if (outcome.result != null) return outcome.result;

            if (outcome.busy is { } backoff)
            {
                if (busyRetries++ >= _maxRetries)
                    throw new EwsResponseException("ErrorServerBusy", "Server busy");
                // One shared pause for all requests of this account, not a stampede of retries.
                PauseAll(backoff);
                MailLog.Warn?.Invoke($"EWS {name}: сервер ограничивает частоту запросов (ErrorServerBusy), пауза {backoff.TotalSeconds:0.#} с");
                continue;
            }

            var ex = outcome.network!;
            if (idempotent && networkRetries < 2 && !ct.IsCancellationRequested)
            {
                MailLog.Warn?.Invoke($"EWS {name}: обрыв соединения, повтор {networkRetries + 1}/2: {MailLog.Describe(ex)}");
                await Task.Delay(NetworkRetryDelay(networkRetries++), ct).ConfigureAwait(false);
                continue;
            }
            MailLog.Warn?.Invoke($"EWS {name}: ошибка сети: {MailLog.Describe(ex)}");
            if (ex is HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException })
                throw new MailConnectionException(
                    $"Сертификат сервера {_endpoint.Host} не является доверенным. Если в организации используется " +
                    "собственный удостоверяющий центр или «Russian Trusted Root CA», импортируйте корневой сертификат " +
                    "в настройках учётной записи (раздел «Безопасность»).", ex);
            if (ex is TaskCanceledException or TimeoutException)
                throw new MailConnectionException($"Сервер Exchange {_endpoint.Host} не ответил вовремя ({name}). Проверьте сетевое подключение или VPN.", ex);
            throw new MailConnectionException($"Соединение с сервером Exchange {_endpoint.Host} прервано ({name}): {MailLog.Describe(ex)}", ex);
        }
    }

    private async Task WaitWhileThrottledAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_throttleSync) wait = _pausedUntilUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    private void PauseAll(TimeSpan backoff)
    {
        lock (_throttleSync)
        {
            var until = DateTime.UtcNow + backoff;
            if (until > _pausedUntilUtc) _pausedUntilUtc = until;
        }
    }

    /// <summary>
    /// One HTTP round trip. Returns the operation response, or a server-busy back-off, or a network failure
    /// (including a connection dropped while the response body was being read).
    /// </summary>
    private async Task<(XElement? result, TimeSpan? busy, Exception? network)> SendOnceAsync(string name, byte[] payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        request.Headers.Accept.ParseAdd("text/xml");

        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (null, null, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                MailLog.Warn?.Invoke($"EWS {name}: HTTP 401, сервер предлагает: {string.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.Scheme))}");
                throw new MailAuthenticationException(Http.ExchangeHttp.DescribeAuthFailure(response));
            }
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                throw new MailConnectionException(ProxyAuthenticationMessage(_endpoint.Host));
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new MailAuthenticationException("Доступ запрещён (HTTP 403). Возможно, для этого почтового ящика отключён доступ по EWS — обратитесь к администратору.");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new MailConnectionException($"Сервер перенаправил запрос на {response.Headers.Location}. Воспользуйтесь автообнаружением или исправьте адрес EWS.");

            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return (null, null, ex); // connection cut while reading the body
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(new MemoryStream(bytes));
            }
            catch (XmlException)
            {
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                    return (null, null, new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
                MailLog.Warn?.Invoke($"EWS {name}: ответ не XML, HTTP {(int)response.StatusCode}, {bytes.Length} байт");
                throw new MailServiceException($"Непредвиденный ответ сервера (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            var serverVersion = doc.Root?.Element(Soap + "Header")?.Element(T + "ServerVersionInfo");
            if (serverVersion != null)
            {
                LastServerVersion = string.Join(".", VersionParts.Select(a => serverVersion.Attribute(a)?.Value ?? "0"));
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
                if (code.EndsWith("ErrorServerBusy", StringComparison.Ordinal))
                {
                    var hint = fault.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName == "Value" && (string?)e.Attribute("Name") == "BackOffMilliseconds")?.Value;
                    return (null, Backoff(hint), null);
                }
                MailLog.Warn?.Invoke($"EWS {name}: SOAP fault {code}: {text}");
                throw new EwsResponseException(code, text);
            }

            var result = body.Elements().FirstOrDefault()
                ?? throw new MailServiceException("Некорректный ответ сервера (пустое тело SOAP).");

            // Throttling can also surface as a response message error; honour the
            // BackOffMilliseconds the server puts into MessageXml. When only part of a batch was throttled the
            // rest has already been executed, so only idempotent operations may be repeated as a whole.
            var codes = result.Descendants(M + "ResponseCode").ToList();
            var busyCodes = codes.Where(c => c.Value == "ErrorServerBusy").ToList();
            if (busyCodes.Count > 0 && (busyCodes.Count == codes.Count || IdempotentOperations.Contains(name)))
            {
                var hint = busyCodes.Select(c => c.Parent?.Element(M + "MessageXml")).Where(x => x != null)
                    .SelectMany(x => x!.Descendants())
                    .FirstOrDefault(e => e.Name.LocalName == "Value" && (string?)e.Attribute("Name") == "BackOffMilliseconds")?.Value;
                return (null, Backoff(hint), null);
            }
            return (result, null, null);
        }
    }

    /// <summary>Explanation for HTTP 407 from a corporate proxy that did not accept the Windows sign-in.</summary>
    public static string ProxyAuthenticationMessage(string host) =>
        $"Прокси-сервер организации не пропустил запрос к {host} (HTTP 407: требуется авторизация на прокси). " +
        "Программа входит на прокси от имени пользователя Windows; если прокси этого не принимает, попросите администратора " +
        "добавить адрес почтового сервера в исключения прокси (обычно внутренний сервер должен открываться напрямую).";

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

    private static TimeSpan Backoff(string? serverHintMs) =>
        int.TryParse(serverHintMs, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(Math.Min(ms, 60_000))
            : TimeSpan.FromSeconds(5);

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(w);
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
