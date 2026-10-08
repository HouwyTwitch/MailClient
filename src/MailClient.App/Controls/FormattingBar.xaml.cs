using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.App.Views;
using Microsoft.Win32;

namespace MailClient.App.Controls;

/// <summary>
/// Formatting toolbar for an <see cref="HtmlEditor"/>: font, size, emphasis, colours, alignment, lists, links and
/// pictures. Shared by the message window and the signature editor.
/// </summary>
public partial class FormattingBar : UserControl
{
    private const long MaxInlineImageBytes = 5 * 1024 * 1024;

    private readonly ObservableCollection<string> _fonts = new(EditorFonts.Families);
    private readonly ObservableCollection<double> _sizes = new(EditorFonts.Sizes);
    private bool _syncingFormatBar;
    private HtmlEditor? _editor;

    public FormattingBar()
    {
        InitializeComponent();
        FontCombo.ItemsSource = _fonts;
        SizeCombo.ItemsSource = _sizes;
        ColorPalette.ItemsSource = PaletteColor.TextColors;
        HighlightPalette.ItemsSource = PaletteColor.HighlightColors;
    }

    /// <summary>The editor this bar formats; its formatting at the caret is shown on the bar.</summary>
    public void Attach(HtmlEditor editor)
    {
        _editor = editor;
        editor.FormatStateChanged += (_, state) => ShowFormatState(state);
    }

    private HtmlEditor Editor => _editor ?? throw new InvalidOperationException("Панель форматирования не связана с редактором");

    /// <summary>Reflects the formatting at the caret in the toolbar (without applying anything).</summary>
    private void ShowFormatState(EditorFormatState state)
    {
        _syncingFormatBar = true;
        try
        {
            var font = _fonts.FirstOrDefault(f => f.Equals(state.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (font == null && state.FontFamily.Length > 0 && state.FontFamily.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-'))
            {
                font = state.FontFamily; // e.g. text pasted from Word in a font that is not in the list
                _fonts.Add(font);
            }
            FontCombo.SelectedItem = font;

            if (state.FontSizePt > 0 && !_sizes.Contains(state.FontSizePt))
            {
                int i = 0;
                while (i < _sizes.Count && _sizes[i] < state.FontSizePt) i++;
                _sizes.Insert(i, state.FontSizePt);
            }
            SizeCombo.SelectedItem = state.FontSizePt > 0 ? state.FontSizePt : null;

            BoldButton.IsChecked = state.Bold;
            ItalicButton.IsChecked = state.Italic;
            UnderlineButton.IsChecked = state.Underline;
            StrikeButton.IsChecked = state.Strikethrough;
            BulletsButton.IsChecked = state.Bullets;
            NumbersButton.IsChecked = state.Numbering;
            AlignLeftButton.IsChecked = state.Alignment == "left";
            AlignCenterButton.IsChecked = state.Alignment == "center";
            AlignRightButton.IsChecked = state.Alignment == "right";
            JustifyButton.IsChecked = state.Alignment == "justify";
            UndoButton.IsEnabled = state.CanUndo;
            RedoButton.IsEnabled = state.CanRedo;
        }
        finally
        {
            _syncingFormatBar = false;
        }
    }

    private async void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFormatBar || FontCombo.SelectedItem is not string family) return;
        await Editor.SetFontFamilyAsync(family);
        await Editor.FocusAsync();
    }

    private async void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFormatBar || SizeCombo.SelectedItem is not double size) return;
        await Editor.SetFontSizeAsync(size);
        await Editor.FocusAsync();
    }

    /// <summary>On/off formatting (bold, alignment, lists): the editor reports the resulting state back.</summary>
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command }) await Editor.ExecAsync(command);
    }

    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command }) await Editor.ExecAsync(command);
    }

    private async void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaletteColor color }) return;
        if (color.IsHighlight)
        {
            HighlightButton.IsChecked = false;
            HighlightSwatch.Fill = color.Brush;
            await Editor.SetHighlightAsync(color.Hex);
        }
        else
        {
            ColorButton.IsChecked = false;
            ColorSwatch.Fill = color.Brush;
            await Editor.SetTextColorAsync(color.Hex);
        }
        await Editor.FocusAsync();
    }

    private async void ColorAuto_Click(object sender, RoutedEventArgs e)
    {
        ColorButton.IsChecked = false;
        await Editor.SetTextColorAsync(null);
        await Editor.FocusAsync();
    }

    private async void HighlightNone_Click(object sender, RoutedEventArgs e)
    {
        HighlightButton.IsChecked = false;
        await Editor.SetHighlightAsync(null);
        await Editor.FocusAsync();
    }

    /// <summary>A picture inside the text (sent as an inline attachment), as opposed to an attached file.</summary>
    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Вставить изображение",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Все файлы|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > MaxInlineImageBytes)
            {
                Dialogs.Error($"Изображение слишком большое ({RuText.Size(info.Length)}). В текст можно вставить изображение " +
                              $"до {RuText.Size(MaxInlineImageBytes)}; большие файлы добавьте как вложение.");
                return;
            }
            var type = MimeTypes.FromFileName(dlg.FileName);
            if (!type.StartsWith("image/", StringComparison.Ordinal))
            {
                Dialogs.Error("Выберите файл изображения (PNG, JPEG, GIF или BMP).");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            await Editor.InsertImageAsync($"data:{type};base64,{Convert.ToBase64String(bytes)}");
            await Editor.FocusAsync();
        }
        catch (IOException ex)
        {
            Dialogs.Error(ex, "Не удалось прочитать изображение");
        }
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = WindowFactory.Prompt("Вставка ссылки", "Адрес (URL):", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !WindowsIntegration.IsSafeExternalLink(uri))
        {
            Dialogs.Error("Допускаются только ссылки http://, https:// и mailto:.");
            return;
        }
        await Editor.ExecAsync("createLink", uri.AbsoluteUri);
    }
}
