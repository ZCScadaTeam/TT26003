using ZC.Mvvm;

namespace AppZC.UI.Dialogs;

[ObservableObject(IncludeAllPartialProperty = true)]
public partial class MessageDialogVM
{
    public partial string Title { get; set; }
    public partial string Message { get; set; }
    
    public MessageDialogVM()
    {
        Title = "";
        Message = "";
    }
    
    public MessageDialogVM(string title, string message)
    {
        Title = title;
        Message = message;
    }
}
