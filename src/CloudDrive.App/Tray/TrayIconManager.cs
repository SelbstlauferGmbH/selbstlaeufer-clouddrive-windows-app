using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncEngine;
using Serilog;

namespace CloudDrive.App.Tray;

public class TrayIconManager : IDisposable
{
    private const string TrayTooltipText = "CloudDrive";
    private const float TrayIconDesignSize = 16f;

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly Bitmap _appIconBitmap;
    private readonly Dictionary<SyncState, Icon> _stateIcons = new();
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;
    private readonly Font _updateItemFont;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _openActivityItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _quitItem;
    private SyncState _currentState = SyncState.Disconnected;
    private long _lastToggleTick;
    private Action? _balloonClickRequested;
    private Action? _installUpdateRequested;

    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action? PauseResumeRequested;
    public event Action? ActivityPanelRequested;

    public TrayIconManager(AppSettings settings)
    {
        _appIcon = LoadAppIcon();
        _appIconBitmap = LoadAppIconBitmap();
        _updateItemFont = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold);
        _updateItem = new ToolStripMenuItem
        {
            Visible = false,
            Font = _updateItemFont
        };
        _updateItem.Click += OnUpdateItemClick;
        _updateSeparator = new ToolStripSeparator
        {
            Visible = false
        };
        _pauseItem = new ToolStripMenuItem();
        _pauseItem.Click += (_, _) => PauseResumeRequested?.Invoke();
        _openActivityItem = new ToolStripMenuItem();
        _openActivityItem.Click += (_, _) => ActivityPanelRequested?.Invoke();
        _settingsItem = new ToolStripMenuItem();
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        _quitItem = new ToolStripMenuItem();
        _quitItem.Click += (_, _) => QuitRequested?.Invoke();

        _notifyIcon = new NotifyIcon
        {
            Text = TrayTooltipText,
            Visible = true,
            Icon = GetIconForState(SyncState.Disconnected),
            ContextMenuStrip = CreateContextMenu()
        };

        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.DoubleClick += (_, _) => RaiseToggleIfNeeded();
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        AppLocalizer.Instance.CultureChanged += OnCultureChanged;

        UpdateState(SyncState.Disconnected);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        Log.Information("UI[Tray] MouseUp button={Button}", e.Button);
        if (e.Button == MouseButtons.Left)
        {
            RaiseToggleIfNeeded();
        }
    }

    private void RaiseToggleIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now - _lastToggleTick < SystemInformation.DoubleClickTime)
        {
            Log.Information("UI[Tray] Toggle suppressed by double-click debounce");
            return;
        }

        _lastToggleTick = now;
        Log.Information("UI[Tray] Raising activity flyout request");
        ActivityPanelRequested?.Invoke();
    }

    public void UpdateState(SyncState state)
    {
        _currentState = state;
        UpdateMenuLabels();
        _notifyIcon.Text = GetTooltipForState(state);
        _notifyIcon.Icon = GetIconForState(state);
        Log.Debug("UI[Tray] State updated to {State}", state);
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, Action? onClicked = null)
    {
        _balloonClickRequested = onClicked;
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    public void ShowUpdateAvailable(Action onInstallRequested)
    {
        _installUpdateRequested = onInstallRequested;
        _updateItem.Visible = true;
        _updateItem.Enabled = true;
        _updateSeparator.Visible = true;
        UpdateMenuLabels();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        UpdateMenuLabels();
        menu.Items.Add(_updateItem);
        menu.Items.Add(_updateSeparator);
        menu.Items.Add(_openActivityItem);
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_quitItem);

        return menu;
    }

    private string GetTooltipForState(SyncState state)
    {
        return TrayTooltipText;
    }

    private void UpdateMenuLabels()
    {
        var localizer = AppLocalizer.Instance;
        _updateItem.Text = localizer.GetString("Tray_Menu_UpdateAvailable");
        _openActivityItem.Text = localizer.GetString("Tray_Menu_OpenActivityStream");
        _settingsItem.Text = localizer.GetString("Tray_Menu_OpenSettings");
        _pauseItem.Text = _currentState == SyncState.Paused
            ? localizer.GetString("Tray_Menu_ResumeSync")
            : localizer.GetString("Tray_Menu_PauseSync");
        _quitItem.Text = localizer.GetString("Tray_Menu_Quit");
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        UpdateState(_currentState);
    }

    private void OnUpdateItemClick(object? sender, EventArgs e)
    {
        var callback = _installUpdateRequested;
        if (callback == null)
            return;

        _updateItem.Enabled = false;
        callback();
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        var callback = _balloonClickRequested;
        _balloonClickRequested = null;

        if (callback == null)
            return;

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UI[Tray] Balloon click action failed");
        }
    }

    private Icon GetIconForState(SyncState state)
    {
        if (_stateIcons.TryGetValue(state, out var icon))
            return icon;

        var created = CreateBadgedIcon(state);
        _stateIcons[state] = created;
        return created;
    }

    private Icon CreateBadgedIcon(SyncState state)
    {
        var iconSize = GetTrayIconSize();
        using var canvas = new Bitmap(iconSize, iconSize);
        using var graphics = Graphics.FromImage(canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(_appIconBitmap, new Rectangle(0, 0, iconSize, iconSize));

        var badgeScale = iconSize / TrayIconDesignSize;
        graphics.ScaleTransform(badgeScale, badgeScale);

        switch (state)
        {
            case SyncState.Syncing:
                DrawCircularBadge(graphics, Color.FromArgb(37, 99, 235), drawRefresh: true);
                break;
            case SyncState.Error:
                DrawTriangleBadge(graphics, Color.FromArgb(245, 158, 11));
                break;
            case SyncState.Paused:
                DrawCircularBadge(graphics, Color.FromArgb(107, 114, 128), drawPause: true);
                break;
            case SyncState.Disconnected:
                DrawCircularBadge(graphics, Color.FromArgb(220, 38, 38), drawExclamation: true);
                break;
        }

        var handle = canvas.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawCircularBadge(Graphics graphics, Color color, bool drawPause = false, bool drawExclamation = false, bool drawRefresh = false)
    {
        using var fillBrush = new SolidBrush(color);
        using var outlinePen = new Pen(Color.White, 0.6f);
        var rect = new RectangleF(8.2f, 8.2f, 6.6f, 6.6f);
        graphics.FillEllipse(fillBrush, rect);
        graphics.DrawEllipse(outlinePen, rect);

        if (drawPause)
        {
            using var whiteBrush = new SolidBrush(Color.White);
            graphics.FillRectangle(whiteBrush, 10.1f, 9.8f, 1.1f, 3.3f);
            graphics.FillRectangle(whiteBrush, 12.0f, 9.8f, 1.1f, 3.3f);
        }
        else if (drawExclamation)
        {
            using var whitePen = new Pen(Color.White, 1.2f);
            graphics.DrawLine(whitePen, 11.5f, 9.8f, 11.5f, 12.0f);
            graphics.DrawEllipse(whitePen, 11.0f, 12.8f, 0.6f, 0.6f);
        }
        else if (drawRefresh)
        {
            using var whitePen = new Pen(Color.White, 0.9f);
            graphics.DrawArc(whitePen, 9.5f, 9.5f, 3.8f, 3.8f, 205, 200);
            graphics.DrawLine(whitePen, 9.7f, 10.8f, 9.4f, 9.7f);
            graphics.DrawLine(whitePen, 9.7f, 10.8f, 10.7f, 10.5f);
            graphics.DrawArc(whitePen, 10.0f, 10.0f, 3.8f, 3.8f, 20, 190);
            graphics.DrawLine(whitePen, 13.5f, 12.0f, 12.5f, 12.2f);
            graphics.DrawLine(whitePen, 13.5f, 12.0f, 13.2f, 13.0f);
        }
    }

    private static void DrawTriangleBadge(Graphics graphics, Color color)
    {
        var points = new[]
        {
            new PointF(11.5f, 8.6f),
            new PointF(14.7f, 14.2f),
            new PointF(8.3f, 14.2f)
        };

        using var fillBrush = new SolidBrush(color);
        using var outlinePen = new Pen(Color.White, 0.6f);
        graphics.FillPolygon(fillBrush, points);
        graphics.DrawPolygon(outlinePen, points);

        using var whitePen = new Pen(Color.White, 1.0f);
        graphics.DrawLine(whitePen, 11.5f, 10.2f, 11.5f, 12.1f);
        graphics.DrawEllipse(whitePen, 11.1f, 12.8f, 0.5f, 0.5f);
    }

    private static Icon LoadAppIcon()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        const string resourceName = "CloudDrive.App.Resources.selbstlaeufer-cloud-app-icon.ico";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
            return new Icon(stream);

        var exeDir = AppContext.BaseDirectory;
        var iconPath = Path.Combine(exeDir, "Resources", "selbstlaeufer-cloud-app-icon.ico");
        if (File.Exists(iconPath))
            return new Icon(iconPath);

        return SystemIcons.Application;
    }

    private static Bitmap LoadAppIconBitmap()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        const string resourceName = "CloudDrive.App.Resources.selbstlaeufer-cloud-app-icon-1024px.png";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }

        var exeDir = AppContext.BaseDirectory;
        var pngPath = Path.Combine(exeDir, "Resources", "selbstlaeufer-cloud-app-icon-1024px.png");
        if (File.Exists(pngPath))
            return new Bitmap(pngPath);

        using var fallback = LoadAppIcon();
        return new Bitmap(fallback.ToBitmap());
    }

    private static int GetTrayIconSize()
    {
        var size = SystemInformation.SmallIconSize;
        return Math.Max(16, Math.Max(size.Width, size.Height));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    public void Dispose()
    {
        AppLocalizer.Instance.CultureChanged -= OnCultureChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _appIconBitmap.Dispose();
        _updateItemFont.Dispose();

        foreach (var icon in _stateIcons.Values.Where(icon => !ReferenceEquals(icon, _appIcon)))
        {
            icon.Dispose();
        }
    }
}
