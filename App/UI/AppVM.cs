using System.Collections.ObjectModel;
using AppZC.UI.Dialogs;
using AppZC.UI.Ext;
using AppZC.UI.Layouts;
using AppZC.UI.Pages;
using AppZC.UI.Pages.Monitor;
using AppZC.UI.Pages.Warning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using ZC.Mvvm;
using ZC.UI.Models;

namespace AppZC.UI;

[ApiController("/App")]
[AddToIOC(Lifetime = LifetimeType.Singleton,
	AliasMapTo = [typeof(IApplicationViewModel), typeof(ApplicationViewModel)])]
[ObservableObject(IncludeAllPartialProperty = true)]
public sealed partial class AppVM : ApplicationViewModel<AppView>
{
	public partial bool HasAdminRole { get; set; }
	
	// 当前登录用户信息
	public partial string CurrentUserDisplay { get; set; } = "未登录";
	
	// 全局状态显示属性（模板中性）
	public partial string GlobalCurrentTime { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
	public partial string GlobalSoftwareVersion { get; set; } = "版本: --";

	// 窗口是否最大化
	public partial bool IsWindowMaximized { get; set; }
	
	[Inject] public Configs.AppConfig AppConfig { get; set; } = null!;
	[Inject] public Services.LicenseService License { get; set; } = null!;
	
	private System.Threading.Timer? _globalStatusTimer;

	public override IEnumerable<INavigationInfo> CreateNavigations() => new NavigationInfo[]
	{
		new("/主页") { ViewModel = typeof(MainVM), AllowClose = false },
		new("/监控") { ViewModel = typeof(MonitorVM), AllowClose = false },
		new("/统计") { ViewModel = typeof(DateVM), AllowClose = false },
		new("/配置") { ViewModel = typeof(ConfigVM), AllowClose = false },
		new("/人员") { ViewModel = typeof(PermissionVM), AllowClose = false },
		new("/帮助") { ViewModel = typeof(HelpVM), AllowClose = false },
	};

	// 为每个页面定义左侧导航项
	private Dictionary<string, IEnumerable<INavigationInfo>>? _leftNavigations;
	private Dictionary<string, IEnumerable<INavigationInfo>> LeftNavigations
	{
		get
		{
			if (_leftNavigations == null)
			{
				_leftNavigations = new()
				{
					["主页"] = new NavigationInfo[]
					{
						new("/主页/主页面") { ViewModel = typeof(MainVM), AllowClose = false },
					},
					["监控"] = new NavigationInfo[]
					{
						new("/监控/交互结构体") { ViewModel = typeof(MonitorVM), AllowClose = false },
					},
					["统计"] = new NavigationInfo[]
					{
						new("/统计/数据概览") { ViewModel = typeof(DateVM), AllowClose = false },
					},
					["配置"] = new NavigationInfo[]
					{
						new("/配置/连接设置") { ViewModel = typeof(ConfigVM), AllowClose = false },
					},
					["消息"] = new NavigationInfo[]
					{
						new("/消息/操作记录") { ViewModel = typeof(LogVM), AllowClose = false },
					},
					["人员"] = new NavigationInfo[]
					{
						new("/人员/身份登入") { ViewModel = typeof(PermissionVM), AllowClose = false },
					},
				};
			}
			return _leftNavigations;
		}
	}

	public required AppStartUpVM AppStartUpVM { get; init; }

	public partial object? SelectedNavData { get; set; }

	// 左侧导航栏的导航项
	public partial ObservableCollection<INavigationInfo>? LeftNavItems { get; set; }

	// 左侧导航栏是否可见（主页隐藏，其它页签显示）
	public partial bool LeftNavHasItems { get; set; }
	
	// 左侧导航栏选中的项
	public partial object? SelectedLeftNavData { get; set; }

	private bool _isUpdatingNavigation = false;
	private object? _previousNavData = null; // 保存上一次的顶部导航选择
	private object? _previousLeftNavData = null; // 保存上一次的左侧导航选择

	partial void OnSelectedNavDataChanged(object? value)
	{
		if (AppStartUpVM.IsCompleted == false || _isUpdatingNavigation) return;

		// 配置页守卫：技术员（权限≥2）及以上身份登录后方可访问
		if (value is INavigationInfo navInfo && navInfo.Header?.ToString() == "配置")
		{
			if (!DemoSession.IsLoggedIn || DemoSession.Role < DemoSession.RoleTechnician)
			{
				var who = DemoSession.IsLoggedIn
					? $"{DemoSession.UserName}（{DemoSession.RoleName}）"
					: "未登录";
				ShowMessageDialogAndRestoreTopNav("权限不足",
					$"当前身份：{who}。\n\n请先到「人员 → 身份登入」页面以技术员及以上身份登录后再访问配置页面。");
				return;
			}
		}

		_previousNavData = value;

		_isUpdatingNavigation = true;
		try
		{
			NavManager.Navigate(value);
			// 更新左侧导航项
			UpdateLeftNavItems();
		}
		finally
		{
			_isUpdatingNavigation = false;
		}
	}

	partial void OnSelectedLeftNavDataChanged(object? value)
	{
		if (_isUpdatingNavigation) return;

		if (value is INavigationInfo navInfo)
		{
			// 演示模式：无鉴权，直接放行
			_previousLeftNavData = value;

			_isUpdatingNavigation = true;
			try
			{
				NavManager.Navigate(navInfo);
			}
			finally
			{
				_isUpdatingNavigation = false;
			}
		}
	}
	
	/// <summary>
	/// 恢复到之前的左侧导航选择
	/// </summary>
	private void RestorePreviousLeftNavSelection()
	{
		_isUpdatingNavigation = true;
		try
		{
			SelectedLeftNavData = _previousLeftNavData;
		}
		finally
		{
			_isUpdatingNavigation = false;
		}
	}
	
	/// <summary>
	/// 恢复到之前的顶部导航选择
	/// </summary>
	private void RestorePreviousTopNavSelection()
	{
		_isUpdatingNavigation = true;
		try
		{
			SelectedNavData = _previousNavData;
		}
		finally
		{
			_isUpdatingNavigation = false;
		}
	}
	
	/// <summary>
	/// 显示消息对话框并在关闭后恢复之前的左侧导航选择
	/// </summary>
	private async void ShowMessageDialogAndRestore(string title, string message)
	{
		await ShowSimpleDialog(title, message);
		
		// 对话框关闭后，恢复到之前的选择
		Avalonia.Threading.Dispatcher.UIThread.Post(() => RestorePreviousLeftNavSelection());
	}
	
	/// <summary>
	/// 显示消息对话框并在关闭后恢复之前的顶部导航选择
	/// </summary>
	private async void ShowMessageDialogAndRestoreTopNav(string title, string message)
	{
		await ShowSimpleDialog(title, message);
		
		// 对话框关闭后，恢复到之前的顶部导航选择
		Avalonia.Threading.Dispatcher.UIThread.Post(() => RestorePreviousTopNavSelection());
	}
	
	/// <summary>
	/// 显示消息对话框（不恢复导航）
	/// </summary>
	private async void ShowMessageDialog(string title, string message)
	{
		await ShowSimpleDialog(title, message);
	}
	
	/// <summary>
	/// 显示简单对话框
	/// </summary>
	private async Task ShowSimpleDialog(string title, string message)
	{
		var dialog = new MessageDialog(title, message);
		
		var mainWindow = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop 
			? desktop.MainWindow 
			: null;
		
		if (mainWindow != null)
		{
			await dialog.ShowDialog(mainWindow);
		}
	}

	private void UpdateLeftNavItems()
	{
		// 根据当前选中的顶部导航项更新左侧导航项
		if (SelectedNavData is INavigationInfo navInfo)
		{
			var header = navInfo.Header?.ToString() ?? "";

			// 主页不显示左侧导航栏（仅首页全宽展示）
			if (header.Contains("主页"))
			{
				LeftNavItems = null;
				SelectedLeftNavData = null;
				LeftNavHasItems = false;
				return;
			}

			if (LeftNavigations.TryGetValue(header, out var leftNavs))
			{
				LeftNavItems = new ObservableCollection<INavigationInfo>(leftNavs);
				SelectedLeftNavData = LeftNavItems.FirstOrDefault();
				LeftNavHasItems = LeftNavItems.Count > 0;
			}
			else
			{
				LeftNavItems = null;
				SelectedLeftNavData = null;
				LeftNavHasItems = false;
			}
		}
		else
		{
			LeftNavItems = null;
			SelectedLeftNavData = null;
			LeftNavHasItems = false;
		}
	}


	[Inject]
	public void Initialize(AppStartUpVM appStartUpVM)
	{
		// 默认 UI 版本改为浅色：未手动选择主题，或选择「默认(跟随系统)」时，统一以亮色启动。
		// 已显式选择其它主题（暗色/Aquatic 等）的用户偏好仍然保留。
		var themes = CreateThemes().ToList();
		var light = themes.FirstOrDefault(t => t.Theme == ThemeVariant.Light);
		if (light is not null && (SelectedTheme is null || SelectedTheme.Theme == ThemeVariant.Default))
			SelectedTheme = light;

		UiTick = 100;
		LayoutView = App.Current.IOC.Get<StartUpLayout>();
		
		// 监听窗口状态变化，更新最大化状态
		var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop 
			? desktop.MainWindow as AppWindow
			: null;
		
		if (window != null)
		{
			// 设置初始状态
			IsWindowMaximized = window.WindowState == WindowState.Maximized;
			
			// 监听窗口状态变化
			window.GetObservable(Window.WindowStateProperty).Subscribe(state =>
			{
				IsWindowMaximized = state == WindowState.Maximized;
			});
		}
		
		appStartUpVM.OnCompleted += () =>
		{
			LayoutView = App.Current.IOC.Get<TopAppBarLayout>();
			NavManager.Navigate(SelectedNavData);
			UpdateLeftNavItems();
			UpdateCurrentUserDisplay();
			
			// 启动定时器定期更新用户显示
			StartUserDisplayTimer();
			
			// 启动时钟定时器
			StartClockTimer();

			// 启动授权到期守卫
			StartLicenseGuard();
			
			// UI加载完成后，再次更新窗口状态
			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
			{
				var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop 
					? desktop.MainWindow as AppWindow
					: null;
				
				if (window != null)
				{
					IsWindowMaximized = window.WindowState == WindowState.Maximized;
				}
			}, Avalonia.Threading.DispatcherPriority.Loaded);
			
			return Task.CompletedTask;
		};
		NavManager.Navigate(appStartUpVM);
	}
	
	private System.Threading.Timer? _userDisplayTimer;
	
	/// <summary>
	/// 启动定时器定期更新当前用户显示
	/// </summary>
	private void StartUserDisplayTimer()
	{
		_userDisplayTimer = new System.Threading.Timer(_ =>
		{
			UpdateCurrentUserDisplay();
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
	
	/// <summary>
	/// 更新当前登录用户显示（演示会话）
	/// </summary>
	private void UpdateCurrentUserDisplay()
	{
		CurrentUserDisplay = DemoSession.Display;
	}
	
	/// <summary>
	/// 导航到登录界面
	/// </summary>
	[RelayCommand]
	public void NavigateToLogin()
	{
		// 先选择"人员"顶部导航
		var permissionNav = NavManager.NavInfos.FirstOrDefault(n => n.Header?.ToString() == "人员");
		if (permissionNav != null)
		{
			SelectedNavData = permissionNav;

			// 等待左侧导航更新后，选择"身份登入"
			Task.Delay(100).ContinueWith(_ =>
			{
				Avalonia.Threading.Dispatcher.UIThread.Post(() =>
				{
					if (LeftNavItems != null)
					{
						var loginNav = LeftNavItems.FirstOrDefault(n => n.Header?.ToString() == "身份登入");
						if (loginNav != null)
						{
							SelectedLeftNavData = loginNav;
						}
					}
				});
			});
		}
	}

	#region Basic
	/// <summary>主界面「交互结构体监控」按钮入口：选中顶部「监控」页签并导航到监控页。</summary>
	public void NavigateToMonitor()
	{
		var monitorNav = NavManager.NavInfos.FirstOrDefault(n => n.Header?.ToString() == "监控");
		if (monitorNav != null) SelectedNavData = monitorNav;
	}


	public override partial IThemeInfo? SelectedTheme { get; set; }

	public partial string ThemeToggleLabel { get; set; } = "浅色";

	public void @ToggleGround()
	{
		if (NavManager.ActiveContent is UiView uiView) uiView.ShowBackgroundContent = !uiView.ShowBackgroundContent;
	}

	public void @ChangeColorTheme(IThemeInfo? themeInfo)
	{
		SelectedTheme = themeInfo;
	}

	public void @ToggleTheme()
	{
		var themes = CreateThemes().ToList();
		IThemeInfo? target = (SelectedTheme?.Theme == ThemeVariant.Light)
			? themes.FirstOrDefault(t => t.Theme == ThemeVariant.Dark)
			: themes.FirstOrDefault(t => t.Theme == ThemeVariant.Light);
		if (target is not null) ChangeColorTheme(target);
	}

	public override IEnumerable<IThemeInfo> CreateThemes() => new List<ThemeInfo>()
	{
		new("默认", ThemeVariant.Default),
		new("亮色", ThemeVariant.Light),
		new("暗色", ThemeVariant.Dark),
		new("Aquatic", UiTheme.Aquatic),
		new("Desert", UiTheme.Desert),
		new("Dusk", UiTheme.Dusk),
		new("NightSky", UiTheme.NightSky)
	};

	partial void OnSelectedThemeChanged(IThemeInfo? value)
	{
		if (value is null) return;
		var app = Application.Current;
		if (app is null) return;
		app.RequestedThemeVariant = value.Theme;
		ThemeToggleLabel = value.Theme == ThemeVariant.Light ? "深色" : "浅色";
		Notification.Show(new UiNotification(
			"主题已切换",
			$"主题切换到 {value.Name}",
			UiMessageType.Success));
	}

	#endregion
	
	/// <summary>
	/// 启动时钟定时器，定期更新当前时间显示
	/// </summary>
	private void StartClockTimer()
	{
		_globalStatusTimer = new System.Threading.Timer(_ =>
		{
			GlobalCurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	/// <summary>
	/// 授权到期守卫：每 3 秒检查一次系统日期，超过授权截止日期后弹出离线激活窗；
	/// 弹窗被关闭后继续循环检查并再次弹出（保持显示，直至完成授权续期）。
	/// 弹窗提供激活码输入：ZC0001 续 60 天，ZC0000 永久授权。
	/// </summary>
	private void StartLicenseGuard()
	{
		License.LoadFromConfig();

		_ = Task.Run(async () =>
		{
			while (true)
			{
				await Task.Delay(3000).ConfigureAwait(false);
				if (License.IsLicensed) continue;

				await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
				{
					var mainWindow = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
						? desktop.MainWindow
						: null;
					if (mainWindow == null) return;
						// （ZC0001 续期 60 天 / ZC0000 永久授权）
					var vm = new LicenseDialogVM
					{
						Description =
							$"本软件的授权有效期已截止至 {License.ExpiryDate:yyyy年MM月dd日}。\n\n" +
							"请联系软件供应商获取激活码。\n" +
							"在下方输入激活码并点击「激活」完成续期。\n" +
							"（本提示窗口关闭后将再次弹出，直至授权续期完成）"
					};
					var dialog = new LicenseDialog(vm);
					dialog.ActivateRequested += (_, args) =>
					{
						var (ok, error) = License.ApplyActivation(args.Code, out var newExpiry);
						if (ok)
						{
							License.ExpiryDate = newExpiry;
							License.Save();
							dialog.OnActivationSucceeded();
						}
						else
						{
							dialog.OnActivationFailed(error ?? "激活失败");
						}
					};

					await dialog.ShowDialog(mainWindow);
				});
			}
		});
	}
}
