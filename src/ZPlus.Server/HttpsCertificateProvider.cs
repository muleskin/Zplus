using System.Security.Cryptography.X509Certificates;

namespace ZPlus.Server;

/// <summary>
/// Holds the HTTPS certificate for Kestrel and reloads it automatically when the underlying
/// files change on disk (e.g. after a Let's Encrypt renewal), so renewal does not require a
/// server restart. Kestrel invokes <see cref="Current"/> once per TLS handshake; a background
/// poll swaps in a freshly-loaded certificate when the files' timestamps advance.
/// </summary>
internal sealed class HttpsCertificateProvider
{
    private readonly string _certPath;
    private readonly string? _keyPath;
    private readonly string? _password;
    private readonly Timer _timer;

    private X509Certificate2 _current;
    private DateTime _fileTimeUtc;
    private Action<string>? _log;

    public HttpsCertificateProvider(string certPath, string? keyPath, string? password, TimeSpan pollInterval)
    {
        _certPath = certPath;
        _keyPath = keyPath;
        _password = password;
        _current = CertificateLoader.Load(certPath, keyPath, password);
        _fileTimeUtc = LatestWriteTimeUtc();
        _timer = new Timer(_ => ReloadIfChanged(), null, pollInterval, pollInterval);
    }

    /// <summary>The current certificate; reference reads are atomic, so it is safe to call concurrently.</summary>
    public X509Certificate2 Current => _current;

    /// <summary>Wired after the host is built so reloads are reported through the app logger.</summary>
    public void AttachLogger(Action<string> log) => _log = log;

    private DateTime LatestWriteTimeUtc()
    {
        var newest = File.GetLastWriteTimeUtc(_certPath);
        if (!string.IsNullOrWhiteSpace(_keyPath) && File.Exists(_keyPath))
        {
            var keyTime = File.GetLastWriteTimeUtc(_keyPath);
            if (keyTime > newest) newest = keyTime;
        }
        return newest;
    }

    private void ReloadIfChanged()
    {
        try
        {
            var latest = LatestWriteTimeUtc();
            if (latest <= _fileTimeUtc) return;

            var fresh = CertificateLoader.Load(_certPath, _keyPath, _password);
            // Reference assignment is atomic; in-flight handshakes keep the previous instance,
            // so the old certificate is left for the GC rather than disposed under them.
            _current = fresh;
            _fileTimeUtc = latest;
            _log?.Invoke($"HTTPS certificate reloaded after an on-disk change; valid until {fresh.NotAfter:u}.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"HTTPS certificate reload failed (keeping the current certificate): {ex.Message}");
        }
    }
}
