namespace MailClient.Core.Services;

/// <summary>Supplies the stored password of an account.</summary>
public interface ICredentialProvider
{
    /// <summary>Returns the stored password for the account, or null.</summary>
    string? GetPassword(Guid accountId);
}

/// <summary>Persists secrets (implemented with DPAPI on Windows).</summary>
public interface ISecretStore
{
    void Save(string key, string secret);
    string? Load(string key);
    void Delete(string key);
}
