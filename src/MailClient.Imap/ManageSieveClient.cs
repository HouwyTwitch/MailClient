using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MailClient.Core.Services;

namespace MailClient.Imap;

/// <summary>
/// Minimal ManageSieve client (RFC 5804): the commands needed to read and replace the active Sieve script.
/// TLS is negotiated with STARTTLS whenever the server offers it; login is a single SASL PLAIN attempt.
/// </summary>
internal sealed class ManageSieveClient : IAsyncDisposable
{
    public const int DefaultPort = 4190;

    private readonly TcpClient _tcp;
    private Stream _stream;
    private readonly byte[] _buffer = new byte[8192];
    private int _bufferPos, _bufferLen;

    private ManageSieveClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>Capabilities from the last greeting (name → value), e.g. SIEVE → "fileinto copy body …".</summary>
    public Dictionary<string, string> Capabilities { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A reply: data lines (each a list of strings), then OK / NO / BYE with an optional code and text.</summary>
    public sealed record Reply(List<List<string>> Data, string Status, string Code, string Text)
    {
        public bool IsOk => Status == "OK";
    }

    /// <summary>Connects, upgrades to TLS when possible (required when <paramref name="requireTls"/>) and signs in.</summary>
    public static async Task<ManageSieveClient> ConnectAsync(string host, int port, bool requireTls,
        RemoteCertificateValidationCallback? certificateCallback, NetworkCredential credential, CancellationToken ct)
    {
        var tcp = new TcpClient();
        ManageSieveClient? client = null;
        try
        {
            try
            {
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new MailServiceException(
                    $"Сервер {host} не поддерживает управление правилами почты (ManageSieve, порт {port}). " +
                    "Для Яндекс 360 и Mail.ru пересылка настраивается в веб-интерфейсе почты.", "ManageSieveUnavailable", ex);
            }
            client = new ManageSieveClient(tcp);
            client.ReadCapabilities(await client.ReadReplyAsync(ct).ConfigureAwait(false));

            if (client.Capabilities.ContainsKey("STARTTLS"))
            {
                Expect(await client.CommandAsync("STARTTLS", ct).ConfigureAwait(false), "STARTTLS");
                var ssl = new SslStream(client._stream, leaveInnerStreamOpen: false, certificateCallback);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct).ConfigureAwait(false);
                }
                catch (AuthenticationException ex)
                {
                    throw new MailConnectionException(
                        $"Не удалось установить защищённое соединение с сервером правил {host}:{port}. Если сертификат выдан внутренним " +
                        "удостоверяющим центром, импортируйте корневой сертификат в настройках учётной записи.", ex);
                }
                client._stream = ssl;
                client._bufferPos = client._bufferLen = 0;
                // After STARTTLS the server repeats its capabilities (they may differ, e.g. SASL mechanisms).
                client.ReadCapabilities(await client.ReadReplyAsync(ct).ConfigureAwait(false));
            }
            else if (requireTls)
            {
                throw new MailConnectionException($"Сервер правил {host}:{port} не поддерживает шифрование (STARTTLS); пароль не будет передан открытым текстом.");
            }

            var mechanisms = client.Capabilities.GetValueOrDefault("SASL", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!mechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
                throw new MailServiceException($"Сервер правил {host} не поддерживает вход по паролю (SASL PLAIN).", "ManageSieveNoPlain");
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{credential.UserName}\0{credential.Password}"));
            var auth = await client.CommandAsync($"AUTHENTICATE \"PLAIN\" {Quote(token)}", ct).ConfigureAwait(false);
            if (!auth.IsOk)
                // Not a MailAuthenticationException: the mailbox password works for IMAP, only the rules service refused it.
                throw new MailServiceException($"Сервер правил {host} не принял имя пользователя или пароль ({auth.Text}).", "ManageSieveAuth");
            return client;
        }
        catch
        {
            if (client != null) await client.DisposeAsync().ConfigureAwait(false);
            else tcp.Dispose();
            throw;
        }
    }

    /// <summary>Script names and which one is active.</summary>
    public async Task<(List<string> Names, string? Active)> ListScriptsAsync(CancellationToken ct)
    {
        var reply = Expect(await CommandAsync("LISTSCRIPTS", ct).ConfigureAwait(false), "LISTSCRIPTS");
        var names = reply.Data.Where(d => d.Count > 0).Select(d => d[0]).ToList();
        var active = reply.Data.FirstOrDefault(d => d.Count > 1 && d[1].Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))?[0];
        return (names, active);
    }

    public async Task<string> GetScriptAsync(string name, CancellationToken ct)
    {
        var reply = Expect(await CommandAsync($"GETSCRIPT {Quote(name)}", ct).ConfigureAwait(false), "GETSCRIPT");
        return reply.Data.FirstOrDefault(d => d.Count > 0)?[0] ?? "";
    }

    /// <summary>Uploads a script; the server compiles it first and rejects it with its error text.</summary>
    public async Task PutScriptAsync(string name, string script, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(script);
        var command = Encoding.UTF8.GetBytes($"PUTSCRIPT {Quote(name)} {{{bytes.Length.ToString(CultureInfo.InvariantCulture)}+}}\r\n");
        await _stream.WriteAsync(command, ct).ConfigureAwait(false);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (!reply.IsOk) throw new MailServiceException($"Сервер не принял правила: {reply.Text}", "ManageSieve" + reply.Status);
    }

    public async Task SetActiveAsync(string name, CancellationToken ct) =>
        Expect(await CommandAsync($"SETACTIVE {Quote(name)}", ct).ConfigureAwait(false), "SETACTIVE");

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await CommandAsync("LOGOUT", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or MailServiceException or ObjectDisposedException)
        {
            // The connection is being closed anyway.
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }

    // ------------------------------------------------------------------ protocol

    private void ReadCapabilities(Reply reply)
    {
        Expect(reply, "greeting");
        Capabilities = reply.Data.Where(d => d.Count > 0)
            .GroupBy(d => d[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Count > 1 ? g.First()[1] : "", StringComparer.OrdinalIgnoreCase);
    }

    private static Reply Expect(Reply reply, string command) =>
        reply.IsOk ? reply : throw new MailServiceException($"Сервер правил ответил на {command}: {reply.Status} {reply.Text}".Trim(), "ManageSieve" + reply.Status);

    private async Task<Reply> CommandAsync(string command, CancellationToken ct)
    {
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads lines until OK, NO or BYE; a line can continue after a literal ({n} then n bytes).</summary>
    private async Task<Reply> ReadReplyAsync(CancellationToken ct)
    {
        var data = new List<List<string>>();
        while (true)
        {
            var (tokens, atomFirst) = await ReadTokensAsync(ct).ConfigureAwait(false);
            if (atomFirst && tokens.Count > 0 && tokens[0].ToUpperInvariant() is "OK" or "NO" or "BYE" && tokens[0] is var status)
            {
                var code = tokens.Count > 1 && tokens[1].StartsWith('(') ? tokens[1] : "";
                var text = tokens.Skip(code.Length > 0 ? 2 : 1).FirstOrDefault() ?? "";
                return new Reply(data, status.ToUpperInvariant(), code, text);
            }
            data.Add(tokens);
        }
    }

    /// <summary>One logical response line as strings (quoted strings and literals unescaped).</summary>
    private async Task<(List<string> Tokens, bool AtomFirst)> ReadTokensAsync(CancellationToken ct)
    {
        var tokens = new List<string>();
        bool atomFirst = false;
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        int i = 0;
        while (true)
        {
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) return (tokens, atomFirst);
            char c = line[i];
            if (c == '"')
            {
                var sb = new StringBuilder();
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\' && i + 1 < line.Length) i++;
                    sb.Append(line[i]);
                }
                i++;
                tokens.Add(sb.ToString());
            }
            else if (c == '{' && line.EndsWith('}') && int.TryParse(line.AsSpan(i + 1, line.Length - i - 2).TrimEnd('+'),
                         NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                tokens.Add(Encoding.UTF8.GetString(await ReadBytesAsync(size, ct).ConfigureAwait(false)));
                line = await ReadLineAsync(ct).ConfigureAwait(false);
                i = 0;
            }
            else if (c == '(')
            {
                int depth = 0, start = i;
                bool quoted = false;
                for (; i < line.Length; i++)
                {
                    if (quoted && line[i] == '\\') { i++; continue; }
                    if (line[i] == '"') quoted = !quoted;
                    else if (!quoted && line[i] == '(') depth++;
                    else if (!quoted && line[i] == ')' && --depth == 0) { i++; break; }
                }
                tokens.Add(line[start..Math.Min(i, line.Length)]);
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ' ') i++;
                if (tokens.Count == 0) atomFirst = true;
                tokens.Add(line[start..i]);
            }
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (_bufferPos >= _bufferLen) await FillAsync(ct).ConfigureAwait(false);
            var b = _buffer[_bufferPos++];
            if (b == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(b);
            if (bytes.Count > 1_000_000) throw new MailServiceException("Сервер правил прислал слишком длинную строку.", "ManageSieveProtocol");
        }
    }

    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        if (count > 16 * 1024 * 1024) throw new MailServiceException("Сервер правил прислал слишком большой скрипт.", "ManageSieveProtocol");
        var result = new byte[count];
        int done = 0;
        while (done < count)
        {
            if (_bufferPos >= _bufferLen) await FillAsync(ct).ConfigureAwait(false);
            int n = Math.Min(count - done, _bufferLen - _bufferPos);
            Array.Copy(_buffer, _bufferPos, result, done, n);
            _bufferPos += n;
            done += n;
        }
        return result;
    }

    private async Task FillAsync(CancellationToken ct)
    {
        _bufferLen = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
        _bufferPos = 0;
        if (_bufferLen == 0) throw new IOException("Сервер правил закрыл соединение.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
