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
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static void Enable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return;
        if (!IsRunningFromStableExe()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Could not open Run key");
        key.SetValue(ValueName, $"\"{path}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
