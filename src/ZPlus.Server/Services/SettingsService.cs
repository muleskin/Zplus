using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Services;

/// <summary>Reads and writes server-wide settings stored in the database.</summary>
public class SettingsService(AppDbContext db)
{
    public static readonly ServerSettingsDto Defaults = new(
        AllowSelfRegistration: true,
        RequireMeetingPasswords: false,
        MaxParticipantsPerMeeting: 25,
        ListenUrl: "http://0.0.0.0:5199");

    public async Task<ServerSettingsDto> GetAsync()
    {
        var stored = await db.ServerSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        return new ServerSettingsDto(
            GetBool(stored, nameof(ServerSettingsDto.AllowSelfRegistration), Defaults.AllowSelfRegistration),
            GetBool(stored, nameof(ServerSettingsDto.RequireMeetingPasswords), Defaults.RequireMeetingPasswords),
            GetInt(stored, nameof(ServerSettingsDto.MaxParticipantsPerMeeting), Defaults.MaxParticipantsPerMeeting),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.ListenUrl), Defaults.ListenUrl));
    }

    public async Task SaveAsync(ServerSettingsDto settings)
    {
        await Upsert(nameof(ServerSettingsDto.AllowSelfRegistration), settings.AllowSelfRegistration.ToString());
        await Upsert(nameof(ServerSettingsDto.RequireMeetingPasswords), settings.RequireMeetingPasswords.ToString());
        await Upsert(nameof(ServerSettingsDto.MaxParticipantsPerMeeting), settings.MaxParticipantsPerMeeting.ToString());
        await Upsert(nameof(ServerSettingsDto.ListenUrl), settings.ListenUrl.Trim());
        await db.SaveChangesAsync();
    }

    private async Task Upsert(string key, string value)
    {
        var existing = await db.ServerSettings.FindAsync(key);
        if (existing is null) db.ServerSettings.Add(new ServerSetting { Key = key, Value = value });
        else existing.Value = value;
    }

    private static bool GetBool(Dictionary<string, string> stored, string key, bool fallback) =>
        stored.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int GetInt(Dictionary<string, string> stored, string key, int fallback) =>
        stored.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
}
