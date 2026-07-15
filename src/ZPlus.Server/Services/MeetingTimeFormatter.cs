using System.Globalization;

namespace ZPlus.Server.Services;

/// <summary>How a meeting time reads to a recipient: the host's local rendering plus UTC.</summary>
/// <param name="Primary">The time in the host's zone and chosen format (or UTC if the zone is unknown).</param>
/// <param name="Utc">The same instant in UTC, or null when it would just repeat <see cref="Primary"/>.</param>
public record MeetingWhen(string Primary, string? Utc);

/// <summary>
/// Renders meeting times for invitations and the join page. Times are stored in UTC; the host
/// records the zone and 12/24-hour preference they scheduled with, so invitations can show both
/// their local wall-clock time and the unambiguous UTC equivalent.
/// </summary>
public static class MeetingTimeFormatter
{
    private const string Pattern24 = "dddd, MMMM d yyyy 'at' HH:mm";
    private const string Pattern12 = "dddd, MMMM d yyyy 'at' h:mm tt";

    /// <summary>
    /// Resolves a zone id. Clients may send a Windows id ("Central Standard Time") or an IANA id
    /// ("America/Chicago"); .NET converts between them, so a Windows client works against a Linux
    /// server. Returns null when the id is empty or unknown — callers then fall back to UTC only.
    /// </summary>
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    public static MeetingWhen Describe(DateTime startUtc, string? hostTimeZoneId, bool use24Hour)
    {
        var utc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        // UTC always reads 24-hour — the international convention, and free of AM/PM ambiguity.
        var utcText = $"{utc.ToString(Pattern24, CultureInfo.InvariantCulture)} UTC";

        var zone = ResolveZone(hostTimeZoneId);
        if (zone is null) return new MeetingWhen(utcText, null);

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var offset = zone.GetUtcOffset(local);
        // A zone that is on UTC would render the same twice — one line is enough.
        if (offset == TimeSpan.Zero) return new MeetingWhen(utcText, null);

        var zoneName = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var offsetText = $"UTC{sign}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
        // The host's line follows the format they scheduled in.
        var pattern = use24Hour ? Pattern24 : Pattern12;
        var localText = $"{local.ToString(pattern, CultureInfo.InvariantCulture)} {zoneName} ({offsetText})";
        return new MeetingWhen(localText, utcText);
    }
}
