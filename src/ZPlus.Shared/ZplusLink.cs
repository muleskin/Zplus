namespace ZPlus.Shared;

/// <summary>A parsed <c>zplus://join</c> deep link.</summary>
public record ZplusJoinLink(string Server, string Code, string? Password);

/// <summary>
/// Builds and parses <c>zplus://</c> deep links used to launch the Z+ client straight into
/// a meeting. Format: <c>zplus://join?code=123-456-789&amp;server=http://host:5199&amp;pw=secret</c>.
/// </summary>
public static class ZplusLink
{
    public const string Scheme = "zplus";

    public static string BuildJoin(string server, string code, string? password)
    {
        var query = $"code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(server)) query += $"&server={Uri.EscapeDataString(server)}";
        if (!string.IsNullOrEmpty(password)) query += $"&pw={Uri.EscapeDataString(password)}";
        return $"{Scheme}://join?{query}";
    }

    /// <summary>Parses a zplus:// deep link, or returns null if it is not a valid join link.</summary>
    public static ZplusJoinLink? Parse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        uri = uri.Trim().Trim('"');
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            values[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }

        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) return null;
        values.TryGetValue("server", out var server);
        values.TryGetValue("pw", out var pw);
        return new ZplusJoinLink(server ?? "", code.Trim(), string.IsNullOrEmpty(pw) ? null : pw);
    }
}
