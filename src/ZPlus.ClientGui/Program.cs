using Avalonia;
using ZPlus.Client.Services;

namespace ZPlus.ClientGui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var launchArg = args.FirstOrDefault(a =>
            a.StartsWith($"{ZPlus.Shared.ZplusLink.Scheme}:", StringComparison.OrdinalIgnoreCase));

        // Single instance: forward a clicked link to the running client and exit.
        if (!DeepLink.TryBecomePrimary(launchArg)) return;

        var pending = DeepLink.FromArgs(args);
        if (pending is not null) DeepLink.SetPendingJoin(pending);

        DeepLink.RegisterLinuxScheme(Environment.ProcessPath ?? "");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
