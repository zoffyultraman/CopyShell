using CopyShell.Core.Protocol;
using Microsoft.UI.Xaml;

namespace CopyShell.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ShellRequest? request = null;
        string? startupError = null;

        try
        {
            var requestStore = new ShellRequestStore();
            requestStore.DeleteStaleRequests(TimeSpan.FromHours(24));
            var requestPath = FindRequestPath(Environment.GetCommandLineArgs());
            if (requestPath is not null)
            {
                request = requestStore
                    .ReadAndDeleteAsync(requestPath)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                startupError = "请在资源管理器中选择文件或文件夹，然后使用 CopyShell 右键菜单。";
            }
        }
        catch (Exception exception)
        {
            startupError = exception.Message;
        }

        _window = new MainWindow(request, startupError);
        _window.Activate();
    }

    private static string? FindRequestPath(IReadOnlyList<string> arguments)
    {
        for (var index = 1; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--request", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
