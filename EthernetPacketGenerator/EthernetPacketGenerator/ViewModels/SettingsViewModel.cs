using System.Windows.Input;
using System.Windows.Threading;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels;

/// <summary>
/// Exposes LabWorkerService connection settings and status to the Settings tab.
/// Polls the connection state every 2 seconds via a DispatcherTimer.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;

    private string _serverUrl        = string.Empty;
    private string _workerId         = string.Empty;
    private string _connectionStatus = "Disconnected";
    private bool   _isConnected;

    public string ServerUrl
    {
        get => _serverUrl;
        private set => SetProperty(ref _serverUrl, value);
    }

    public string WorkerId
    {
        get => _workerId;
        private set => SetProperty(ref _workerId, value);
    }

    /// <summary>"Connected", "Reconnecting...", or "Disconnected".</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>Restarts the LabWorkerService WebSocket loop.</summary>
    public ICommand ReconnectCommand { get; }

    public SettingsViewModel()
    {
        ReconnectCommand = new RelayCommand(Reconnect);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // Populate immediately so the UI is not blank on first render.
        Refresh();
    }

    private void Reconnect()
    {
        var worker = GetLabWorker();
        worker?.Restart();
        // Refresh immediately after requesting reconnect so status updates to "Reconnecting..."
        Refresh();
    }

    private void Refresh()
    {
        var worker = GetLabWorker();

        if (worker == null)
        {
            ServerUrl        = string.Empty;
            WorkerId         = string.Empty;
            IsConnected      = false;
            ConnectionStatus = "Disconnected";
            return;
        }

        ServerUrl   = worker.ServerUrl;
        WorkerId    = worker.WorkerId;
        IsConnected = worker.IsConnected;

        ConnectionStatus = worker.IsConnected ? "Connected" : "Reconnecting...";
    }

    private static LabWorkerService? GetLabWorker()
    {
        return (System.Windows.Application.Current as App)?.LabWorker;
    }
}
