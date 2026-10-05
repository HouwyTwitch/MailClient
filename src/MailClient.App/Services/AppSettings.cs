using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailClient.Core.Models;

namespace MailClient.App.Services;

public enum AppTheme { System, Light, Dark }

public sealed class AppSettings
{
    /// <summary>Format version, used to migrate older settings files.</summary>
    public int SettingsVersion { get; set; }

    public List<AccountSettings> Accounts { get; set; } = new();
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Remote images are blocked by default (tracking pixels, privacy).</summary>
    public bool LoadRemoteImages { get; set; }
    /// <summary>Sender addresses for which remote images are always loaded.</summary>
    public List<string> TrustedSenders { get; set; } = new();
    public int MarkAsReadDelaySeconds { get; set; } = 2;
    public bool ShowNotifications { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ConfirmDelete { get; set; }
    /// <summary>Default font of new messages (the font travels with the message).</summary>
    public string ComposeFontFamily { get; set; } = EditorFonts.DefaultFamily;
    public double ComposeFontSize { get; set; } = EditorFonts.DefaultSize;
    public double FolderPaneWidth { get; set; } = 250;
    public double MessageListWidth { get; set; } = 400;
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return Migrate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings());
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось прочитать настройки, используются значения по умолчанию", ex);
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true); } catch { }
        }
        return new AppSettings();
    }

    private const int CurrentVersion = 2;

    /// <summary>
    /// v2: Exchange sign-in uses NTLM by default, and "Windows single sign-on" became
    /// "NTLM with an empty password" (the logged-on Windows user).
    /// </summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentVersion) return s;
        foreach (var a in s.Accounts.Where(a => a.Protocol == MailClient.Core.Models.MailProtocol.Exchange))
        {
            if (a.AuthScheme == MailClient.Core.Models.HttpAuthScheme.Auto) a.AuthScheme = MailClient.Core.Models.HttpAuthScheme.Ntlm;
            if (a.AuthMethod == MailClient.Core.Models.AuthMethod.IntegratedWindows) a.AuthMethod = MailClient.Core.Models.AuthMethod.Password;
        }
        Log.Info($"Настройки обновлены до версии {CurrentVersion}: вход в Exchange по NTLM");
        s.SettingsVersion = CurrentVersion;
        Save(s);
        return s;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            // Write atomically so a crash can never leave a half-written settings file.
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
        }
    }
}
