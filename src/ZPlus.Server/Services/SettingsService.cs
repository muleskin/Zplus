using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Services;

/// <summary>Reads and writes server-wide settings stored in the database.</summary>
public class SettingsService(AppDbContext db, SecretProtector protector)
{
    private const string SmtpPasswordKey = "SmtpPassword";
    private const string MailgunApiKeyKey = "MailgunApiKey";
    private const string CertPasswordKey = "CertPassword";

    public static readonly ServerSettingsDto Defaults = new(
        AllowSelfRegistration: true,
        RequireMeetingPasswords: false,
        MaxParticipantsPerMeeting: 25,
        ListenUrl: "http://0.0.0.0:5199");

    /// <summary>Returns the settings with SmtpPassword blanked (it is write-only through the API).</summary>
    public async Task<ServerSettingsDto> GetAsync()
    {
        var stored = await db.ServerSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        return new ServerSettingsDto(
            GetBool(stored, nameof(ServerSettingsDto.AllowSelfRegistration), Defaults.AllowSelfRegistration),
            GetBool(stored, nameof(ServerSettingsDto.RequireMeetingPasswords), Defaults.RequireMeetingPasswords),
            GetInt(stored, nameof(ServerSettingsDto.MaxParticipantsPerMeeting), Defaults.MaxParticipantsPerMeeting),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.ListenUrl), Defaults.ListenUrl),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.PublicUrl), ""),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.SmtpHost), ""),
            GetInt(stored, nameof(ServerSettingsDto.SmtpPort), Defaults.SmtpPort),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.SmtpFrom), ""),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.SmtpUser), ""),
            SmtpPassword: "",
            stored.GetValueOrDefault(nameof(ServerSettingsDto.EmailProvider), "SMTP"),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.MailgunDomain), ""),
            stored.GetValueOrDefault(nameof(ServerSettingsDto.MailgunRegion), "us"),
            MailgunApiKey: "",
            stored.GetValueOrDefault(nameof(ServerSettingsDto.CertPath), ""),
            CertPassword: "",
            CertKeyPath: stored.GetValueOrDefault(nameof(ServerSettingsDto.CertKeyPath), ""));
    }

    /// <summary>Decrypts the stored SMTP password for the mailer. Never exposed via the API.</summary>
    public Task<string> GetSmtpPasswordAsync() => GetSecretAsync(SmtpPasswordKey);

    /// <summary>Decrypts the stored Mailgun sending key for the mailer. Never exposed via the API.</summary>
    public Task<string> GetMailgunApiKeyAsync() => GetSecretAsync(MailgunApiKeyKey);

    /// <summary>Decrypts the stored HTTPS certificate password. Never exposed via the API.</summary>
    public Task<string> GetCertPasswordAsync() => GetSecretAsync(CertPasswordKey);

    private async Task<string> GetSecretAsync(string key)
    {
        var row = await db.ServerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key);
        return row is null ? "" : protector.Unprotect(row.Value) ?? "";
    }

    public async Task SaveAsync(ServerSettingsDto settings)
    {
        await Upsert(nameof(ServerSettingsDto.AllowSelfRegistration), settings.AllowSelfRegistration.ToString());
        await Upsert(nameof(ServerSettingsDto.RequireMeetingPasswords), settings.RequireMeetingPasswords.ToString());
        await Upsert(nameof(ServerSettingsDto.MaxParticipantsPerMeeting), settings.MaxParticipantsPerMeeting.ToString());
        await Upsert(nameof(ServerSettingsDto.ListenUrl), settings.ListenUrl.Trim());
        await Upsert(nameof(ServerSettingsDto.PublicUrl), settings.PublicUrl.Trim().TrimEnd('/'));
        await Upsert(nameof(ServerSettingsDto.SmtpHost), settings.SmtpHost.Trim());
        await Upsert(nameof(ServerSettingsDto.SmtpPort), settings.SmtpPort.ToString());
        await Upsert(nameof(ServerSettingsDto.SmtpFrom), settings.SmtpFrom.Trim());
        await Upsert(nameof(ServerSettingsDto.SmtpUser), settings.SmtpUser.Trim());
        await Upsert(nameof(ServerSettingsDto.EmailProvider),
            settings.EmailProvider.Equals("Mailgun", StringComparison.OrdinalIgnoreCase) ? "Mailgun" : "SMTP");
        await Upsert(nameof(ServerSettingsDto.MailgunDomain), settings.MailgunDomain.Trim());
        await Upsert(nameof(ServerSettingsDto.MailgunRegion),
            settings.MailgunRegion.Equals("eu", StringComparison.OrdinalIgnoreCase) ? "eu" : "us");
        // Write-only secrets: an empty value means "keep the current one".
        if (!string.IsNullOrEmpty(settings.SmtpPassword))
            await Upsert(SmtpPasswordKey, protector.Protect(settings.SmtpPassword));
        if (!string.IsNullOrEmpty(settings.MailgunApiKey))
            await Upsert(MailgunApiKeyKey, protector.Protect(settings.MailgunApiKey));
        await Upsert(nameof(ServerSettingsDto.CertPath), settings.CertPath.Trim());
        await Upsert(nameof(ServerSettingsDto.CertKeyPath), settings.CertKeyPath.Trim());
        if (!string.IsNullOrEmpty(settings.CertPassword))
            await Upsert(CertPasswordKey, protector.Protect(settings.CertPassword));
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
