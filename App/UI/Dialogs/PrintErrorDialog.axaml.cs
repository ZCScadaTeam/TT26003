using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AppZC.UI.Dialogs;

public partial class PrintErrorDialog : Window
{
    public PrintErrorDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    public PrintErrorDialog(string title, string message) : this()
    {
        DataContext = new MessageDialogVM(title, message);
        Title = title;
    }
    
    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        Close(true); // 返回 true 表示重新打印
    }
    
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false); // 返回 false 表示取消
    }
    
    /// <summary>
    /// 标题栏按下时开始拖动窗口
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
