using ZPlus.Mobile.Views;

namespace ZPlus.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // A simple navigation stack: Login -> Home -> Meeting.
        return new Window(new NavigationPage(new LoginPage())
        {
            BarBackgroundColor = Color.FromArgb("#FF24262E"),
            BarTextColor = Colors.White,
        });
    }
}
