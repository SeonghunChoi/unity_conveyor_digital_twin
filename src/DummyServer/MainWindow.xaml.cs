using System.ComponentModel;
using System.Net.Sockets;
using System.Windows;

namespace ConveyorTwin.DummyServer;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.StartAsync();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            MessageBox.Show(this, "1883 포트를 이미 사용 중입니다. 다른 서버(또는 MQTT 브로커)가 실행 중인지 확인하세요.",
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    void OnClearLog(object sender, RoutedEventArgs e) => _vm.ClearLog();

    protected override async void OnClosing(CancelEventArgs e)
    {
        // 정상 종료 시 offline 상태를 발행하고 브로커를 내린 뒤 창을 닫는다
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        try { await _vm.DisposeAsync(); }
        finally { Application.Current.Shutdown(); }
    }
}
