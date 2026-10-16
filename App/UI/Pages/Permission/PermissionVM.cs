using AppZC.Services;
using AppZC.UI.Ext;

namespace AppZC.UI.Pages;

/// <summary>
/// 人员·身份登入页 VM。
/// 无 MES 刷卡环境：选择身份（下拉框）后直接登录，无需密码。
/// 配置页面需技术员（权限≥2）及以上身份方可进入。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class PermissionVM : UiVM<PermissionPage>
{
	// 可选身份列表（与 DemoSession 角色常量一一对应）
	public IReadOnlyList<string> RoleOptions { get; } = new[]
	{
		"操作员", "技术员", "PE", "ME", "管理员", "厂商",
	};

	private static readonly int[] RoleValues =
	{
		DemoSession.RoleOperator, DemoSession.RoleTechnician,
		DemoSession.RolePe, DemoSession.RoleMe,
		DemoSession.RoleAdministrator, DemoSession.RoleVendor,
	};

	/// <summary>下拉框选中的身份索引（0=操作员 ... 5=厂商）。</summary>
	public partial int SelectedRoleIndex { get; set; } = 5;

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

		UpdateLoginStatus();

		// 启动定时器定期更新登录状态（每秒检查一次）
		_statusUpdateTimer = new System.Threading.Timer(_ =>
		{
			UpdateLoginStatus();
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
	/// 身份登入：按所选身份直接登录（无密码）。
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
			DemoSession.Login(roleName, RoleValues[index]);
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
	/// 登出
	/// </summary>
	void @Logout()
	{
		try
		{
			if (DemoSession.IsLoggedIn)
			{
				var userName = DemoSession.UserName;
				DemoSession.Logout();
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