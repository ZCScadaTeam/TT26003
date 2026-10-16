using ZC.Mvvm;

namespace AppZC.UI.Dialogs;

[ObservableObject(IncludeAllPartialProperty = true)]
public partial class LicenseDialogVM
{
	/// <summary>窗口标题。</summary>
	public partial string Title { get; set; } = "软件授权已到期";

	/// <summary>说明文本（提示用户当前到期日与激活码格式）。</summary>
	public partial string Description { get; set; } = "";

	/// <summary>用户输入的激活码。</summary>
	public partial string ActivationCode { get; set; } = "";

	/// <summary>激活按钮是否启用（文本非空时启用）。</summary>
	public partial bool CanActivate { get; set; }

	/// <summary>校验失败时的错误提示（成功时清空）。</summary>
	public partial string ErrorMessage { get; set; } = "";

	/// <summary>是否有错误提示（XAML 用此控制 IsVisible）。</summary>
	public partial bool HasError { get; set; }

	partial void OnActivationCodeChanged(string value)
	{
		CanActivate = !string.IsNullOrWhiteSpace(value);
		// 重新输入时清掉旧错误，给用户重新提交的机会。
		if (HasError)
		{
			ErrorMessage = "";
			HasError = false;
		}
	}

	public LicenseDialogVM()
	{
		Description =
			"本软件当前已超出授权有效期，请联系软件供应商获取激活码。\n\n" +
			"在下方输入激活码并点击「激活」即可完成续期，授权状态会立即生效。";
	}

	/// <summary>由调用方在激活失败时调用，把错误显示到界面。</summary>
	public void ShowError(string message)
	{
		ErrorMessage = message;
		HasError = true;
	}
}

