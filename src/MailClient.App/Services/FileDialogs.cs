using Microsoft.Win32;

namespace MailClient.App.Services;

/// <summary>Standard file dialogs (Windows). The Linux build has its own implementation with the same calls.</summary>
public static class FileDialogs
{
    /// <summary>Asks where to save a file; null when cancelled.</summary>
    public static string? SaveFile(string fileName, string title, string? filter = null)
    {
        var dlg = new SaveFileDialog { FileName = fileName, Title = title };
        if (filter != null) dlg.Filter = filter;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Asks for a folder; null when cancelled.</summary>
    public static string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
