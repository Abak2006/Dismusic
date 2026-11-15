using Dismusic.Core.Interop;

namespace Dismusic.Core.Screens;

public readonly record struct AppWindow(IntPtr Handle, uint ProcessId);

public static class WindowLocator
{
    /// <summary>
    /// Visible, non-minimized, non-cloaked top-level app windows in Z-order (topmost first).
    /// Tool windows and owned popups are skipped.
    /// </summary>
    public static List<AppWindow> GetAppWindowsInZOrder()
    {
        var list = new List<AppWindow>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
            if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return true;
            if ((Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!Win32.GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1) return true;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) list.Add(new AppWindow(hwnd, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The topmost window belonging to any of <paramref name="processIds"/>, or null.</summary>
    public static AppWindow? FindTopmost(IReadOnlyList<AppWindow> windowsInZOrder, IReadOnlyCollection<uint> processIds)
    {
        foreach (var w in windowsInZOrder)
            if (processIds.Contains(w.ProcessId)) return w;
        return null;
    }

    public static IntPtr MonitorOf(IntPtr hwnd) => Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
}

/// <summary>
/// Raises <see cref="WindowsChanged"/> when any top-level window is moved, resized, restored or focused.
/// Must be created on a thread that pumps messages (e.g. the WPF UI thread); events arrive on that thread.
/// The event fires very often while dragging; callers should debounce.
/// </summary>
public sealed class WindowEventWatcher : IDisposable
{
    private readonly Win32.WinEventProc _callback; // kept alive: the OS holds only a raw pointer
    private readonly List<IntPtr> _hooks = new();

    public event Action? WindowsChanged;

    public WindowEventWatcher()
    {
        _callback = OnEvent;
        foreach (var evt in new[] { Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MOVESIZEEND,
                     Win32.EVENT_SYSTEM_MINIMIZEEND, Win32.EVENT_OBJECT_LOCATIONCHANGE })
        {
            var hook = Win32.SetWinEventHook(evt, evt, IntPtr.Zero, _callback, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            if (hook == IntPtr.Zero) Log.Warn($"SetWinEventHook failed for event 0x{evt:X}");
            else _hooks.Add(hook);
        }
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        if (eventType == Win32.EVENT_OBJECT_LOCATIONCHANGE && (idObject != Win32.OBJID_WINDOW || idChild != 0)) return;
        if (Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;
        WindowsChanged?.Invoke();
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
