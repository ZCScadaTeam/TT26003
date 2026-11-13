using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using AppZC.Configs;
using AppZC.Services;
using AppZC.UI.Dialogs;

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

			// 退出前：当天报表落盘；有超过保留期的数据才弹窗清理（进度窗），没有则直接退出。
			// 无论清理成功与否都必须继续退出，不能让异常把窗口卡住。
			try { await RunExitCleanupAsync(); }
			finally { this.Close(); }
		}
	}

	/// <summary>
	/// 退出前处理：
	/// 1) 先把当日报表队列里的记录落盘（保证文件完整）；
	/// 2) 检查本地数据是否超过保留期（默认 30 天），有才显示「正在清理」进度窗并清理；
	/// 3) 没有旧数据时什么都不弹，自然退出。
	/// </summary>
	private async Task RunExitCleanupAsync()
	{
		try { ThaDailyReport.Shutdown(); }
		catch (Exception ex) { LogMgr.Instance.Warn($"[THA-EXIT] 报表落盘失败：{ex.Message}"); }

		var cfg = App.Current.IOC.GetOrNull<AppConfig>();
		var keepDays = cfg?.DataRetentionDays ?? 30;
		if (keepDays <= 0) return;

		var reportDir = ThaDailyReport.ReportDirectory;
		bool hasExpired;
		try
		{
			hasExpired = await Task.Run(() => ThaDataRetention.HasExpired(keepDays, reportDir));
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-EXIT] 旧数据检查失败（直接退出）：{ex.Message}");
			return;
		}

		if (!hasExpired) return;

		CleanupDialog? dialog = null;
		try
		{
			dialog = new CleanupDialog($"正在清理 {keepDays} 天前的本地数据…");
			var showTask = dialog.ShowDialog(this);
			try
			{
				var progress = new Progress<string>(dialog.SetStatus);
				await Task.Run(() => ThaDataRetention.Purge(keepDays, reportDir, progress));
			}
			finally
			{
				dialog.Finish();
				try { await showTask; } catch { /* 窗口关闭异常忽略 */ }
			}
		}
		catch (Exception ex)
		{
			// 弹窗本身失败也要保证能退出
			LogMgr.Instance.Error($"[THA-EXIT] 退出前清理本地数据失败：{ex.Message}", ex);
			try { dialog?.Finish(); } catch { /* 忽略 */ }
		}
	}
	
	private async Task<bool> ShowCloseConfirmDialog()
	{
		var dialog = new AppZC.UI.Dialogs.ConfirmDialog("确认退出", "确定要关闭应用程序吗？");
		var result = await dialog.ShowDialog<bool>(this);
		return result;
	}
}