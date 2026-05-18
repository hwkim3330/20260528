using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator;

public partial class App : Application
{
    private LabWorkerService? _worker;

    // Backward compat — SendViewModel checks app.LabServer == null before using; always null now
    public Services.LabApiServer? LabServer => null;
    public LabWorkerService? LabWorker => _worker;

    private static string ResolveServerUrl(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--server", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return Environment.GetEnvironmentVariable("PACKET_LAB_SERVER") ?? "ws://127.0.0.1:8080";
    }

    private static string ResolveWorkerId(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--worker-id", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return Environment.GetEnvironmentVariable("PACKET_LAB_WORKER_ID") ?? "local";
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // WebSocket worker 연결 (Node.js 서버에 worker로 등록)
        var serverUrl = ResolveServerUrl(e.Args);
        var workerId  = ResolveWorkerId(e.Args);
        _worker = new LabWorkerService(serverUrl, workerId);
        _worker.Start();

        MainWindow mainWindow;
        try
        {
            mainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"앱 초기화 오류:\n{ex.Message}\n\n{ex.StackTrace}",
                "시작 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = mainWindow;
        mainWindow.Show();

        // WPF ViewModel들을 worker에 연결
        if (_worker != null && mainWindow.DataContext is ViewModels.MainViewModel vm)
        {
            _worker.MainVm          = vm;
            _worker.AutomationVm    = vm.AutomationVM;
            _worker.CaptureVm       = vm.CaptureVM;
            _worker.HyperTerminalVm = vm.HyperTerminalVM;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _worker?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Unhandled error:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            MessageBox.Show($"Fatal error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
