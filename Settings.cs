using Microsoft.Win32;

namespace HandMirror;

/// <summary>Small persisted settings stored under HKCU\Software\HandMirror.</summary>
internal static class Settings
{
    private const string KeyPath = @"Software\HandMirror";
    private const string CameraIdValue = "CameraId";

    /// <summary>The device id of the camera the user selected, or null for default.</summary>
    public static string? CameraId
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(CameraIdValue) as string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key == null) return;
            if (string.IsNullOrEmpty(value))
                key.DeleteValue(CameraIdValue, throwOnMissingValue: false);
            else
                key.SetValue(CameraIdValue, value);
        }
    }
}
