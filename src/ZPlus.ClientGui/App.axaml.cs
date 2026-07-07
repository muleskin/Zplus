using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZPlus.Client.Services;
using ZPlus.ClientGui.Views;

namespace ZPlus.ClientGui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new LoginWindow();

            // A zplus:// link arriving while we're already running (single-instance pipe).
            DeepLink.LinkActivated += () => Dispatcher.UIThread.Post(() => HandleActivatedLink(desktop));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void HandleActivatedLink(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Prefer the signed-in Home window; otherwise the pending join is honoured after login.
        var home = desktop.Windows.OfType<HomeWindow>().FirstOrDefault();
        if (home is not null)
        {
            home.Activate();
            _ = home.HandlePendingJoinAsync();
        }
        else
        {
            desktop.Windows.OfType<LoginWindow>().FirstOrDefault()?.Activate();
        }
    }
}
