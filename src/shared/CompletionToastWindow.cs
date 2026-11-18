using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace ClaudeTerminal.Host;

/// <summary>
/// In-app completion toast: Windows does not have to register an AppUserModelID
/// or permit legacy tray balloons. Showing it never activates the host.
/// This is NOT a persistent Windows Action Center notification.
/// </summary>
public sealed class CompletionToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly Action _openConversation;

    public CompletionToastWindow(Window owner, string provider, string title, Action openConversation)
    {
        _openConversation = openConversation;
        Width = 354;
        Height = 104;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var accent = provider == "Codex"
            ? Color.FromRgb(102, 238, 231)
            : Color.FromRgb(230, 154, 112);
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 33, 39)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(15, 12, 15, 12)
        };
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 15,
            ShadowDepth = 4,
            Opacity = 0.35
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = provider + " · " + UiText.T("toastComplete"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(accent)
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 38,
            Margin = new Thickness(0, 5, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = UiText.T("toastOpen"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(162, 187, 196)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        border.Child = content;
        Content = border;
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) =>
        {
            Close();
            _openConversation();
        };

        // Position on the monitor that contains the terminal, in WPF DIPs.
        var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle);
        var source = PresentationSource.FromVisual(owner);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var bottomRight = transform.Transform(new Point(monitor.WorkingArea.Right, monitor.WorkingArea.Bottom));
        Left = bottomRight.X - Width - 18;
        Top = bottomRight.Y - Height - 18;

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _dismissTimer.Tick += (_, _) => Close();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    public void ShowForCompletion()
    {
        Show();
        _dismissTimer.Start();
    }
}
