using System.Windows;
using ZPlus.Client.Services;
using ZPlus.Client.ViewModels;
using ZPlus.Client.Views;

namespace ZPlus.Client;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Register the zplus:// scheme so invitation links launch this client.
        WindowsProtocol.Register(Environment.ProcessPath ?? "");

        var launchArg = e.Args.FirstOrDefault(a => a.StartsWith($"{ZPlus.Shared.ZplusLink.Scheme}:", StringComparison.OrdinalIgnoreCase));

        // Single instance: hand a clicked link to the already-running client and exit.
        if (!DeepLink.TryBecomePrimary(launchArg))
        {
            Shutdown();
            return;
        }

        var pending = DeepLink.FromArgs(e.Args);
        if (pending is not null) DeepLink.SetPendingJoin(pending);

        // A link arriving while we're already running (via the single-instance pipe).
        DeepLink.LinkActivated += () => Dispatcher.BeginInvoke(HandleActivatedLink);

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new LoginWindow().Show();
    }

    private void HandleActivatedLink()
    {
        // Prefer the signed-in Home window; otherwise the pending join is honoured after login.
        var home = Windows.OfType<HomeWindow>().FirstOrDefault();
        if (home is not null)
        {
            home.Activate();
            _ = home.HandlePendingJoinAsync();
        }
        else
        {
            (Windows.OfType<LoginWindow>().FirstOrDefault() as Window)?.Activate();
        }
    }
}
