namespace AppZC.UI.Ext;

/// <summary>
/// 演示模式会话：替代原 UserAuthService 的登录/登出状态（无数据库、无鉴权）。
/// 仅保留两种身份：1=操作员（默认身份，免密） 5=管理员（密码 2026）；0=未登录。
/// 程序启动即以操作员身份登录；顶栏用户显示（AppVM）、登入/登出（PermissionVM）、
/// 配置页守卫（AppVM）统一读取本会话。
/// </summary>
public static class DemoSession
{
	public const int RoleOperator = 1;
	public const int RoleAdministrator = 5;

	/// <summary>管理员登录密码。</summary>
	public const string AdminPassword = "2026";

	// 默认以操作员身份登录（未调用 Login 前即为操作员权限）
	public static string UserName { get; private set; } = "操作员";
	public static int Role { get; private set; } = RoleOperator;

	public static bool IsLoggedIn => !string.IsNullOrEmpty(UserName);

	/// <summary>是否为管理员（唯一可进入配置页的身份）。</summary>
	public static bool IsAdministrator => IsLoggedIn && Role == RoleAdministrator;

	/// <summary>角色名（供顶栏/登入页展示）。</summary>
	public static string RoleName
	{
		get
		{
			if (!IsLoggedIn) return "未登录";
			return Role switch
			{
				RoleOperator => "操作员",
				RoleAdministrator => "管理员",
				_ => "未知",
			};
		}
	}

	/// <summary>顶栏显示文本（如「操作员 (操作员)」）。</summary>
	public static string Display => IsLoggedIn ? $"{UserName} ({RoleName})" : "未登录";

	public static void Login(string name, int role)
	{
		UserName = name;
		Role = role;
	}

	/// <summary>登出后回到未登录态；再次登录默认仍是操作员。</summary>
	public static void Logout()
	{
		UserName = "";
		Role = 0;
	}

	/// <summary>恢复默认操作员登录（登出后重新进入程序 / 显式重置时调用）。</summary>
	public static void LoginAsDefaultOperator()
	{
		UserName = "操作员";
		Role = RoleOperator;
	}
}
