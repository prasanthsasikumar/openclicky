// Ports MenuBarPanelManager.swift: the tray icon (the Mac's NSStatusItem) and its plain menu, for
// either click: what OpenClicky is doing and the keys to remember, agent mode, docking the pointer,
// the window, the island, settings, the welcome tour, and quit. Each item is raised as an event the
// composition root wires (Wired in AppComposition.WireTray: CompanionManager.setAgentModeEnabled, setCursorDocked,
// showDictationWindow(section: .words), notchHUDManager.openPanel, showSettings, firstRunTour.replay,
// quitWithFarewell). The icon is Assets\OpenClicky.ico when the app ships one, else the mark drawn here.

using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Windows.Tray;

/// <summary>What the menu shows, read each time it opens.</summary>
public sealed record TrayMenuState(
    string StatusText,
    bool IsAgentModeEnabled,
    bool IsCursorDocked,
    bool IslandIsSideDrawer,
    string DictationKeyLabel)
{
    /// <summary>"Ready", or what OpenClicky is busy with (MenuBarPanelManager.statusText).</summary>
    public static string StatusTextFor(string voiceState, bool agentIsWorking) => voiceState switch
    {
        "listening" => "Listening",
        "processing" => "Thinking",
        "responding" => "Speaking",
        _ => agentIsWorking ? "Agent working" : "Ready",
    };
}

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Func<TrayMenuState> readState;
    private readonly Icon icon;

    public event Action? AgentModeToggled;
    public event Action? DockToggled;
    public event Action? OpenWindowRequested;
    public event Action? OpenIslandRequested;
    public event Action? SettingsRequested;
    public event Action? ReplayWelcomeRequested;
    public event Action? QuitRequested;

    public TrayIcon(Func<TrayMenuState> readState)
    {
        this.readState = readState;
        icon = LoadIcon();
        notifyIcon = new NotifyIcon { Icon = icon, Text = "OpenClicky", Visible = false };
        notifyIcon.ContextMenuStrip = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        notifyIcon.ContextMenuStrip.Opening += (_, _) => BuildMenu(notifyIcon.ContextMenuStrip);
        // Either click opens the menu, as on the Mac.
        notifyIcon.MouseUp += (_, arguments) =>
        {
            if (arguments.Button != MouseButtons.Left) return;
            ShowContextMenuAtCursor();
        };
    }

    /// <summary>Shown or hidden (Settings › General › Show in the tray; always shown while the island is hidden).</summary>
    public bool IsVisible
    {
        get => notifyIcon.Visible;
        set => notifyIcon.Visible = value;
    }

    private void ShowContextMenuAtCursor()
    {
        // NotifyIcon opens its menu only on a right click; the private ShowContextMenu does the
        // focus dance that makes the menu close when the person clicks elsewhere.
        var method = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method is not null) method.Invoke(notifyIcon, null);
        else notifyIcon.ContextMenuStrip?.Show(Cursor.Position);
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        var state = readState();
        menu.Items.Clear();
        menu.Items.Add(new ToolStripLabel($"● OpenClicky — {state.StatusText}")
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
            ForeColor = state.StatusText == "Ready" ? Color.FromArgb(0x2E, 0x9E, 0x4F) : Color.FromArgb(0xD9, 0x7A, 0x00),
        });
        menu.Items.Add(new ToolStripLabel($"Hold Ctrl + Win to talk · hold {state.DictationKeyLabel} to dictate") { ForeColor = SystemColors.GrayText });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Agent Mode", () => AgentModeToggled?.Invoke(), state.IsAgentModeEnabled));
        menu.Items.Add(Item(state.IslandIsSideDrawer ? "Dock Pointer in the Drawer" : "Dock Pointer in the Island", () => DockToggled?.Invoke(), state.IsCursorDocked));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Open OpenClicky", () => OpenWindowRequested?.Invoke()));
        menu.Items.Add(Item("Open the Island", () => OpenIslandRequested?.Invoke()));
        menu.Items.Add(Item("Settings…", () => SettingsRequested?.Invoke(), shortcut: "Ctrl+,"));
        menu.Items.Add(Item("Replay Welcome…", () => ReplayWelcomeRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quit OpenClicky", () => QuitRequested?.Invoke()));
    }

    private static ToolStripMenuItem Item(string title, Action onClick, bool? isOn = null, string? shortcut = null)
    {
        var item = new ToolStripMenuItem(title) { ShortcutKeyDisplayString = shortcut };
        if (isOn is { } checkedState) item.Checked = checkedState;
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Icon LoadIcon()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "OpenClicky.ico");
        try
        {
            if (File.Exists(shipped)) return new Icon(shipped, SystemInformation.SmallIconSize);
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            AppLog.Write($"tray: couldn't load {shipped}: {error.Message}");
        }
        return GeneratedMarkIcon();
    }

    /// <summary>The mark (a ring with a square top-right corner and the pointer inside), white on the orange accent, at the tray's size.</summary>
    public static Icon GeneratedMarkIcon()
    {
        var side = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bitmap = new Bitmap(side, side);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(0xD2, 0x4E, 0x2A));
            using (var rounded = RoundedRectangle(new RectangleF(0, 0, side, side), side * 0.22f)) graphics.FillPath(background, rounded);
            using var mark = MarkPath(side * 0.2f, side * 0.2f, side * 0.6f);
            graphics.FillPath(Brushes.White, mark);
        }
        var handle = bitmap.GetHicon();
        // Icon.FromHandle does not own the handle; clone it so the icon outlives the bitmap's handle.
        using var borrowed = Icon.FromHandle(handle);
        var owned = (Icon)borrowed.Clone();
        DestroyIcon(handle);
        return owned;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>OpenClickyMark's proportions (OpenClickyMark.swift), filled even-odd.</summary>
    private static GraphicsPath MarkPath(float left, float top, float side)
    {
        PointF Scaled(float x, float y) => new(left + x * side, top + y * side);
        var path = new GraphicsPath(FillMode.Alternate);
        var center = new PointF(left + side / 2, top + side / 2);
        var radius = side / 2;
        // Three quarters of the circle, clockwise from the right to the top, then the square corner.
        path.StartFigure();
        path.AddArc(center.X - radius, center.Y - radius, side, side, 0, 270);
        path.AddLine(center.X, top, left + side, top);
        path.CloseFigure();
        var inner = 0.4125f * side;
        path.AddEllipse(center.X - inner, center.Y - inner, inner * 2, inner * 2);
        path.AddPolygon([Scaled(0.3125f, 0.3125f), Scaled(0.7475f, 0.475f), Scaled(0.505f, 0.52f), Scaled(0.4775f, 0.7525f)]);
        return path;
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon.Dispose();
    }
}
