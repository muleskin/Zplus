using Microsoft.Win32;
using ZPlus.Shared;

namespace ZPlus.Client.Services;

/// <summary>
/// Registers the <c>zplus://</c> URI scheme for the current user on Windows (HKCU, no admin
/// rights needed) so clicking an invitation link launches this client. Idempotent.
/// </summary>
public static class WindowsProtocol
{
    public static void Register(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ZplusLink.Scheme}");
            var command = $"\"{exePath}\" \"%1\"";
            // Skip the write if it already points here — avoids churn on every launch.
            using (var existing = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ZplusLink.Scheme}\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command) return;
            }

            scheme.SetValue(null, "URL:Z+ Protocol");
            scheme.SetValue("URL Protocol", "");
            using var cmd = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ZplusLink.Scheme}\shell\open\command");
            cmd.SetValue(null, command);
        }
        catch
        {
            // Registration is best-effort; the client still works, links just won't auto-open.
        }
    }
}
