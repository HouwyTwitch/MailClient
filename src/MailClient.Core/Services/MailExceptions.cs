namespace MailClient.Core.Services;

public class MailServiceException : Exception
{
    public string? ErrorCode { get; }
    public MailServiceException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner) => ErrorCode = errorCode;
}

public sealed class MailAuthenticationException : MailServiceException
{
    public MailAuthenticationException(string message, Exception? inner = null)
        : base(message, "Unauthorized", inner) { }
}

public sealed class MailConnectionException : MailServiceException
{
    public MailConnectionException(string message, Exception? inner = null)
        : base(message, "ConnectionFailed", inner) { }
}

/// <summary>The sync state is no longer valid; a full resync is required.</summary>
public sealed class SyncStateInvalidException : MailServiceException
{
    public SyncStateInvalidException(string message) : base(message, "ErrorInvalidSyncStateData") { }
}
