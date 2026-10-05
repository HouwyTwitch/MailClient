using System.Windows.Controls;
using MailClient.App.Controls;

namespace MailClient.App.Views;

public partial class MessagePreviewView : UserControl
{
    public MessagePreviewView() => InitializeComponent();

    /// <summary>The rendered body (printing, releasing the browser when the window closes).</summary>
    public MessageBodyView BodyView => Body;
}
