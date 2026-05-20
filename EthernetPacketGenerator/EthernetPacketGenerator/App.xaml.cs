using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator;

public partial class App : Application
{
    public LabApiServer LabServer { get; } = new(8080);

    private static void Log(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "EPG_startup.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup begin");
        base.OnStartup(e);

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        Log("handlers registered");

        // 방화벽 규칙 확인은 백그라운드에서 — UI 블로킹 방지
        Task.Run(() => EnsureFirewallRule("EthernetPacketGenerator API", 8080));

        Log("LabServer.Start begin");
        try { LabServer.Start(); Log("LabServer.Start ok"); }
        catch (Exception ex)
        {
            Log($"LabServer.Start FAIL: {ex}");
            // 포트 충돌이면 기존 프로세스가 이미 8080을 점유 중
            MessageBox.Show(
                $"포트 8080을 바인드할 수 없습니다:\n{ex.Message}\n\n이전 EthernetPacketGenerator 인스턴스가 실행 중일 수 있습니다.",
                "서버 시작 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // MainWindow 직접 생성 (StartupUri 제거)
        Log("new MainWindow begin");
        MainWindow mainWindow;
        try
        {
            mainWindow = new MainWindow();
            Log("new MainWindow ok");
        }
        catch (Exception ex)
        {
            Log($"new MainWindow FAIL: {ex}");
            MessageBox.Show(
                $"앱 초기화 오류:\n{ex.Message}\n\n{ex.StackTrace}",
                "시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = mainWindow;
        Log("mainWindow.Show begin");
        mainWindow.Show();
        Log("mainWindow.Show ok");

        if (mainWindow.DataContext is ViewModels.MainViewModel vm)
        {
            LabServer.MainVm = vm;
            LabServer.AutomationVm = vm.AutomationVM;
            LabServer.CaptureVm = vm.CaptureVM;
            LabServer.HyperTerminalVm = vm.HyperTerminalVM;
        }
    }

    // 방화벽 인바운드 규칙이 없을 때만 UAC 프롬프트를 띄워 한 번 추가한다.
    private static void EnsureFirewallRule(string ruleName, int port)
    {
        try
        {
            // 규칙 존재 여부 확인 (관리자 권한 불필요)
            var check = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "netsh",
                    Arguments              = $"advfirewall firewall show rule name=\"{ruleName}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true
                }
            };
            check.Start();
            check.StandardOutput.ReadToEnd();
            check.WaitForExit();

            if (check.ExitCode == 0) return; // 이미 규칙 있음

            // 규칙 없음 → UAC 권한 상승으로 추가 (최초 1회)
            Process.Start(new ProcessStartInfo
            {
                FileName        = "netsh",
                Arguments       = $"advfirewall firewall add rule name=\"{ruleName}\" " +
                                  $"dir=in action=allow protocol=TCP localport={port}",
                UseShellExecute = true,
                Verb            = "runas",
                CreateNoWindow  = true
            })?.WaitForExit();
        }
        catch { /* 방화벽 추가 실패 시 무시 — 수동 허용 필요 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LabServer.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"DispatcherUnhandled: {e.Exception}");
        MessageBox.Show($"오류가 발생했습니다:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log($"DomainUnhandled: {e.ExceptionObject}");
        if (e.ExceptionObject is Exception ex)
            MessageBox.Show($"심각한 오류:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
