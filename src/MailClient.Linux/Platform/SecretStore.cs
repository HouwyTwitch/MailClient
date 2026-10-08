using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MailClient.Core.Services;

namespace MailClient.App.Services;

/// <summary>
/// Passwords on Linux: the desktop keyring (Secret Service — GNOME Keyring, KWallet — through the secret-tool
/// utility from libsecret-tools) when the session has one; otherwise an AES-GCM encrypted file in the user's
/// settings folder that only the user can read (folder 0700, files 0600). Nothing is sent anywhere.
/// </summary>
public sealed class LinuxSecretStore : ISecretStore
{
    private const string Service = "mailclient";
    private static readonly Lazy<bool> KeyringAvailable = new(() => File.Exists("/usr/bin/secret-tool") && RunSecretTool(["search", "service", Service], null).ok);

    public void Save(string key, string secret)
    {
        if (KeyringAvailable.Value && RunSecretTool(["store", "--label=Корпоративная почта", "service", Service, "key", key], secret).ok)
        {
            DeleteFile(key);
            return;
        }
        SaveFile(key, secret);
    }

    public string? Load(string key)
    {
        if (KeyringAvailable.Value)
        {
            var (ok, output) = RunSecretTool(["lookup", "service", Service, "key", key], null);
            if (ok && output.Length > 0) return output;
        }
        return LoadFile(key);
    }

    public void Delete(string key)
    {
        if (KeyringAvailable.Value) RunSecretTool(["clear", "service", Service, "key", key], null);
        DeleteFile(key);
    }

    private static (bool ok, string output) RunSecretTool(string[] args, string? input)
    {
        try
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            if (input != null) p.StandardInput.Write(input);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                p.Kill();
                return (false, "");
            }
            return (p.ExitCode == 0, output.TrimEnd('\n'));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Хранилище ключей недоступно: {ex.Message}");
            return (false, "");
        }
    }

    // ------------------------------------------------------------------ file fallback

    private static string Folder
    {
        get
        {
            var dir = AppPaths.Secrets;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return dir;
        }
    }

    private static string PathFor(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".bin");

    private static void WritePrivate(string path, byte[] data)
    {
        File.WriteAllBytes(path, []);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(path, data);
    }

    private static byte[] MasterKey()
    {
        var path = Path.Combine(Folder, "master.key");
        if (File.Exists(path) && File.ReadAllBytes(path) is { Length: 32 } existing) return existing;
        var key = RandomNumberGenerator.GetBytes(32);
        WritePrivate(path, key);
        return key;
    }

    private static void SaveFile(string key, string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(MasterKey(), 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(key));
        WritePrivate(PathFor(key), [.. nonce, .. tag, .. cipher]);
    }

    private static string? LoadFile(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(MasterKey(), 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(key));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            Log.Warn($"Не удалось расшифровать сохранённый секрет: {ex.Message}");
            return null;
        }
    }

    private static void DeleteFile(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }
}

public sealed class CredentialProvider : ICredentialProvider
{
    private readonly ISecretStore _store;

    public CredentialProvider(ISecretStore store) => _store = store;

    public static string PasswordKey(Guid accountId) => $"password:{accountId:N}";

    public string? GetPassword(Guid accountId) => _store.Load(PasswordKey(accountId));

    public void SetPassword(Guid accountId, string password) => _store.Save(PasswordKey(accountId), password);

    public void DeletePassword(Guid accountId) => _store.Delete(PasswordKey(accountId));
}
