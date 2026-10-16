using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace GratingSensor.Lab.UI;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = BuildAvaloniaApp();
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        app.SetupWithLifetime(lifetime);
        return lifetime.Start(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
