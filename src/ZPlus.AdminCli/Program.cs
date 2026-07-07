using System.Net.Http.Headers;
using System.Net.Http.Json;
using ZPlus.Shared.Dtos;

// zplus-admin — cross-platform command-line administration tool for a Z+ server.

const string Usage = """
    zplus-admin — Z+ server administration

    Usage:
      zplus-admin [connection options] <command> [arguments]

    Connection options (or environment variables):
      --server <url>        Server URL           (ZPLUS_SERVER, default http://localhost:5199)
      --email <email>       Admin email          (ZPLUS_ADMIN_EMAIL)
      --password <pw>       Admin password       (ZPLUS_ADMIN_PASSWORD)
      Missing values are prompted for interactively.

    Commands:
      users list
      users create <email> <display name> <password> <User|Admin|SuperAdmin>
      users update <email> [--role <role>] [--name <display name>] [--enable] [--disable]
      users reset-password <email> <new password>
      settings get
      settings set [--allow-registration on|off] [--require-meeting-passwords on|off]
                   [--max-participants <n>] [--listen-url <url>] [--public-url <url>]
                   [--smtp-host <host>] [--smtp-port <n>] [--smtp-from <email>]
                   [--smtp-user <user>] [--smtp-password <pw>]
      meetings list
      meetings end <meeting id>

    Examples:
      zplus-admin users create carol@corp.local "Carol White" S3cret123 User
      zplus-admin settings set --require-meeting-passwords on --max-participants 50
      zplus-admin meetings end 123-456-789
    """;

var (options, positional) = ParseArgs(args);
if (positional.Count == 0 || positional[0] is "help" or "--help" or "-h")
{
    Console.WriteLine(Usage);
    return 0;
}

var http = new HttpClient
{
    BaseAddress = new Uri(Get("server", "ZPLUS_SERVER") ?? "http://localhost:5199"),
};

try
{
    // ---- sign in -----------------------------------------------------------
    string email = Get("email", "ZPLUS_ADMIN_EMAIL") ?? Prompt("Admin email: ");
    string password = Get("password", "ZPLUS_ADMIN_PASSWORD") ?? Prompt("Password: ", hide: true);

    var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
    if (!login.IsSuccessStatusCode)
        return Fail($"Sign-in failed: {await Detail(login)}");
    var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
    if (auth.User.Role is not (Roles.Admin or Roles.SuperAdmin))
        return Fail("This account does not have administrator rights.");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

    // ---- dispatch ------------------------------------------------------------
    return (positional[0], positional.Skip(1).ToArray()) switch
    {
        ("users", ["list"]) => await UsersList(),
        ("users", ["create", var em, var name, var pw, var role]) => await UsersCreate(em, name, pw, role),
        ("users", ["update", var em]) => await UsersUpdate(em),
        ("users", ["reset-password", var em, var pw]) => await UsersResetPassword(em, pw),
        ("settings", ["get"]) => await SettingsGet(),
        ("settings", ["set"]) => await SettingsSet(),
        ("meetings", ["list"]) => await MeetingsList(),
        ("meetings", ["end", var id]) => await MeetingsEnd(id),
        _ => Fail("Unknown command. Run 'zplus-admin help' for usage."),
    };
}
catch (HttpRequestException ex)
{
    return Fail($"Could not reach the server at {http.BaseAddress}: {ex.Message}");
}

// ---- commands ----------------------------------------------------------------

async Task<int> UsersList()
{
    var users = (await http.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users"))!;
    Console.WriteLine($"{"EMAIL",-32} {"DISPLAY NAME",-24} {"ROLE",-11} {"STATUS",-9} CREATED (UTC)");
    foreach (var u in users)
    {
        Console.WriteLine(
            $"{u.Email,-32} {u.DisplayName,-24} {u.Role,-11} {(u.IsDisabled ? "disabled" : "active"),-9} {u.CreatedAtUtc:yyyy-MM-dd}");
    }
    Console.WriteLine($"{users.Count} user(s).");
    return 0;
}

async Task<int> UsersCreate(string em, string name, string pw, string role)
{
    var response = await http.PostAsJsonAsync("/api/admin/users", new AdminCreateUserRequest(em, name, pw, role));
    if (!response.IsSuccessStatusCode) return Fail(await Detail(response));
    var user = (await response.Content.ReadFromJsonAsync<AdminUserDto>())!;
    Console.WriteLine($"Created {user.Email} ({user.Role}).");
    return 0;
}

async Task<int> UsersUpdate(string em)
{
    var user = await FindUser(em);
    if (user is null) return Fail($"No user with email '{em}'.");

    bool? disabled = options.ContainsKey("disable") ? true : options.ContainsKey("enable") ? false : null;
    var request = new AdminUpdateUserRequest(
        options.GetValueOrDefault("name"), options.GetValueOrDefault("role"), disabled);
    if (request.DisplayName is null && request.Role is null && disabled is null)
        return Fail("Nothing to change. Pass --role, --name, --enable or --disable.");

    var response = await http.PutAsJsonAsync($"/api/admin/users/{user.Id}", request);
    if (!response.IsSuccessStatusCode) return Fail(await Detail(response));
    var updated = (await response.Content.ReadFromJsonAsync<AdminUserDto>())!;
    Console.WriteLine($"{updated.Email}: role={updated.Role}, status={(updated.IsDisabled ? "disabled" : "active")}, name=\"{updated.DisplayName}\".");
    return 0;
}

async Task<int> UsersResetPassword(string em, string pw)
{
    var user = await FindUser(em);
    if (user is null) return Fail($"No user with email '{em}'.");
    var response = await http.PostAsJsonAsync($"/api/admin/users/{user.Id}/reset-password",
        new AdminResetPasswordRequest(pw));
    if (!response.IsSuccessStatusCode) return Fail(await Detail(response));
    Console.WriteLine($"Password reset for {user.Email}.");
    return 0;
}

async Task<int> SettingsGet()
{
    var s = (await http.GetFromJsonAsync<ServerSettingsDto>("/api/admin/settings"))!;
    Console.WriteLine($"allow-registration          {(s.AllowSelfRegistration ? "on" : "off")}");
    Console.WriteLine($"require-meeting-passwords   {(s.RequireMeetingPasswords ? "on" : "off")}");
    Console.WriteLine($"max-participants            {s.MaxParticipantsPerMeeting}");
    Console.WriteLine($"listen-url                  {s.ListenUrl}   (restart required to apply)");
    Console.WriteLine($"public-url                  {(s.PublicUrl == "" ? "(not set — invite links disabled)" : s.PublicUrl)}");
    Console.WriteLine($"smtp-host                   {(s.SmtpHost == "" ? "(not set — email disabled)" : s.SmtpHost)}");
    Console.WriteLine($"smtp-port                   {s.SmtpPort}");
    Console.WriteLine($"smtp-from                   {s.SmtpFrom}");
    Console.WriteLine($"smtp-user                   {s.SmtpUser}");
    Console.WriteLine($"smtp-password               (write-only; set with --smtp-password)");
    return 0;
}

async Task<int> SettingsSet()
{
    var current = (await http.GetFromJsonAsync<ServerSettingsDto>("/api/admin/settings"))!;
    var updated = current with
    {
        AllowSelfRegistration = OnOff("allow-registration") ?? current.AllowSelfRegistration,
        RequireMeetingPasswords = OnOff("require-meeting-passwords") ?? current.RequireMeetingPasswords,
        MaxParticipantsPerMeeting = options.TryGetValue("max-participants", out var m) && int.TryParse(m, out var max)
            ? max : current.MaxParticipantsPerMeeting,
        ListenUrl = options.GetValueOrDefault("listen-url") ?? current.ListenUrl,
        PublicUrl = options.GetValueOrDefault("public-url") ?? current.PublicUrl,
        SmtpHost = options.GetValueOrDefault("smtp-host") ?? current.SmtpHost,
        SmtpPort = options.TryGetValue("smtp-port", out var sp) && int.TryParse(sp, out var smtpPort)
            ? smtpPort : current.SmtpPort,
        SmtpFrom = options.GetValueOrDefault("smtp-from") ?? current.SmtpFrom,
        SmtpUser = options.GetValueOrDefault("smtp-user") ?? current.SmtpUser,
        SmtpPassword = options.GetValueOrDefault("smtp-password") ?? "",
    };
    if (updated == current) return Fail("Nothing to change. Run 'zplus-admin help' for the available flags.");

    var response = await http.PutAsJsonAsync("/api/admin/settings", updated);
    if (!response.IsSuccessStatusCode) return Fail(await Detail(response));
    Console.WriteLine("Settings saved.");
    return await SettingsGet();
}

async Task<int> MeetingsList()
{
    var meetings = (await http.GetFromJsonAsync<List<ActiveMeetingDto>>("/api/admin/meetings/active"))!;
    if (meetings.Count == 0)
    {
        Console.WriteLine("No active meetings.");
        return 0;
    }
    Console.WriteLine($"{"MEETING ID",-13} {"TOPIC",-32} {"HOST",-22} PARTICIPANTS");
    foreach (var m in meetings)
    {
        Console.WriteLine($"{m.MeetingCode,-13} {Trim(m.Topic, 32),-32} {Trim(m.HostDisplayName, 22),-22} {m.ParticipantCount}");
    }
    return 0;
}

async Task<int> MeetingsEnd(string id)
{
    var meetings = (await http.GetFromJsonAsync<List<ActiveMeetingDto>>("/api/admin/meetings/active"))!;
    var digits = new string(id.Where(char.IsDigit).ToArray());
    var meeting = meetings.FirstOrDefault(m =>
        m.Id.ToString().Equals(id, StringComparison.OrdinalIgnoreCase) ||
        (digits.Length > 0 && new string(m.MeetingCode.Where(char.IsDigit).ToArray()) == digits));
    if (meeting is null) return Fail($"No active meeting '{id}'. Run 'zplus-admin meetings list'.");

    var response = await http.PostAsync($"/api/admin/meetings/{meeting.Id}/end", null);
    if (!response.IsSuccessStatusCode) return Fail(await Detail(response));
    Console.WriteLine($"Ended \"{meeting.Topic}\" ({meeting.MeetingCode}); {meeting.ParticipantCount} participant(s) notified.");
    return 0;
}

// ---- helpers -------------------------------------------------------------------

async Task<AdminUserDto?> FindUser(string em)
{
    var users = (await http.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users"))!;
    return users.FirstOrDefault(u => u.Email.Equals(em.Trim(), StringComparison.OrdinalIgnoreCase));
}

string? Get(string option, string env) =>
    options.GetValueOrDefault(option) ?? Environment.GetEnvironmentVariable(env);

bool? OnOff(string option) => options.GetValueOrDefault(option)?.ToLowerInvariant() switch
{
    "on" or "true" or "yes" => true,
    "off" or "false" or "no" => false,
    _ => null,
};

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string Prompt(string label, bool hide = false)
{
    Console.Write(label);
    if (!hide || Console.IsInputRedirected) return (Console.ReadLine() ?? "").Trim('\uFEFF');
    var buffer = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
        else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
    }
}

static async Task<string> Detail(HttpResponseMessage response)
{
    var body = (await response.Content.ReadAsStringAsync()).Trim('"');
    return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)response.StatusCode}" : body;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// Splits args into --key value options (--enable/--disable are valueless flags) and positional arguments.
static (Dictionary<string, string> Options, List<string> Positional) ParseArgs(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--"))
        {
            string key = args[i][2..];
            bool isFlag = key is "enable" or "disable" or "help";
            if (!isFlag && i + 1 < args.Length) options[key] = args[++i];
            else options[key] = "true";
        }
        else
        {
            positional.Add(args[i]);
        }
    }
    return (options, positional);
}
