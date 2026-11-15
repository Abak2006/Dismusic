using System.Runtime.InteropServices;

namespace Dismusic.Core.Interop;

/// <summary>Minimal HSTRING / activation-factory helpers from combase.dll.</summary>
internal static class WinRt
{
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    /// <summary>
    /// Makes sure COM/WinRT is initialized on the calling thread. An STA thread (e.g. the WPF UI thread)
    /// returns RPC_E_CHANGED_MODE, which is fine: COM is already usable there.
    /// </summary>
    public static void EnsureInitialized()
    {
        int hr = RoInitialize(1 /* RO_INIT_MULTITHREADED */);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
            Marshal.ThrowExceptionForHR(hr);
    }

    public static IntPtr CreateHString(string value)
    {
        int hr = WindowsCreateString(value, value.Length, out var h);
        Marshal.ThrowExceptionForHR(hr);
        return h;
    }

    public static void DeleteHString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero)
            WindowsDeleteString(hstring);
    }

    public static string? HStringToString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return null;
        var buffer = WindowsGetStringRawBuffer(hstring, out uint length);
        return length == 0 ? string.Empty : Marshal.PtrToStringUni(buffer, (int)length);
    }
}
