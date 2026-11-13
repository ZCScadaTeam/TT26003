using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AppZC.UI.Dialogs;

/// <summary>
/// 退出前旧数据清理进度窗：无按钮、不可手动关闭，只显示当前清理阶段与无限进度条。
/// 清理在主线程外执行，<see cref="SetStatus"/> 自动切回 UI 线程；完成后由调用方 <see cref="Finish"/>。
/// </summary>
public partial class CleanupDialog : Window
{
	private readonly TextBlock? _statusText;
	private bool _allowClose;

	public CleanupDialog()
	{
		AvaloniaXamlLoader.Load(this);
		_statusText = this.FindControl<TextBlock>("StatusText");
		Closing += (_, e) => e.Cancel = !_allowClose;
	}

	public CleanupDialog(string status) : this()
	{
		SetStatus(status);
	}

	/// <summary>更新当前清理阶段（可从任意线程调用）。</summary>
	public void SetStatus(string text)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			if (_statusText != null) _statusText.Text = text;
			return;
		}
		Dispatcher.UIThread.Post(() =>
		{
			if (_statusText != null) _statusText.Text = text;
		});
	}

	/// <summary>清理结束，允许关闭窗口。</summary>
	public void Finish()
	{
		if (Dispatcher.UIThread.CheckAccess()) CloseInternal();
		else Dispatcher.UIThread.Post(CloseInternal);
	}

	private void CloseInternal()
	{
		_allowClose = true;
		Close();
	}

	private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			BeginMoveDrag(e);
	}
}
