using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AppZC.UI.Dialogs;

public partial class LicenseDialog : Window
{
	public LicenseDialog()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public LicenseDialog(LicenseDialogVM vm) : this()
	{
		DataContext = vm;
		Title = vm.Title;
	}

	/// <summary>关闭按钮（标题栏右上角 ×）。</summary>
	private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

	/// <summary>激活按钮：触发激活并把结果写到 ViewModel.ErrorMessage，由 code-behind 读取后决定是否关闭弹窗。</summary>
	public event EventHandler<ActivateRequestEventArgs>? ActivateRequested;

	/// <summary>激活成功后由调用方调用以关闭弹窗。</summary>
	public void OnActivationSucceeded() => Close(true);

	/// <summary>激活失败时调用，弹窗继续显示，错误信息已写到 ErrorMessage。</summary>
	public void OnActivationFailed(string error)
	{
		if (DataContext is LicenseDialogVM vm) vm.ShowError(error);
	}

	private void OnActivateClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not LicenseDialogVM vm) return;
		ActivateRequested?.Invoke(this, new ActivateRequestEventArgs(vm.ActivationCode));
	}

	/// <summary>标题栏按下时开始拖动窗口。</summary>
	private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			BeginMoveDrag(e);
		}
	}
}

public sealed class ActivateRequestEventArgs(string code) : EventArgs
{
	public string Code { get; } = code;
}
