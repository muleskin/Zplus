using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ZPlus.Shared.Dtos;

// zplus-client — cross-platform console meeting client for Z+.
// Joins meetings with live roster and chat. Audio/video capture is not available
// in the console client; use the Windows desktop client for full A/V.

const string Usage = """
    zplus-client — Z+ console meeting client (roster + chat; no audio/video)

    Usage:
      zplus-client [options]

    Options (or environment variables):
      --server <url>       Server URL      (ZPLUS_SERVER, default http://localhost:5199)
      --email <email>      Account email   (ZPLUS_EMAIL)
      --password <pw>      Password        (ZPLUS_PASSWORD)
      --register           Create a new account instead of signing in
      Missing values are prompted for interactively.

    In-meeting commands:
      /who                 List participants
      /msg <name> <text>   Send a private message
      /end                 End the meeting for everyone (host only)
      /leave               Leave the meeting
      Anything else you type is sent as public chat.
    """;

var options = ParseArgs(args);
if (options.ContainsKey("help") || options.ContainsKey("h"))
{
    Console.WriteLine(Usage);
    return 0;
}

string serverUrl = (Get("server", "ZPLUS_SERVER") ?? "http://localhost:5199").TrimEnd('/');
var http = new HttpClient { BaseAddress = new Uri(serverUrl) };

string? token = null;
UserDto? me = null;

try
{
    // ---- sign in / register --------------------------------------------------
    string email = Get("email", "ZPLUS_EMAIL") ?? Prompt("Email: ");
    string password = Get("password", "ZPLUS_PASSWORD") ?? Prompt("Password: ", hide: true);

    HttpResponseMessage authResponse;
    if (options.ContainsKey("register"))
    {
        string displayName = Prompt("Display name: ");
        authResponse = await http.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, displayName, password));
    }
    else
    {
        authResponse = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
    }
    if (!authResponse.IsSuccessStatusCode)
        return Fail($"Sign-in failed: {await Detail(authResponse)}");

    var auth = (await authResponse.Content.ReadFromJsonAsync<AuthResponse>())!;
    token = auth.Token;
    me = auth.User;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    Console.WriteLine($"Signed in as {me.DisplayName} <{me.Email}>.");
}
catch (HttpRequestException ex)
{
    return Fail($"Could not reach the server at {serverUrl}: {ex.Message}");
}

// ---- main menu -----------------------------------------------------------------
while (true)
{
    Console.WriteLine();
    Console.WriteLine("[1] Start an instant meeting   [2] Join a meeting   [3] My meetings   [q] Quit");
    Console.Write("> ");
    switch ((Console.ReadLine() ?? "q").Trim('\uFEFF').Trim().ToLowerInvariant())
    {
        case "1":
        {
            var response = await http.PostAsJsonAsync("/api/meetings",
                new CreateMeetingRequest($"{me!.DisplayName}'s Meeting", null, null, null));
            if (!response.IsSuccessStatusCode) { Console.WriteLine(await Detail(response)); break; }
            var created = (await response.Content.ReadFromJsonAsync<CreateMeetingResponse>())!;
            Console.WriteLine($"Meeting ID: {created.Meeting.MeetingCode} — share it with participants.");
            await RunMeeting(created.Meeting.MeetingCode, null);
            break;
        }
        case "2":
        {
            string code = Prompt("Meeting ID: ");
            string pw = Prompt("Password (Enter if none): ");
            await RunMeeting(code, string.IsNullOrEmpty(pw) ? null : pw);
            break;
        }
        case "3":
        {
            var meetings = (await http.GetFromJsonAsync<List<MeetingDto>>("/api/meetings/mine"))!;
            if (meetings.Count == 0) { Console.WriteLine("No meetings."); break; }
            foreach (var m in meetings)
            {
                Console.WriteLine($"  {m.MeetingCode}  {m.Topic}  {(m.ScheduledStartUtc is null ? "(instant)" : m.ScheduledStartUtc.Value.ToLocalTime().ToString("g"))}");
            }
            break;
        }
        case "q":
            return 0;
    }
}

// ---- meeting session -------------------------------------------------------------

async Task RunMeeting(string meetingCode, string? meetingPassword)
{
    var participants = new Dictionary<string, ParticipantDto>(); // connectionId -> participant
    using var e2ee = new ZPlus.Shared.E2ee.MeetingE2ee();
    var pendingEncrypted = new List<ChatMessageDto>();

    var hub = new HubConnectionBuilder()
        .WithUrl($"{serverUrl}/hubs/meeting", o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
        .WithAutomaticReconnect()
        .Build();

    var ended = new TaskCompletionSource();

    void ShowChat(ChatMessageDto m)
    {
        if (e2ee.TryDecrypt(m.Text, out var plaintext))
            Console.WriteLine($"{(m.IsPrivate ? "[private] " : "")}{m.SenderDisplayName}: {plaintext}");
        else if (ZPlus.Shared.E2ee.MeetingE2ee.IsEncrypted(m.Text))
        {
            if (!e2ee.HasKey) lock (pendingEncrypted) pendingEncrypted.Add(m);
            // With a key but undecryptable: drop (tampered or foreign key).
        }
        else
            Console.WriteLine($"{(m.IsPrivate ? "[private] " : "")}{m.SenderDisplayName}: {m.Text}");
    }

    hub.On<ParticipantDto>(HubEvents.ParticipantJoined, p =>
    {
        participants[p.ConnectionId] = p;
        Console.WriteLine($"* {p.DisplayName} joined");
    });
    hub.On<ParticipantDto>(HubEvents.ParticipantLeft, p =>
    {
        participants.Remove(p.ConnectionId);
        Console.WriteLine($"* {p.DisplayName} left");
    });
    hub.On<ParticipantDto>(HubEvents.ParticipantStateChanged, p =>
    {
        if (participants.ContainsKey(p.ConnectionId)) participants[p.ConnectionId] = p;
    });
    hub.On<ChatMessageDto>(HubEvents.ChatReceived, ShowChat);
    hub.On<ParticipantDto>(HubEvents.HostChanged, p =>
    {
        if (participants.ContainsKey(p.ConnectionId)) participants[p.ConnectionId] = p;
        Console.WriteLine($"* {p.DisplayName} is now the host");
        if (!e2ee.HasKey)
            _ = hub.InvokeAsync(HubMethods.SendSignal, p.ConnectionId,
                ZPlus.Shared.E2ee.MeetingE2ee.SignalPublicKey, e2ee.PublicKeyBase64);
    });
    hub.On<SignalMessage>(HubEvents.SignalReceived, s =>
    {
        if (s.Type == ZPlus.Shared.E2ee.MeetingE2ee.SignalPublicKey && e2ee.HasKey)
        {
            _ = hub.InvokeAsync(HubMethods.SendSignal, s.FromConnectionId,
                ZPlus.Shared.E2ee.MeetingE2ee.SignalWrappedKey, e2ee.WrapKeyFor(s.Payload));
        }
        else if (s.Type == ZPlus.Shared.E2ee.MeetingE2ee.SignalWrappedKey && !e2ee.HasKey &&
                 e2ee.TryUnwrapKey(s.Payload))
        {
            Console.WriteLine("* End-to-end encryption active");
            lock (pendingEncrypted)
            {
                foreach (var m in pendingEncrypted) ShowChat(m);
                pendingEncrypted.Clear();
            }
        }
    });
    hub.On(HubEvents.UnmuteRequested, () => Console.WriteLine("* The host asks you to unmute"));
    hub.On(HubEvents.ForcedMute, () => Console.WriteLine("* The host muted you"));
    hub.On(HubEvents.RemovedFromMeeting, () =>
    {
        Console.WriteLine("* You were removed from the meeting by the host");
        ended.TrySetResult();
    });
    hub.On(HubEvents.MeetingEnded, () =>
    {
        Console.WriteLine("* The host ended the meeting");
        ended.TrySetResult();
    });

    try
    {
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<MeetingJoinedSnapshot>(HubMethods.JoinMeeting, meetingCode, meetingPassword);

        foreach (var p in snapshot.Participants) participants[p.ConnectionId] = p;
        participants[snapshot.Self.ConnectionId] = snapshot.Self;

        Console.WriteLine();
        Console.WriteLine($"=== {snapshot.Meeting.Topic} (ID {snapshot.Meeting.MeetingCode}) ===");
        Console.WriteLine($"{participants.Count} participant(s). You are {(snapshot.Self.IsHost ? "the HOST" : "a participant")}.");
        Console.WriteLine("Console client: chat and roster only — no audio/video. Type /help for commands.");

        // End-to-end encryption: alone → mint the meeting key; otherwise request it from the host.
        if (snapshot.Participants.Count == 0)
        {
            e2ee.CreateMeetingKey();
            Console.WriteLine("* End-to-end encryption active");
        }
        else
        {
            var keyHolder = snapshot.Participants.FirstOrDefault(p => p.IsHost) ?? snapshot.Participants[0];
            await hub.InvokeAsync(HubMethods.SendSignal, keyHolder.ConnectionId,
                ZPlus.Shared.E2ee.MeetingE2ee.SignalPublicKey, e2ee.PublicKeyBase64);
        }

        foreach (var m in snapshot.RecentChat) ShowChat(m);

        // Read stdin on a worker task so a server-side end can close the session too.
        var selfConnectionId = snapshot.Self.ConnectionId;
        _ = Task.Run(async () =>
        {
            while (!ended.Task.IsCompleted)
            {
                string? line = Console.ReadLine();
                if (line is null) { ended.TrySetResult(); return; }   // stdin closed
                // The meeting may have ended (host/admin) while we were blocked reading.
                if (ended.Task.IsCompleted) return;
                line = line.Trim('\uFEFF').Trim();
                if (line.Length == 0) continue;

                try
                {
                    if (line == "/leave")
                    {
                        await hub.InvokeAsync(HubMethods.LeaveMeeting);
                        ended.TrySetResult();
                    }
                    else if (line == "/end") await hub.InvokeAsync(HubMethods.EndMeetingForAll);
                    else if (line == "/help") Console.WriteLine("/who  /msg <name> <text>  /end  /leave");
                    else if (line == "/who")
                    {
                        foreach (var p in participants.Values.OrderByDescending(p => p.IsHost))
                        {
                            Console.WriteLine(
                                $"  {p.DisplayName}{(p.IsHost ? " (host)" : "")}{(p.ConnectionId == selfConnectionId ? " (you)" : "")}{(p.IsMuted ? " [muted]" : "")}");
                        }
                    }
                    else if (line.StartsWith("/msg "))
                    {
                        var parts = line[5..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var target = parts.Length == 2
                            ? participants.Values.FirstOrDefault(p =>
                                p.ConnectionId != selfConnectionId &&
                                p.DisplayName.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
                            : null;
                        if (target is null) Console.WriteLine("Usage: /msg <name> <text>   (name may be a prefix)");
                        else if (!e2ee.HasKey) Console.WriteLine("* Securing chat — try again in a moment");
                        else await hub.InvokeAsync(HubMethods.SendChat, e2ee.Encrypt(parts[1]), target.UserId);
                    }
                    else if (line.StartsWith('/')) Console.WriteLine("Unknown command. /help for commands.");
                    else if (!e2ee.HasKey) Console.WriteLine("* Securing chat — try again in a moment");
                    else await hub.InvokeAsync(HubMethods.SendChat, e2ee.Encrypt(line), null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"! {ex.Message}");
                }
            }
        });

        await ended.Task;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not join: {ex.Message.Replace("HubException: ", "")}");
    }
    finally
    {
        try { await hub.StopAsync(); } catch { /* already gone */ }
        await hub.DisposeAsync();
        Console.WriteLine("--- meeting session closed ---");
    }
}

// ---- helpers ----------------------------------------------------------------------

string? Get(string option, string env) =>
    options.GetValueOrDefault(option) ?? Environment.GetEnvironmentVariable(env);

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

static Dictionary<string, string> ParseArgs(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        string key = args[i][2..];
        bool isFlag = key is "register" or "help" or "h";
        if (!isFlag && i + 1 < args.Length) options[key] = args[++i];
        else options[key] = "true";
    }
    return options;
}
