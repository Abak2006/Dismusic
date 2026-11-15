using System.Runtime.InteropServices;
using Dismusic.Core.Interop;

namespace Dismusic.Core.Routing;

/// <summary>Thrown when per-app routing is not available on this Windows build.</summary>
public sealed class AudioRoutingUnavailableException(string message) : Exception(message);

/// <summary>
/// Wrapper over the undocumented <c>Windows.Media.Internal.AudioPolicyConfig</c> activation factory,
/// the same mechanism behind Settings &gt; Sound &gt; Volume mixer &gt; per-app output device.
/// <para>
/// It is not a public API. The interface IID changed in Windows 10 build 21390, so both known IIDs are tried.
/// The vtable layout (IUnknown + IInspectable + 19 unused slots, then Set/Get/Clear) is the one used by
/// several open-source tools and must be re-verified if Microsoft changes it; <c>Dismusic.Diag policytest</c> does that.
/// </para>
/// Instances are bound to the thread that created them; call them from that thread only.
/// </summary>
public sealed unsafe class AudioPolicyConfig : IDisposable
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    private static readonly Guid[] CandidateIids =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"), // Windows 10 21H2+ / Windows 11
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // Windows 10 1803 - 21H1
    ];

    private const int SetPersistedDefaultAudioEndpointSlot = 25;
    private const int GetPersistedDefaultAudioEndpointSlot = 26;
    private const int ClearAllPersistedApplicationDefaultEndpointsSlot = 27;

    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int ERoleMultimedia = 1;

    internal const string MmDevApiToken = @"\\?\SWD#MMDEVAPI#";
    internal const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private IntPtr _factory;

    private AudioPolicyConfig(IntPtr factory) => _factory = factory;

    public static AudioPolicyConfig Create()
    {
        WinRt.EnsureInitialized();
        var className = WinRt.CreateHString(ClassName);
        try
        {
            int lastHr = 0;
            foreach (var candidate in CandidateIids)
            {
                var iid = candidate;
                int hr = WinRt.RoGetActivationFactory(className, ref iid, out var factory);
                if (hr >= 0 && factory != IntPtr.Zero)
                    return new AudioPolicyConfig(factory);
                lastHr = hr;
            }
            throw new AudioRoutingUnavailableException(
                $"Windows did not provide the per-app audio routing interface (0x{lastHr:X8}). " +
                "Per-app and screen-based routing are disabled on this Windows build.");
        }
        finally
        {
            WinRt.DeleteHString(className);
        }
    }

    /// <summary>Routes a process's audio to <paramref name="deviceId"/> (an MMDevice id), or back to the Windows default when null.</summary>
    public void SetAppEndpoint(uint processId, string? deviceId)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)Slot(SetPersistedDefaultAudioEndpointSlot);
        IntPtr h = deviceId is null ? IntPtr.Zero : WinRt.CreateHString(ToInterfacePath(deviceId));
        try
        {
            foreach (var role in new[] { ERoleConsole, ERoleMultimedia })
            {
                int hr = fn(_factory, processId, EDataFlowRender, role, h);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            WinRt.DeleteHString(h);
        }
    }

    /// <summary>Returns the MMDevice id the process is pinned to, or null if it follows the Windows default.</summary>
    public string? GetAppEndpoint(uint processId)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)Slot(GetPersistedDefaultAudioEndpointSlot);
        IntPtr h = IntPtr.Zero;
        int hr = fn(_factory, processId, EDataFlowRender, ERoleMultimedia, &h);
        try
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return FromInterfacePath(WinRt.HStringToString(h));
        }
        finally
        {
            WinRt.DeleteHString(h);
        }
    }

    /// <summary>Removes every per-app output assignment on the system (same as Settings' "Reset").</summary>
    public void ClearAll()
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(ClearAllPersistedApplicationDefaultEndpointsSlot);
        int hr = fn(_factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static string ToInterfacePath(string deviceId) => MmDevApiToken + deviceId + RenderInterfaceSuffix;

    public static string? FromInterfacePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var s = path.StartsWith(MmDevApiToken, StringComparison.OrdinalIgnoreCase) ? path[MmDevApiToken.Length..] : path;
        int hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash];
        return s.Length == 0 ? null : s;
    }

    private IntPtr Slot(int index)
    {
        ObjectDisposedException.ThrowIf(_factory == IntPtr.Zero, this);
        var vtable = *(IntPtr**)_factory;
        return vtable[index];
    }

    public void Dispose()
    {
        if (_factory != IntPtr.Zero)
        {
            Marshal.Release(_factory);
            _factory = IntPtr.Zero;
        }
    }
}
