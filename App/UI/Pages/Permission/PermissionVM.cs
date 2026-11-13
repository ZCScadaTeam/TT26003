using AppZC.Services;
using AppZC.UI.Ext;

namespace AppZC.UI.Pages;

/// <summary>
/// 人员·身份登入页 VM。
/// 仅两种身份：操作员（默认，免密）、管理员（密码 2026）。
/// 配置页面仅管理员身份可进入。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class PermissionVM : UiVM<PermissionPage>
{
	// 可选身份列表（与 DemoSession 角色常量一一对应）
	public IReadOnlyList<string> RoleOptions { get; } = new[]
	{
		"操作员", "管理员",
	};

	private static readonly int[] RoleValues =
	{
		DemoSession.RoleOperator, DemoSession.RoleAdministrator,
	};

	/// <summary>下拉框选中的身份索引（0=操作员，默认；1=管理员）。</summary>
	public partial int SelectedRoleIndex { get; set; } = 0;

	/// <summary>是否选中管理员（选中时才显示密码框）。</summary>
	public partial bool RequiresPassword { get; set; }

	/// <summary>管理员登录密码输入。</summary>
	public partial string PasswordInput { get; set; } = "";

	// 当前登录用户信息
	public partial string CurrentUserInfo { get; set; }
	public partial bool IsLoggedIn { get; set; }

	// 错误提示信息
	public partial string ErrorMessage { get; set; }
	public partial bool HasError { get; set; }

	private System.Threading.Timer? _statusUpdateTimer;

	public PermissionVM()
	{
		ErrorMessage = "";
		HasError = false;
		RequiresPassword = SelectedRoleIndex == 1;

		UpdateLoginStatus();

		// 启动定时器定期更新登录状态（每秒检查一次）
		_statusUpdateTimer = new System.Threading.Timer(_ =>
		{
			UpdateLoginStatus();
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	/// <summary>身份切换时更新密码框显隐（操作员无需密码）。</summary>
	partial void OnSelectedRoleIndexChanged(int value)
	{
		RequiresPassword = value == 1;
		if (!RequiresPassword)
		{
			PasswordInput = "";
			HasError = false;
			ErrorMessage = "";
		}
	}

	/// <summary>
	/// 更新登录状态显示
	/// </summary>
	private void UpdateLoginStatus()
	{
		if (DemoSession.IsLoggedIn)
		{
			CurrentUserInfo = $"当前登录：{DemoSession.Display}";
			IsLoggedIn = true;
		}
		else
		{
			CurrentUserInfo = "未登录";
			IsLoggedIn = false;
		}
	}

	/// <summary>
	/// 身份登入：操作员免密；管理员需输入密码 2026。
	/// </summary>
	void @Login()
	{
		try
		{
			// 清空之前的错误信息
			ErrorMessage = "";
			HasError = false;

			var index = SelectedRoleIndex >= 0 && SelectedRoleIndex < RoleOptions.Count
				? SelectedRoleIndex
				: 0;
			var roleName = RoleOptions[index];
			var roleValue = RoleValues[index];

			// 管理员校验密码
			if (roleValue == DemoSession.RoleAdministrator)
			{
				if (PasswordInput?.Trim() != DemoSession.AdminPassword)
				{
					ErrorMessage = "管理员密码错误，请重新输入。";
					HasError = true;
					LogMgr.Instance.Warn("管理员登录失败：密码错误");
					return;
				}
			}

			DemoSession.Login(roleName, roleValue);
			PasswordInput = "";
			LogMgr.Instance.Info($"身份登录成功：{roleName}");

			// 更新登录状态显示
			UpdateLoginStatus();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"登录失败：{ex.Message}";
			HasError = true;
			LogMgr.Instance.Error($"登录失败: {ex.Message}");
		}
	}

	/// <summary>
	/// 登出：回到未登录态（再次登录默认操作员）。
	/// </summary>
	void @Logout()
	{
		try
		{
			if (DemoSession.IsLoggedIn)
			{
				var userName = DemoSession.UserName;
				DemoSession.Logout();
				PasswordInput = "";
				LogMgr.Instance.Info($"用户 [{userName}] 已登出");

				// 更新登录状态显示
				UpdateLoginStatus();
			}
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"登出失败: {ex.Message}");
		}
	}
}
