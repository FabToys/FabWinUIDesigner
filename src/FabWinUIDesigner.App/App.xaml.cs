using Microsoft.UI.Xaml;

namespace FabWinUIDesigner.App;

/// <summary>The application entry point; owns the single top-level <see cref="MainWindow"/> instance.</summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Initializes the application's XAML resources.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Creates and shows <see cref="MainWindow"/> on app launch.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
