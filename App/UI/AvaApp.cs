using AppZC.Services;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReactiveUI.Avalonia;
using System.Diagnostics;

namespace AppZC.UI;

public class AvaApp : AvaloniaApplication
{
	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		WindowsWebView.SetToDefaultWebView();
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = App.Current.IOC.Get<AppWindow>();
			desktop.MainWindow.Content = App.Current.IOC.Get<ApplicationViewModel>().AppView;
		}
		else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
		{
			singleViewPlatform.MainView = App.Current.IOC.Get<ApplicationViewModel>().AppView;
		}

		base.OnFrameworkInitializationCompleted();
		AvaloniaInitializedCompletionSource.SetResult();
	}

	public static Task StartAsync()
	{
		UiThread = new Thread(AvaloniaMain, 1024 * 1024 * 10) { Name = "AppUI", IsBackground = false };
		UiThread.TrySetApartmentState(ApartmentState.STA);
		UiThread.Start();
		return AvaloniaInitializedCompletionSource.Task;
	}

	private static void AvaloniaMain()
	{
		try
		{
			Build().StartWithClassicDesktopLifetime(Environment.GetCommandLineArgs());
		}
		catch (Exception ex)
		{
			if (Debugger.IsAttached)
				Debugger.Break();
			Console.Error.WriteLine(ex);
		}
		finally
		{
			// 注意：当前线程是 Avalonia UI（STA）线程，绝对不能在此线程上
			// .GetAwaiter().GetResult() 等待 async 任务，否则会死锁导致进程关不掉。
			// 把整个清理流程扔到 ThreadPool 上跑，并加总超时；超时直接强杀自身进程。
			var cleanup = Task.Run(() =>
				{
					try { LogMgr.Instance.Info("AvaApp 退出，开始清理资源（后台线程）"); } catch { }

					// 框架销毁（用 Wait + 超时，避免无限阻塞）
				try
				{
					var destroyTask = App.Current?.Destroy();
					if (destroyTask != null && !destroyTask.Wait(TimeSpan.FromSeconds(2)))
					{
						try { LogMgr.Instance.Warn("App.Destroy 超时，跳过"); } catch { }
					}
				}
				catch { }

				try { LogMgr.Instance.Info("AvaApp 退出清理完成"); } catch { }
			});

			// 整体最多等 6 秒，避免任何卡死把退出流程拖住
			cleanup.Wait(TimeSpan.FromSeconds(6));

			// 强制刷新日志，避免最后几条丢失
			try { NLog.LogManager.Flush(TimeSpan.FromSeconds(2)); } catch { }

			// 兜底：调用 Environment.Exit；若仍有非后台线程把进程拖住，
			// 1 秒后用 Process.Kill 强制结束本进程。
			var killer = new System.Threading.Timer(_ =>
			{
				try { Process.GetCurrentProcess().Kill(); } catch { }
			}, null, 1500, System.Threading.Timeout.Infinite);
			GC.KeepAlive(killer);

			Environment.Exit(0);
		}
	}

	public static AppBuilder Build() =>
		AppBuilder.Configure<AvaApp>().UseSkia().UsePlatformDetect().WithInterFont().LogToTrace().UseReactiveUI();
}