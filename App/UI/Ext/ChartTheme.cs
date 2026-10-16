using System;
using Avalonia;
using Avalonia.Styling;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace AppZC.UI.Ext;

/// <summary>
/// 图表主题帮助类。
/// 根据 Avalonia 当前主题（深 / 浅）返回合适的文字、分隔线、提示框背景颜色，
/// 让 LiveCharts 图表在浅色模式下文字为深色（可见），深色模式下为浅色。
/// 所有图表 VM 统一使用本类取色，并在主题切换时调用 Apply 方法刷新。
/// </summary>
public static class ChartTheme
{
	private static readonly SKTypeface YaHei = SKTypeface.FromFamilyName("Microsoft YaHei", SKFontStyle.Normal);

	/// <summary>当前是否为浅色主题</summary>
	public static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

	/// <summary>图表文字颜色（坐标轴标签 / 图例 / 提示文字 / 数据标签）</summary>
	public static SKColor TextColor => IsLight ? new SKColor(0x1F, 0x24, 0x30) : new SKColor(240, 234, 248);

	/// <summary>坐标轴分隔线颜色</summary>
	public static SKColor SeparatorColor => IsLight ? new SKColor(0xC8, 0xC8, 0xD4) : new SKColor(74, 64, 128);

	/// <summary>提示框背景颜色</summary>
	public static SKColor TooltipBgColor => IsLight ? new SKColor(255, 255, 255, 245) : new SKColor(42, 36, 69, 240);

	/// <summary>文字画笔（坐标轴标签 / 图例 / 提示 / 数据标签通用）</summary>
	public static SolidColorPaint TextPaint(float size = 12) => new SolidColorPaint(TextColor)
	{
		SKTypeface = YaHei
	};

	/// <summary>分隔线画笔</summary>
	public static SolidColorPaint SeparatorPaint() => new SolidColorPaint(SeparatorColor);

	/// <summary>提示框背景画笔</summary>
	public static SolidColorPaint TooltipBgPaint() => new SolidColorPaint(TooltipBgColor);

	/// <summary>
	/// 注册主题切换回调。使用弱引用持有 VM，VM 被回收后回调自动失效，避免内存泄漏。
	/// 返回 IDisposable 以便主动注销。
	/// </summary>
	public static IDisposable WhenThemeChanges(UiViewModel vm, Action onChange)
	{
		var weak = new WeakReference<UiViewModel>(vm);
		EventHandler handler = (_, _) =>
		{
			if (weak.TryGetTarget(out _)) onChange();
		};
		if (Application.Current is { } app) app.ActualThemeVariantChanged += handler;
		return new ChartThemeDisposable(() =>
		{
			if (Application.Current is { } app) app.ActualThemeVariantChanged -= handler;
		});
	}

	private sealed class ChartThemeDisposable : IDisposable
	{
		private readonly Action _dispose;
		private bool _done;
		public ChartThemeDisposable(Action dispose) => _dispose = dispose;
		public void Dispose()
		{
			if (_done) return;
			_done = true;
			_dispose();
		}
	}
}
