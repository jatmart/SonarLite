using System.Runtime.InteropServices;

namespace SonarLite.Services;

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
    [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
    [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
    [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
    [PreserveSig] int SetProcessingPeriod(string pszDeviceName, long hnsProcessingPeriod);
    [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr deviceShareMode);
    [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr deviceShareMode);
    [PreserveSig] int GetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
    [PreserveSig] int SetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
    [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, ERole role);
    [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigClient
{
}

/// <summary>
/// Wraps the undocumented IPolicyConfig COM interface (used by Windows' own audio
/// control panel) to set the default playback/recording device programmatically.
/// </summary>
internal static class PolicyConfig
{
    public static void SetDefaultDevice(string deviceId)
    {
        var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Console);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications);
        }
        finally
        {
            Marshal.ReleaseComObject(policyConfig);
        }
    }
}
