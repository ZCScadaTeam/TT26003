using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AppZC.UI.Pages;

[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class HelpPage : UiView
{
    public HelpPage() => InitializeComponent();

    /// <summary>
    /// 挂载时按当前工位模式重载帮助内容（构造时 [Inject] AppConfig 尚未注入，必须挂载后加载）：
    /// 后段工位 → GitHub 版；耐压/All → 操作手册版。每次进入页面都会刷新，工位切换后再次进入即生效。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // DataContext 由框架在挂载流程设置，稍后一帧再取，确保已绑定 VM
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is HelpVM vm)
                vm.ReloadByMode();
        });
    }
}
