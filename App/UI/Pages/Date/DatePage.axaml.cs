using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using System;
using System.Globalization;

namespace AppZC.UI.Pages;

/// <summary>
/// 高度转换器：将百分比（0-1）转换为像素高度（最大300）
/// </summary>
public class HeightConverter : IValueConverter
{
	public static readonly HeightConverter Instance = new();
	
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is double percent)
		{
			// 最大高度300，留10像素底部间距
			return Math.Max(percent * 290, 0);
		}
		return 0;
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}

[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class DatePage : UiView
{
	private ScrollViewer? _chartScrollViewer;

    public DatePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
		// 查找ScrollViewer
		_chartScrollViewer = this.FindControl<ScrollViewer>("ChartScrollViewer");
		
		// 为ScrollViewer添加鼠标滚轮事件处理，实现横向滚动
		if (_chartScrollViewer != null)
		{
			_chartScrollViewer.PointerWheelChanged += OnChartScrollViewerWheelChanged;
		}
		
        // 页面加载完成后自动查询一次数据
        if (DataContext is DateVM viewModel)
        {
            // 延迟一点时间，确保所有绑定都已完成，然后在UI线程执行（fire-and-forget模式）
#pragma warning disable CS4014 // 由于此调用不会等待，因此在此调用完成之前将会继续执行当前方法
            Task.Run(async () =>
            {
                await Task.Delay(300);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    viewModel.Query();
                }, Avalonia.Threading.DispatcherPriority.Normal);
            });
#pragma warning restore CS4014
        }
        Loaded -= OnPageLoaded; // 只执行一次
    }

	/// <summary>
	/// 处理鼠标滚轮事件，将垂直滚动转换为横向滚动
	/// </summary>
	private void OnChartScrollViewerWheelChanged(object? sender, PointerWheelEventArgs e)
	{
		if (_chartScrollViewer != null && e.Delta.Y != 0)
		{
			// 将垂直滚轮滚动转换为横向滚动
			var currentOffset = _chartScrollViewer.Offset.X;
			var horizontalDelta = e.Delta.Y * 50; // 滚动速度，可以调整
			var newOffset = Math.Max(0, currentOffset - horizontalDelta);
			var maxOffset = Math.Max(0, _chartScrollViewer.Extent.Width - _chartScrollViewer.Viewport.Width);
			var clampedOffset = Math.Min(newOffset, maxOffset);
			_chartScrollViewer.Offset = new Vector(clampedOffset, _chartScrollViewer.Offset.Y);
			e.Handled = true; // 标记事件已处理，避免默认的垂直滚动
		}
	}
}