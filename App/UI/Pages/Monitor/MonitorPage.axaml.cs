using Avalonia.VisualTree;
using UiView = ZC.UI.UiView;

namespace AppZC.UI.Pages.Monitor;

[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class MonitorPage : UiView
{
	public MonitorPage()
	{
		InitializeComponent();
	}

	/// <summary>页面挂到视觉树：启动 500ms 自动刷新（幂等）。</summary>
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (DataContext is MonitorVM vm) vm.StartAutoRefresh();
	}

	/// <summary>页面离开视觉树：停止自动刷新，防止定时器泄漏。</summary>
	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		if (DataContext is MonitorVM vm) vm.StopAutoRefresh();
	}
}
