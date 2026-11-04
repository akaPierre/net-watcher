using Microsoft.Win32;

namespace NetWatcher.Services;

/// <summary>
/// "Start with Windows": a per-user entry under HKCU\...\Run that launches the app minimized
/// to the tray at login. Needs no admin rights and only affects the current Windows user.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NetWatcher";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }

    /// <summary>If the app was moved (e.g. a new build in another folder), point the entry at this copy.</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command)
            key.SetValue(ValueName, Command);
    }
}
