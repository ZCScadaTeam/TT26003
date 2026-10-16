using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AppZC.UI.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    public MessageDialog(string title, string message) : this()
    {
        DataContext = new MessageDialogVM(title, message);
        Title = title; // 设置窗口标题
    }
    
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close();
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

