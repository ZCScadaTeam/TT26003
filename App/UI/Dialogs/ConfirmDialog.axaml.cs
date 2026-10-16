using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AppZC.UI.Dialogs;

public partial class ConfirmDialog : Window
{
    private readonly Button? _okButton;
    private readonly Button? _cancelButton;

    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _okButton = this.FindControl<Button>("OkButton");
        _cancelButton = this.FindControl<Button>("CancelButton");
    }
    
    public ConfirmDialog(string title, string message) : this()
    {
        DataContext = new MessageDialogVM(title, message);
        Title = title;
    }

    public ConfirmDialog(string title, string message, string okButtonText, string cancelButtonText) : this(title, message)
    {
        if (_okButton != null && !string.IsNullOrWhiteSpace(okButtonText))
        {
            _okButton.Content = okButtonText;
        }

        if (_cancelButton != null && !string.IsNullOrWhiteSpace(cancelButtonText))
        {
            _cancelButton.Content = cancelButtonText;
        }
    }
    
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close(true); // 返回 true 表示确认
    }
    
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false); // 返回 false 表示取消
    }

    /// <summary>
    /// 非模态显示确认框（不阻塞主界面操作，也不抢焦点），关闭后回调确认结果（true=确认 false=取消）。
    /// 用于启动后询问用户等场景；正常模态流程仍用 ShowDialog。
    /// </summary>
    public static void ShowNonModal(Window owner, string title, string message,
        string okButtonText, string cancelButtonText, Action<bool> onResult)
    {
        var dlg = new ConfirmDialog(title, message, okButtonText, cancelButtonText)
        {
            // 不激活窗口：用户无需先点弹窗即可继续操作主界面（如勾选测试模式）
            ShowActivated = false
        };
        // 静态集合持有强引用，防止被 GC 回收导致 PlatformImpl 释放（Avalonia 非模态窗口常见坑）
        lock (_openDialogs)
        {
            _openDialogs.Add(dlg);
        }
        dlg.Closed += (_, _) =>
        {
            bool ok = dlg.DialogResult is true;
            lock (_openDialogs)
            {
                _openDialogs.Remove(dlg);
            }
            try { onResult?.Invoke(ok); } catch { }
        };
        dlg.Show(owner);
    }

    // 持有所有非模态打开的确认框（防止 GC；窗口关闭后移除）
    private static readonly List<ConfirmDialog> _openDialogs = new();

    /// <summary>非模态模式下的结果（OnOkClick/OnCancelClick 通过 Close(true/false) 设置）。</summary>
    public object? DialogResult { get; private set; }
}
