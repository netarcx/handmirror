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

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _tray = new TrayApp();
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
