using System.IO;
using System.Security.Cryptography;
using System.Text;
using MailClient.Core.Models;
using MailClient.Core.Services;

namespace MailClient.App.Services;

/// <summary>
/// Stores secrets encrypted with Windows DPAPI (CurrentUser scope): only the same Windows user
/// on the same machine can decrypt them. Nothing is sent anywhere.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailClient.v1.secret-store");

    private static string PathFor(string key)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(AppPaths.Secrets, name + ".bin");
    }

    public void Save(string key, string secret)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), data);
    }

    public string? Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            Log.Warn($"Не удалось расшифровать сохранённый секрет: {ex.Message}");
            return null;
        }
    }

    public void Delete(string key)
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

    public Task<string> GetAccessTokenAsync(AccountSettings account, bool forceRefresh, CancellationToken ct) =>
        throw new MailServiceException("Вход через OAuth (Microsoft Entra ID) в этой сборке не поддерживается. " +
                                       "Используйте вход по паролю или единый вход Windows.");
}
