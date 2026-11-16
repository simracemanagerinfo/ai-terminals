using System.Windows;

namespace ClaudeTerminal.Host;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var project = string.Empty;
        for (var i = 0; i < e.Args.Length; ++i)
        {
            if (e.Args[i] == "--project" && i + 1 < e.Args.Length)
            {
                project = e.Args[++i];
            }
        }
        var main = new MainWindow(project);
        MainWindow = main;
        main.Show();
    }
}
