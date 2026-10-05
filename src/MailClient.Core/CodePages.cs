using System.Runtime.CompilerServices;
using System.Text;

namespace MailClient.Core;

internal static class CodePages
{
    /// <summary>
    /// windows-1251, KOI8-R, CP866… are not built into .NET: register them as soon as this assembly loads, before
    /// MimeKit/MailKit decode anything (otherwise Russian mail in these charsets is shown as mojibake).
    /// </summary>
    [ModuleInitializer]
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
