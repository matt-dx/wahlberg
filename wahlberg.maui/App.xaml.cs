namespace Wahlberg;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Wahlberg" };
#if WINDOWS
		window.Destroying += (_, _) => Wahlberg.WinUI.App.Cleanup();
#endif
		return window;
	}
}
