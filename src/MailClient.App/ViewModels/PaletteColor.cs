using System.Windows.Media;

namespace MailClient.App.ViewModels;

/// <summary>A colour in the editor's text-colour or highlight palette.</summary>
public sealed record PaletteColor(string Name, string Hex, bool IsHighlight)
{
    public Brush Brush { get; } = CreateBrush(Hex);

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static IReadOnlyList<PaletteColor> TextColors { get; } =
    [
        new("Чёрный", "#000000", false), new("Тёмно-серый", "#404040", false), new("Серый", "#808080", false),
        new("Тёмно-красный", "#C00000", false), new("Красный", "#FF0000", false), new("Оранжевый", "#ED7D31", false),
        new("Золотой", "#BF8F00", false), new("Зелёный", "#00B050", false), new("Тёмно-зелёный", "#375623", false),
        new("Голубой", "#00B0F0", false), new("Синий", "#0070C0", false), new("Тёмно-синий", "#002060", false),
        new("Фиолетовый", "#7030A0", false), new("Розовый", "#D6336C", false),
    ];

    public static IReadOnlyList<PaletteColor> HighlightColors { get; } =
    [
        new("Жёлтый", "#FFFF00", true), new("Ярко-зелёный", "#92D050", true), new("Бирюзовый", "#00FFFF", true),
        new("Розовый", "#FF99CC", true), new("Оранжевый", "#FFC000", true), new("Голубой", "#9DC3E6", true),
        new("Светло-серый", "#D9D9D9", true),
    ];
}
