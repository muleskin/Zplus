using System.Windows;
using ZPlus.Admin.Views;

namespace ZPlus.Admin;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new LoginWindow().Show();
    }
}
