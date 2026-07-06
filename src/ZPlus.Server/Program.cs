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
bool seededAdmin = false;
string seedEmail = (Environment.GetEnvironmentVariable("ZPLUS_ADMIN_EMAIL") ?? "admin@zplus.local")
    .Trim().ToLowerInvariant();
{
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
    using var db = new AppDbContext(options);
    db.Database.EnsureCreated();

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

app.Logger.LogInformation("Database: {DbPath}", dbPath);
app.Logger.LogInformation("Key file: {KeyPath}", keyPath);
if (seededAdmin)
{
    app.Logger.LogWarning(
        "Seeded super admin account {Email}. Change its password immediately via the admin app.", seedEmail);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MeetingHub>("/hubs/meeting");
app.MapGet("/", () => "Z+ server is running.");

app.Run();
