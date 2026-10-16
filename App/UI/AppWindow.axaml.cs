using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using AppZC.Services;

namespace AppZC.UI;

[AddToIOC(Lifetime = LifetimeType.Singleton, AliasMapTo = [typeof(ApplicationWindow)])]
public partial class AppWindow : ApplicationWindow
{
	public AppWindow()
	{
		InitializeComponent();
		
		// 监听窗口关闭事件
		this.Closing += OnWindowClosing;
		// 无边框窗口默认靠顶部标题栏命中区拖动，但 Content(AppView) 铺满客户区会覆盖该区域，
		// 导致隐式拖动失效。这里显式处理顶部标题栏区域的按下事件来拖动窗口。
		this.PointerPressed += OnWindowPointerPressed;
	}

	private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		// 仅在标题栏高度范围内、且为左键时触发拖动
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
		if (e.GetPosition(this).Y > 30) return;
		// 点击窗口控制按钮（最小化/最大化/关闭/全屏）时不拖动，让其正常响应
		if (e.Source is Button or ToggleSwitch) return;
		BeginMoveDrag(e);
	}
	
	private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
	{
		// 取消默认的关闭行为
		e.Cancel = true;
		
		// 显示确认对话框
		var result = await ShowCloseConfirmDialog();
		
		// 如果用户确认关闭
		if (result)
		{
			// 移除事件处理器，避免再次触发
			this.Closing -= OnWindowClosing;
			
			// 真正关闭窗口
			this.Close();
		}
	}
	
	private async Task<bool> ShowCloseConfirmDialog()
	{
		var dialog = new AppZC.UI.Dialogs.ConfirmDialog("确认退出", "确定要关闭应用程序吗？");
		var result = await dialog.ShowDialog<bool>(this);
		return result;
	}
}