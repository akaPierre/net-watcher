using System.IO;

namespace NetWatcher.Services;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\NetWatcher — database, vendor list and settings live here.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetWatcher");

    public static string DatabaseFile => Path.Combine(DataDir, "netwatcher.db");
}
