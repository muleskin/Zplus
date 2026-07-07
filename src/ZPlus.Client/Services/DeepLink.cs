using System.IO;
using System.IO.Pipes;
using ZPlus.Shared;

namespace ZPlus.Client.Services;

/// <summary>
/// Cross-platform deep-link plumbing shared by the WPF and Avalonia clients:
/// holds a pending <c>zplus://join</c> request, provides single-instance hand-off so a
/// running client handles a clicked link, and registers the URI scheme on Linux.
/// (Windows scheme registration lives in the WPF client, which owns that handler.)
/// </summary>
public static class DeepLink
{
    // Plain (session-local) names — cross-platform safe on Windows and Linux.
    private const string MutexName = "ZPlusClientSingleInstance";
    private const string PipeName = "ZPlusClientDeepLink";

    private static System.Threading.Mutex? _mutex;

    /// <summary>A join request awaiting the user to finish signing in.</summary>
    public static ZplusJoinLink? PendingJoin { get; private set; }

    /// <summary>Raised (on a background thread) when a link arrives while the app is running.</summary>
    public static event Action? LinkActivated;

    /// <summary>Records a join request and pre-points the session at its server.</summary>
    public static void SetPendingJoin(ZplusJoinLink link)
    {
        PendingJoin = link;
        if (!string.IsNullOrWhiteSpace(link.Server))
            AppSession.Current.ServerUrl = link.Server;
    }

    public static ZplusJoinLink? TakePendingJoin()
    {
        var pending = PendingJoin;
        PendingJoin = null;
        return pending;
    }

    /// <summary>Extracts a zplus:// join link from the process command line, if present.</summary>
    public static ZplusJoinLink? FromArgs(string[] args) =>
        args.Select(ZplusLink.Parse).FirstOrDefault(l => l is not null);

    /// <summary>
    /// Acquires the single-instance lock. Returns true for the first instance (which then
    /// listens for links from later launches). For a subsequent instance, forwards the
    /// launch argument to the running one and returns false — the caller should exit.
    /// </summary>
    public static bool TryBecomePrimary(string? launchArg)
    {
        _mutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out bool created);
        if (created)
        {
            StartPipeServer();
            return true;
        }
        if (!string.IsNullOrWhiteSpace(launchArg)) ForwardToPrimary(launchArg);
        return false;
    }

    private static void StartPipeServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var message = (await reader.ReadToEndAsync()).Trim();
                    var link = ZplusLink.Parse(message);
                    if (link is not null)
                    {
                        SetPendingJoin(link);
                        LinkActivated?.Invoke();
                    }
                }
                catch
                {
                    // A malformed/aborted connection shouldn't stop us serving the next one.
                }
            }
        });
    }

    private static void ForwardToPrimary(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(message);
            writer.Flush();
        }
        catch
        {
            // The primary may be shutting down; nothing more we can do from here.
        }
    }

    /// <summary>
    /// Registers the zplus:// scheme on Linux by writing a .desktop handler and pointing
    /// xdg-mime at it. Safe to call on every launch; a no-op on non-Linux.
    /// </summary>
    public static void RegisterLinuxScheme(string execPath)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            // Desktop entries belong in ~/.local/share/applications.
            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
            Directory.CreateDirectory(appsDir);

            var desktopPath = Path.Combine(appsDir, "zplus-client.desktop");
            var contents =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Z+ Client\n" +
                $"Exec=\"{execPath}\" %u\n" +
                "MimeType=x-scheme-handler/zplus;\n" +
                "NoDisplay=true\n" +
                "Terminal=false\n";
            File.WriteAllText(desktopPath, contents);

            RunQuiet("xdg-mime", "default zplus-client.desktop x-scheme-handler/zplus");
            RunQuiet("update-desktop-database", appsDir);
        }
        catch
        {
            // Registration is best-effort; the app still runs without it.
        }
    }

    private static void RunQuiet(string file, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(3000);
        }
        catch
        {
            // xdg tools may be absent on minimal systems; ignore.
        }
    }
}
