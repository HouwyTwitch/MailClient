namespace MailClient.Core.Services;

/// <summary>Supplies secrets for an account (password, OAuth token).</summary>
public interface ICredentialProvider
{
    /// <summary>Returns the stored password for the account, or null.</summary>
    string? GetPassword(Guid accountId);

    /// <summary>Returns an OAuth2 bearer token for the account (may prompt the user interactively).</summary>
    Task<string> GetAccessTokenAsync(Models.AccountSettings account, bool forceRefresh, CancellationToken ct);
}

/// <summary>Persists secrets (implemented with DPAPI on Windows).</summary>
public interface ISecretStore
{
    void Save(string key, string secret);
    string? Load(string key);
    void Delete(string key);
}
