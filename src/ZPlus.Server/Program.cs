using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ZPlus.Server.Data;
using ZPlus.Server.Hubs;
using ZPlus.Server.Models;
using ZPlus.Server.Services;
using ZPlus.Shared.Dtos;

// All server state lives next to the executable: zplus.db (users + configuration)
// and zplus.key (AES/HMAC master key protecting secrets inside the database).
// Both paths can be overridden with environment variables for service deployments.
string baseDir = AppContext.BaseDirectory;
string dbPath = Environment.GetEnvironmentVariable("ZPLUS_DB") ?? Path.Combine(baseDir, "zplus.db");
string keyPath = Environment.GetEnvironmentVariable("ZPLUS_KEY") ?? Path.Combine(baseDir, "zplus.key");
string connectionString = $"Data Source={dbPath}";

var protector = SecretProtector.LoadOrCreate(keyPath);
var passwords = new PasswordService(protector);

// Bootstrap pass: create the schema, then load (or generate) the configuration the host
// itself needs before it can start — the JWT signing key and the listen URL.
byte[] jwtSigningKey;
string listenUrl;
string certPath = "";
string certKeyPath = "";
string certPassword = "";
bool seededAdmin = false;
List<string> upgradedObjects = [];
string seedEmail = (Environment.GetEnvironmentVariable("ZPLUS_ADMIN_EMAIL") ?? "admin@zplus.local")
    .Trim().ToLowerInvariant();
{
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
    using var db = new AppDbContext(options);
    db.Database.EnsureCreated();
    // Add any tables/indexes the model gained since this database was created
    // (e.g. MeetingInvitations) so existing databases don't need to be recreated.
    upgradedObjects = SchemaUpgrader.EnsureUpToDate(db);

    const string jwtKeyName = "JwtSigningKey";
    var jwtRow = db.ServerSettings.Find(jwtKeyName);
    string? jwtKeyBase64 = jwtRow is null ? null : protector.Unprotect(jwtRow.Value);
    if (jwtKeyBase64 is null)
    {
        // First run (or the key file changed): issue a fresh signing key.
        jwtKeyBase64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var protectedValue = protector.Protect(jwtKeyBase64);
        if (jwtRow is null) db.ServerSettings.Add(new ServerSetting { Key = jwtKeyName, Value = protectedValue });
        else jwtRow.Value = protectedValue;
    }
    jwtSigningKey = Convert.FromBase64String(jwtKeyBase64);

    listenUrl = db.ServerSettings.Find(nameof(ServerSettingsDto.ListenUrl))?.Value
        ?? SettingsService.Defaults.ListenUrl;

    certPath = db.ServerSettings.Find(nameof(ServerSettingsDto.CertPath))?.Value ?? "";
    certKeyPath = db.ServerSettings.Find(nameof(ServerSettingsDto.CertKeyPath))?.Value ?? "";
    var certPwRow = db.ServerSettings.Find("CertPassword");
    certPassword = certPwRow is null ? "" : protector.Unprotect(certPwRow.Value) ?? "";

    // Seed the initial super admin if no admin account exists yet.
    if (!db.Users.Any(u => u.Role == Roles.Admin || u.Role == Roles.SuperAdmin))
    {
        var password = Environment.GetEnvironmentVariable("ZPLUS_ADMIN_PASSWORD") ?? "ChangeMe123!";
        var existing = db.Users.SingleOrDefault(u => u.Email == seedEmail);
        if (existing is not null)
        {
            existing.Role = Roles.SuperAdmin;
        }
        else
        {
            db.Users.Add(new User
            {
                Email = seedEmail,
                DisplayName = "Administrator",
                Role = Roles.SuperAdmin,
                PasswordHash = passwords.Protect(password),
            });
        }
        seededAdmin = true;
    }

    db.SaveChanges();
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listenUrl);

// Native HTTPS: when the listen URL is https, bind Kestrel with the configured certificate.
// The provider reloads the certificate automatically when its files change on disk (renewal).
ZPlus.Server.HttpsCertificateProvider? certProvider = null;
if (listenUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(certPath))
        throw new InvalidOperationException(
            "The listen URL is https:// but no certificate is configured. Set an HTTPS certificate path in server settings, or use an http:// listen URL.");
    if (!File.Exists(certPath))
        throw new InvalidOperationException($"HTTPS certificate file not found on the server: {certPath}");
    if (!string.IsNullOrWhiteSpace(certKeyPath) && !File.Exists(certKeyPath))
        throw new InvalidOperationException($"HTTPS private-key file not found on the server: {certKeyPath}");

    // How often to check for a renewed certificate on disk (default 5 minutes, min 5s).
    var pollSeconds = int.TryParse(Environment.GetEnvironmentVariable("ZPLUS_CERT_RELOAD_SECONDS"), out var s)
        ? Math.Max(5, s) : 300;
    try
    {
        certProvider = new ZPlus.Server.HttpsCertificateProvider(
            certPath, certKeyPath, certPassword, TimeSpan.FromSeconds(pollSeconds));
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"Could not load the HTTPS certificate '{certPath}': {ex.Message}", ex);
    }
    builder.WebHost.ConfigureKestrel(options =>
        options.ConfigureHttpsDefaults(https => https.ServerCertificateSelector = (_, _) => certProvider!.Current));
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddSingleton(protector);
builder.Services.AddSingleton(passwords);
builder.Services.AddSingleton(new JwtConfig(jwtSigningKey));
builder.Services.AddSingleton<MeetingStateStore>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = JwtConfig.Issuer,
            ValidAudience = JwtConfig.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKey),
        };

        // SignalR WebSocket connections pass the JWT via query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

certProvider?.AttachLogger(message => app.Logger.LogInformation("{Message}", message));

app.Logger.LogInformation("Database: {DbPath}", dbPath);
app.Logger.LogInformation("Key file: {KeyPath}", keyPath);
if (seededAdmin)
{
    app.Logger.LogWarning(
        "Seeded super admin account {Email}. Change its password immediately via the admin app.", seedEmail);
}
if (upgradedObjects.Count > 0)
{
    app.Logger.LogWarning("Schema upgrade added missing database objects: {Objects}",
        string.Join(", ", upgradedObjects));
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MeetingHub>("/hubs/meeting");
app.MapGet("/", () => "Z+ server is running.");

// Invitation link target: a small public page with the meeting details and a button
// that launches the Z+ app straight into the join screen via the zplus:// deep link.
app.MapGet("/join/{code}", async (string code, string? pw, AppDbContext db, SettingsService settings) =>
{
    var digits = new string(code.Where(char.IsDigit).ToArray());
    string normalized = digits.Length == 9 ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}" : code.Trim();
    var meeting = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .SingleOrDefaultAsync(db.Meetings, m => m.MeetingCode == normalized && m.EndedAtUtc == null);

    string inner;
    if (meeting is null)
    {
        inner = "<h2>Meeting not found</h2><p>This invitation link is no longer valid.</p>";
    }
    else
    {
        var serverUrl = (await settings.GetAsync()).PublicUrl;
        var deepLink = ZPlus.Shared.ZplusLink.BuildJoin(serverUrl, meeting.MeetingCode, pw);
        inner = $"""
           <h2>{System.Net.WebUtility.HtmlEncode(meeting.Topic)}</h2>
           <p>{(meeting.ScheduledStartUtc is null
                 ? "This meeting is available now."
                 : $"Scheduled for {meeting.ScheduledStartUtc.Value:dddd, MMMM d yyyy HH:mm} UTC")}</p>
           <p class="code">{meeting.MeetingCode}</p>
           <a class="btn" href="{System.Net.WebUtility.HtmlEncode(deepLink)}">Open in Z+ app</a>
           <p class="hint">This opens the Z+ desktop app and takes you to the join screen. If nothing
           happens, open Z+ manually, choose <b>Join a meeting</b> and enter the meeting ID above.{(
               meeting.PasswordHash is null ? "" : " Use the password from your invitation.")}</p>
           """;
    }

    string html = $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Z+ meeting invitation</title>
        <style>
          body { font-family: 'Segoe UI', Arial, sans-serif; background: #1B1D22; color: #F2F2F2;
                 display: flex; justify-content: center; padding-top: 8vh; }
          .card { background: #24262E; border-radius: 12px; padding: 32px 40px; max-width: 420px; }
          .brand { color: #2D8CFF; font-size: 28px; font-weight: bold; }
          .code { font-size: 30px; font-weight: bold; letter-spacing: 2px; color: #2D8CFF; }
          p { color: #C9CED6; line-height: 1.5; }
          .hint { font-size: 13px; color: #9AA0AA; }
          .btn { display: inline-block; margin: 8px 0 16px; padding: 12px 22px; background: #2D8CFF;
                 color: #fff; text-decoration: none; border-radius: 8px; font-weight: 600; }
        </style></head>
        <body><div class="card"><div class="brand">Z+</div>{{inner}}</div></body></html>
        """;
    return Results.Content(html, "text/html");
});

app.Run();
