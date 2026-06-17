using System;
using System.Linq;
using System.Threading;

namespace HandMirror;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "HandMirror_SingleInstance_8F8E5B6E_6A3F_4A1D_9C2A_3E5F1B2A0C7D";

    private Mutex? _singleInstance;
    private TrayApp? _tray;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // The installer relaunches us with --updated after a silent auto-update. In
        // that case the previous process may not have fully exited yet, so wait for it
        // to release the single-instance mutex instead of giving up immediately.
        bool afterUpdate = e.Args.Contains("--updated");

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew && !(afterUpdate && WaitForPreviousInstance(_singleInstance)))
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _tray = new TrayApp();
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            // Old instance exits quickly (background teardown); cap the wait anyway.
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            // Previous instance was force-killed (by the installer) without releasing
            // the mutex — ownership has transferred to us, which is what we want.
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_singleInstance != null)
        {
            try { _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        base.OnExit(e);
    }
}
