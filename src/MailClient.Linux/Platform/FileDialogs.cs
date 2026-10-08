using Avalonia.Platform.Storage;

namespace MailClient.App.Services;

/// <summary>File and folder choosers for the Linux build (GTK/portal dialogs through Avalonia), same calls as on Windows.</summary>
public static class FileDialogs
{
    private static IStorageProvider? Storage => Dialogs.ActiveWindow?.StorageProvider;

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    public static string? SaveFile(string fileName, string title, string? filter = null)
    {
        if (Storage is not { CanSave: true } storage) return null;
        var options = new FilePickerSaveOptions { Title = title, SuggestedFileName = fileName, ShowOverwritePrompt = true };
        if (ParseFilter(filter) is { } types) options.FileTypeChoices = types;
        return Dialogs.Wait(storage.SaveFilePickerAsync(options))?.TryGetLocalPath();
    }

    /// <summary>Asks for a folder; null when cancelled.</summary>
    public static string? PickFolder(string title)
    {
        if (Storage is not { CanPickFolder: true } storage) return null;
        var picked = Dialogs.Wait(storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title }));
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>Asks for files to open; empty when cancelled.</summary>
    public static IReadOnlyList<string> OpenFiles(string title, string? filter = null, bool multiple = true)
    {
        if (Storage is not { CanOpen: true } storage) return [];
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = multiple };
        if (ParseFilter(filter) is { } types) options.FileTypeFilter = types;
        return Dialogs.Wait(storage.OpenFilePickerAsync(options)).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Windows-style "Name (*.ext)|*.ext|…" filter → picker file types.</summary>
    internal static List<FilePickerFileType>? ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        return types.Count > 0 ? types : null;
    }
}
