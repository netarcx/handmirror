using System;
using Microsoft.Win32;

namespace HandMirror;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HandMirror";

    public static bool IsRunningFromStableExe()
    {
        var path = Environment.ProcessPath;
        return !string.IsNullOrEmpty(path)
               && path.EndsWith("HandMirror.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            if (!IsRunningFromStableExe()) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{path}\"");
        }
        catch
        {
            // Registry may be policy-restricted; startup is a nice-to-have, not fatal.
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// If startup is enabled but the stored path is stale (e.g. after an upgrade or
    /// move), rewrite it to the current exe. Does nothing if startup is disabled, so
    /// it won't re-enable something the user turned off.
    /// </summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            if (!IsRunningFromStableExe()) return;
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string existing && !string.IsNullOrWhiteSpace(existing))
            {
                var desired = $"\"{path}\"";
                if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, desired);
            }
        }
        catch
        {
        }
    }
}
