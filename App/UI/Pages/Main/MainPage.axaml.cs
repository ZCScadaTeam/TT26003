using UiView = ZC.UI.UiView;

namespace AppZC.UI.Pages;

[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class MainPage : UiView
{
	public MainPage()
	{
		InitializeComponent();
	}
}
