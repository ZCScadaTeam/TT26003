using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ZC.UI.Extensions;

namespace AppZC.UI.Layouts;

[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class TopAppBarLayout : ApplicationLayoutView
{
	private readonly TranslateTransform _indicatorTransform = new();
	private double _animationStartX;
	private double _targetX;
	private DateTime _animationStart;
	private readonly DispatcherTimer _animationTimer;
	private const double AnimationDurationMs = 300;

	public TopAppBarLayout()
	{
		InitializeComponent();

		var indicator = this.FindControl<Border>("TabIndicator");
		if (indicator != null)
		{
			indicator.RenderTransform = _indicatorTransform;
		}

		_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
		_animationTimer.Tick += OnAnimationTick;

		this.AttachedToVisualTree += (_, _) =>
		{
			var tabStrip = this.FindControl<Control>("TabStrip1");
			if (tabStrip != null)
			{
				tabStrip.PropertyChanged += OnTabStripPropertyChanged;
				Dispatcher.UIThread.Post(() => UpdateIndicatorPosition(), DispatcherPriority.Loaded);
			}
		};

		AppBarPanel.SizeChanged += (o, e) =>
		{
			var appVM = DataContext as ApplicationViewModel;
			if (appVM == null) return;
			appVM.AppBarSettings.Height = e.NewSize.Height;

			if (appVM.AppView.NotificationManager is Control notificationManager)
			{
				var margin = notificationManager.Margin;
				notificationManager.Margin =
					new Thickness(margin.Left, appVM.AppBarSettings.Height, margin.Right, margin.Bottom);
			}

			if (appVM.AppView.ToastManager is Control toastManager)
			{
				var margin = toastManager.Margin;
				toastManager.Margin = new Thickness(margin.Left, appVM.AppBarSettings.Height, margin.Right, margin.Bottom);
			}
		};
	}

	private void OnTabStripPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property.Name != "SelectedItem") return;
		UpdateIndicatorPosition();
	}

	private void UpdateIndicatorPosition()
	{
		var tabStrip = this.FindControl<Control>("TabStrip1");
		if (tabStrip == null) return;

		var indicator = this.FindControl<Border>("TabIndicator");
		if (indicator == null) return;

		var selectedItem = tabStrip.GetType().GetProperty("SelectedItem")?.GetValue(tabStrip);
		if (selectedItem == null) return;

		var containerFromItemMethod = tabStrip.GetType().GetMethod("ContainerFromItem");
		var container = containerFromItemMethod?.Invoke(tabStrip, new[] { selectedItem }) as Control;
		if (container == null) return;

		var targetX = container.Bounds.X;
		var targetWidth = container.Bounds.Width;

		indicator.Width = targetWidth;
		AnimateIndicator(targetX);
	}

	private void AnimateIndicator(double targetX)
	{
		_animationStartX = _indicatorTransform.X;
		_targetX = targetX;
		_animationStart = DateTime.UtcNow;

		if (!_animationTimer.IsEnabled)
		{
			_animationTimer.Start();
		}
	}

	private void OnAnimationTick(object? sender, EventArgs e)
	{
		var elapsed = (DateTime.UtcNow - _animationStart).TotalMilliseconds;
		var progress = Math.Min(elapsed / AnimationDurationMs, 1.0);

		var eased = 1.0 - Math.Pow(1.0 - progress, 3);
		_indicatorTransform.X = _animationStartX + (_targetX - _animationStartX) * eased;

		if (progress >= 1.0)
		{
			_animationTimer.Stop();
		}
	}

	public object? MainContent { get; protected set; }

	public override void SetMainContent(object? content)
	{
		var control = content as Control;
		if (control == null) return;
		if (control.Parent == this)
			return;
		control.RemoveFromParent();
		Grid.SetRow(control, 2);
		Grid.SetColumn(control, 1);
		(MainContent as Control)?.RemoveFromParent();
		MainContent = control;
		Children.Add(control);
	}
}
