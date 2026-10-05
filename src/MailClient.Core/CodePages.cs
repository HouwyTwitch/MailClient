using System.Text;

namespace MailClient.Core;

/// <summary>
/// windows-1251, KOI8-R, CP866… are not built into .NET. Without them MimeKit/MailKit decode Russian mail in those
/// charsets as Latin-1 (mojibake), so they are registered before any mail is parsed.
/// </summary>
public static class CodePages
{
    private static int _registered;

    /// <summary>Registers the code page provider once; cheap to call repeatedly.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
