using ZPlus.Client.ViewModels;

namespace ZPlus.Mobile.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel = new();
    private bool _loaded;

    public HomePage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.OpenMeetingRequested += async (code, password) =>
            await Navigation.PushAsync(new MeetingPage(code, password));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await _viewModel.RefreshMeetingsAsync();
        await _viewModel.TryPendingJoinAsync();
    }
}
