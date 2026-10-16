namespace AppZC.UI.Ext;

/// <summary>
/// 演示模式会话：替代原 UserAuthService 的登录/登出状态（无数据库、无鉴权）。
/// 角色整型常量与原 UserRole 枚举值保持一致：0=未登录 1=操作员 2=技术员 3=PE 4=ME 5=管理员 6=厂商。
/// 顶栏用户显示（AppVM）、登入/登出（PermissionVM）、权限位判断（MainVM）统一读取本会话。
/// </summary>
public static class DemoSession
{
	public const int RoleOperator = 1;
	public const int RoleTechnician = 2;
	public const int RolePe = 3;
	public const int RoleMe = 4;
	public const int RoleAdministrator = 5;
	public const int RoleVendor = 6;

	public static string UserName { get; private set; } = "";
	public static int Role { get; private set; }

	public static bool IsLoggedIn => !string.IsNullOrEmpty(UserName);

	/// <summary>角色名（供顶栏/登入页展示）。</summary>
	public static string RoleName
	{
		get
		{
			if (!IsLoggedIn) return "未登录";
			return Role switch
			{
				RoleOperator => "操作员",
				RoleTechnician => "技术员",
				RolePe => "PE",
				RoleMe => "ME",
				RoleAdministrator => "管理员",
				RoleVendor => "厂商",
				_ => "未知",
			};
		}
	}

	/// <summary>顶栏显示文本（如「演示管理员 (管理员)」）。</summary>
	public static string Display => IsLoggedIn ? $"{UserName} ({RoleName})" : "未登录";

	public static void Login(string name, int role)
	{
		UserName = name;
		Role = role;
	}

	public static void Logout()
	{
		UserName = "";
		Role = 0;
	}
}