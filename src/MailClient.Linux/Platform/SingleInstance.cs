using System.IO;
using System.Net.Sockets;
using System.Text;
using MailClient.App.Services;

namespace MailClient.Linux;

/// <summary>
/// One running copy per user: the first instance listens on a Unix socket in $XDG_RUNTIME_DIR; a second start
/// (for example a mailto: link clicked in the browser) passes its arguments there and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Arguments received from later starts (raised on a background thread).</summary>
    public event EventHandler<string[]>? ArgumentsReceived;

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
        _ = AcceptLoopAsync();
    }

    /// <summary>Unix socket paths are limited to 107 bytes (sun_path).</summary>
    private const int MaxSocketPath = 100;

    private static string SocketPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = AppPaths.Local;
            var path = Path.Combine(dir, "mailclient.sock");
            // A long runtime or home directory: a per-user name in /tmp instead.
            return System.Text.Encoding.UTF8.GetByteCount(path) <= MaxSocketPath
                ? path
                : Path.Combine(Path.GetTempPath(), $"mailclient-{Environment.UserName}.sock");
        }
    }

    /// <summary>Returns the instance guard, or null after handing the arguments to the running instance.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var path = SocketPath;
        if (TrySend(path, args)) return null;
        try
        {
            if (File.Exists(path)) File.Delete(path); // left over from a crash: nobody answered
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            return new SingleInstance(listener, path);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Without the socket a second copy can still start; it is not worth refusing to run.
            Log.Warn($"Проверка единственного экземпляра недоступна: {ex.Message}");
            return new SingleInstance(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified), "");
        }
    }

    private static bool TrySend(string path, string[] args)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.UTF8.GetBytes(string.Join('\n', args.Prepend("activate"))));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        if (_path.Length == 0) return;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                var buffer = new byte[64 * 1024];
                int total = 0, read;
                while (total < buffer.Length && (read = await client.ReceiveAsync(buffer.AsMemory(total), _stop.Token)) > 0) total += read;
                var parts = Encoding.UTF8.GetString(buffer, 0, total).Split('\n');
                if (parts.Length > 0 && parts[0] == "activate") ArgumentsReceived?.Invoke(this, parts[1..]);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Warn($"Сообщение от второго экземпляра не принято: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        _stop.Dispose();
        try
        {
            if (_path.Length > 0 && File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
