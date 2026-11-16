using System.Runtime.InteropServices;

namespace Dismusic.Core.Sessions;

/// <summary>
/// Changes the Windows default playback device via <c>IPolicyConfig</c> (CPolicyConfigClient). The interface is
/// undocumented but has been stable since Windows Vista and is what the Sound control panel uses.
/// <c>Dismusic.Diag defaulttest</c> verifies it on the current Windows build.
/// </summary>
public static class DefaultDevice
{
    /// <summary>Makes <paramref name="deviceId"/> the default for all roles (console, multimedia, communications).</summary>
    public static void Set(string deviceId)
    {
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            for (int role = 0; role <= 2; role++)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient;

    /// <summary>Only <see cref="SetDefaultEndpoint"/> is called; the earlier slots just keep the vtable layout.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
