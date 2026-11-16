using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Dismusic.App;

/// <summary>Registers Dismusic under HKCU\...\Run so it starts (minimized to tray) at sign-in.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dismusic";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// Turns autostart on the first time Dismusic runs (it's needed for enhancements and routing to apply after
    /// Windows starts), and keeps an existing entry pointing at the current exe if Dismusic was moved/updated.
    /// </summary>
    public static void Ensure(Dismusic.Core.Settings.AppSettings settings)
    {
        if (!settings.AutostartConfigured)
        {
            SetEnabled(true);
            settings.AutostartConfigured = true;
            Dismusic.Core.Log.Info("Start with Windows turned on (first run)");
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Draws the app icon at runtime (three output bars), so no binary asset is needed.</summary>
internal static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var background = new SolidBrush(Color.FromArgb(0x1F, 0x5F, 0xA8));
            using var path = RoundedRect(new RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s);
            g.FillPath(background, path);
            using var bar = new SolidBrush(Color.White);
            g.FillRectangle(bar, 8 * s, 14 * s, 4 * s, 10 * s);
            g.FillRectangle(bar, 14 * s, 8 * s, 4 * s, 16 * s);
            g.FillRectangle(bar, 20 * s, 11 * s, 4 * s, 13 * s);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Notification-area icon with quick toggles.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(ViewModels.MainViewModel vm, Action open, Action toggleFlyout, Action exit)
    {
        _image = AppIcon.Create();
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Dismusic", null, (_, _) => open());
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        var follow = new WinForms.ToolStripMenuItem("Screen follow", null, (_, _) => vm.ScreenFollowEnabled = !vm.ScreenFollowEnabled);
        var mirror = new WinForms.ToolStripMenuItem("Multi-output", null, (_, _) => vm.ToggleMirrorCommand.Execute(null));
        var outputs = new WinForms.ToolStripMenuItem("Output device");
        menu.Items.AddRange([openItem, new WinForms.ToolStripSeparator(), outputs, follow, mirror, new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Exit", null, (_, _) => exit())]);
        menu.Opening += (_, _) =>
        {
            vm.RefreshNow();
            outputs.DropDownItems.Clear();
            foreach (var device in vm.OutputDevices)
            {
                var d = device;
                outputs.DropDownItems.Add(new WinForms.ToolStripMenuItem(d.Name, null, (_, _) => vm.MakeDefaultOutput(d)) { Checked = d.IsDefault });
            }
            outputs.Enabled = outputs.DropDownItems.Count > 0;
            follow.Checked = vm.ScreenFollowEnabled;
            mirror.Checked = vm.MirrorRunning;
            mirror.Enabled = !vm.MirrorBusy;
        };

        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = "Dismusic: click for volume and output, right-click for more",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) toggleFlyout();
        };
    }

    public Icon Image => _image;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }
}
