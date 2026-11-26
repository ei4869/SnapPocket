using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Capture;

public partial class ToastNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private string? _targetDirectory;
    private string? _targetFile;

    public ToastNotificationWindow()
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
        _dismissTimer.Tick += (_, _) => FadeOutAndHide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void ShowToast(string title, string detail, string? warning = null, string? directory = null, string? filePath = null, bool isError = false)
    {
        _dismissTimer.Stop();
        _targetDirectory = directory;
        _targetFile = filePath;

        TitleText.Text = title;
        DetailText.Text = detail;

        if (!string.IsNullOrWhiteSpace(warning))
        {
            SubHintText.Text = warning;
            SubHintText.Visibility = Visibility.Visible;
        }
        else
        {
            SubHintText.Visibility = Visibility.Collapsed;
        }

        var brushKey = isError ? "DangerBrush" : (!string.IsNullOrWhiteSpace(warning) ? "WarningBrush" : "SuccessBrush");
        if (TryFindResource(brushKey) is Brush b)
        {
            AccentDot.Background = b;
        }

        Opacity = 0;
        if (!IsVisible) Show();

        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _dismissTimer.Start();
    }

    private void FadeOutAndHide()
    {
        _dismissTimer.Stop();
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) =>
        {
            if (Opacity <= 0.05) Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _dismissTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _dismissTimer.Start();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_targetDirectory))
        {
            StorageService.OpenFolderInExplorer(_targetDirectory, _targetFile);
        }
        Hide();
    }
}
