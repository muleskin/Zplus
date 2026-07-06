using System.Windows;
using ZPlus.Client.Views;

namespace ZPlus.Client;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new LoginWindow().Show();
    }
}
