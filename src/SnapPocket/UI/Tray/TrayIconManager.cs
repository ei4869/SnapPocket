using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly ConfigService _configService;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _annotationItem;
    private readonly ToolStripMenuItem _renameItem;
    private Icon? _generatedIcon;

    public event Action? TogglePanelRequested;
    public event Action? OpenSettingsRequested;
    public event Action? OpenDefaultFolderRequested;
    public event Action? ExitRequested;

    public TrayIconManager(ConfigService configService)
    {
        _configService = configService;
        _generatedIcon = CreateTrayIcon();

        var menu = new ContextMenuStrip();

        var toggleItem = new ToolStripMenuItem("展开 / 收起悬浮面板");
        toggleItem.Click += (_, _) => TogglePanelRequested?.Invoke();
        menu.Items.Add(toggleItem);

        menu.Items.Add(new ToolStripSeparator());

        _annotationItem = new ToolStripMenuItem("截屏后快速标注")
        {
            CheckOnClick = true,
            Checked = _configService.Config.EnableAnnotationAfterCapture
        };
        _annotationItem.Click += (_, _) =>
        {
            _configService.Config.EnableAnnotationAfterCapture = _annotationItem.Checked;
            if (!_configService.Save())
                SyncCheckStates();
        };
        menu.Items.Add(_annotationItem);

        _renameItem = new ToolStripMenuItem("截屏后自定义命名")
        {
            CheckOnClick = true,
            Checked = _configService.Config.EnableCustomRenameAfterCapture
        };
        _renameItem.Click += (_, _) =>
        {
            _configService.Config.EnableCustomRenameAfterCapture = _renameItem.Checked;
            if (!_configService.Save())
                SyncCheckStates();
        };
        menu.Items.Add(_renameItem);

        menu.Items.Add(new ToolStripSeparator());

        var openDirItem = new ToolStripMenuItem("打开默认截图目录");
        openDirItem.Click += (_, _) => OpenDefaultFolderRequested?.Invoke();
        menu.Items.Add(openDirItem);

        var settingsItem = new ToolStripMenuItem("详细设置...");
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出 SnapPocket");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "SnapPocket - 智能目录截屏工具",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePanelRequested?.Invoke();
            }
        };

        _configService.ConfigChanged += SyncCheckStates;
    }

    public void SyncCheckStates()
    {
        _annotationItem.Checked = _configService.Config.EnableAnnotationAfterCapture;
        _renameItem.Checked = _configService.Config.EnableCustomRenameAfterCapture;
    }

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Rounded blue pocket badge
        using var bgPath = CreateRoundedRect(new Rectangle(2, 2, 28, 28), 7);
        using var bgBrush = new LinearGradientBrush(
            new Point(0, 0),
            new Point(32, 32),
            Color.FromArgb(255, 76, 154, 255),
            Color.FromArgb(255, 24, 100, 210));
        g.FillPath(bgBrush, bgPath);

        // White viewfinder corners + lens dot
        using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // Top-left
        g.DrawLine(pen, 8, 12, 8, 8);
        g.DrawLine(pen, 8, 8, 12, 8);
        // Top-right
        g.DrawLine(pen, 20, 8, 24, 8);
        g.DrawLine(pen, 24, 8, 24, 12);
        // Bottom-left
        g.DrawLine(pen, 8, 20, 8, 24);
        g.DrawLine(pen, 8, 24, 12, 24);
        // Bottom-right
        g.DrawLine(pen, 20, 24, 24, 24);
        g.DrawLine(pen, 24, 24, 24, 20);

        using var dotBrush = new SolidBrush(Color.White);
        g.FillEllipse(dotBrush, 13, 13, 6, 6);

        var hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        var cloned = (Icon)temp.Clone();
        DestroyIcon(hIcon);
        return cloned;
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _configService.ConfigChanged -= SyncCheckStates;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _generatedIcon?.Dispose();
        _generatedIcon = null;
    }
}
