using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailClient.App.Controls;
using MailClient.App.ViewModels;

namespace MailClient.App.Views;

public partial class MessagePreviewView : UserControl
{
    /// <summary>Share of the view height the To/Cc lines may take before they scroll.</summary>
    private const double RecipientsMaxShare = 0.15;

    /// <summary>Same for a long row of attachments.</summary>
    private const double AttachmentsMaxShare = 0.25;

    public MessagePreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            CardPopup.IsOpen = false;
            RecipientsScroll.ScrollToTop();
            AttachmentsScroll.ScrollToTop();
        };
    }

    /// <summary>The rendered body (printing, releasing the browser when the window closes).</summary>
    public MessageBodyView BodyView => Body;

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecipientsScroll.MaxHeight = Math.Max(36, e.NewSize.Height * RecipientsMaxShare);
        AttachmentsScroll.MaxHeight = Math.Max(48, e.NewSize.Height * AttachmentsMaxShare);
    }

    /// <summary>A sender or recipient name was clicked: show their contact card under it.</summary>
    private async void Person_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecipientItem person } link || DataContext is not MessagePreviewViewModel preview) return;
        var card = preview.CreateContactCard(person.Address);
        CardPopup.DataContext = card;
        CardPopup.PlacementTarget = link;
        CardPopup.IsOpen = true;
        // Keyboard users land on the first action; Esc closes the card.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CardWriteButton.Focus());
        await card.LoadAsync();
    }

    private void CardAction_Click(object sender, RoutedEventArgs e) => CardPopup.IsOpen = false;

    private void Card_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CardPopup.IsOpen = false;
        e.Handled = true;
    }
}
