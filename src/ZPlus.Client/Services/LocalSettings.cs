using System;
using System.IO;
using System.Text.Json;

namespace ZPlus.Client.Services;

/// <summary>
/// Small per-user preferences that persist between runs (last-used server URL and email),
/// stored as JSON under the platform app-data folder. Works on Windows and Linux.
/// </summary>
public class LocalSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5199";
    public string Email { get; set; } = "";

    private static LocalSettings? _current;

    /// <summary>The process-wide instance, loaded from disk on first access.</summary>
    public static LocalSettings Current => _current ??= Load();

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ZPlus");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "client.json");
        }
    }

    private static LocalSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(FilePath))
                       ?? new LocalSettings();
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
        return new LocalSettings();
    }

    /// <summary>Best-effort persist; never throws so it can't break sign-in.</summary>
    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* ignore */ }
    }
}
