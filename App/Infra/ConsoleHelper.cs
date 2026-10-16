using System.Runtime.InteropServices;

namespace AppZC.Services;

/// <summary>
/// Windows 控制台在「快速编辑模式」下，鼠标选中文字会暂停控制台，
/// 导致 Console.WriteLine / NLog ColoredConsole 等写入阻塞调用线程。
/// </summary>
internal static class ConsoleHelper
{
	private const int StdInputHandle = -10;

	private const uint EnableExtendedFlags = 0x0080;
	private const uint EnableQuickEditMode = 0x0040;
	private const uint EnableInsertMode = 0x0020;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

	/// <summary>
	/// 禁用快速编辑/插入模式，避免选中控制台文本时阻塞进程。
	/// 须分两步设置扩展标志，否则 QUICK_EDIT 可能关不掉。
	/// </summary>
	public static void DisableQuickEditMode()
	{
		if (!OperatingSystem.IsWindows())
			return;

		try
		{
			var handle = GetStdHandle(StdInputHandle);
			if (handle == IntPtr.Zero || handle == new IntPtr(-1))
				return;

			// 第 1 步：先单独打开 EXTENDED_FLAGS，否则无法修改 QUICK_EDIT
			if (!SetConsoleMode(handle, EnableExtendedFlags))
				return;
			if (!GetConsoleMode(handle, out var mode))
				return;

			mode |= EnableExtendedFlags;
			mode &= ~EnableQuickEditMode;
			mode &= ~EnableInsertMode;

			// 第 2 步：写入最终模式
			SetConsoleMode(handle, mode);
		}
		catch
		{
			// ignored
		}
	}
}
