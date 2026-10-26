using System.Net;
using System.Net.Sockets;

namespace ConveyorTwin.DummyServer.OpcUa;

/// <summary>
/// 로봇 컨트롤러와 트윈 사이의 "네트워크" (2단계 7일 차). 외부 포트(4840)로 들어온 TCP 연결을 OPC UA 서버의 내부 포트(4841)로 넘긴다.
///
/// "로봇 통신 끊김" 장애는 이 중계만 끊는다: 열린 연결을 모두 끊고 새 연결도 받지 않는다 (케이블을 뽑은 것과 같음).
/// OPC UA 서버(= 로봇 컨트롤러)는 그대로 돌고 있으므로 서버 시작 시각이 바뀌지 않는다 — 트윈은 이를 "재시작"이 아니라
/// "통신 끊김 후 복구"로 구분해야 한다. 스택에는 실행 중인 서버의 연결만 끊는 공개 API가 없어 바깥에서 끊는다.
/// </summary>
public sealed class NetworkRelay : IAsyncDisposable
{
    readonly IPEndPoint _listen;
    readonly IPEndPoint _target;
    readonly object _lock = new();
    readonly List<(TcpClient A, TcpClient B)> _pairs = new();
    TcpListener? _listener;
    CancellationTokenSource? _acceptCts;
    volatile bool _cut;

    public NetworkRelay(int listenPort, int targetPort)
    {
        _listen = new IPEndPoint(IPAddress.Loopback, listenPort);
        _target = new IPEndPoint(IPAddress.Loopback, targetPort);
    }

    /// <summary>중계 중인 연결 수</summary>
    public int Connections { get { lock (_lock) return _pairs.Count; } }

    /// <summary>통신 끊김 장애: true면 모든 연결을 끊고 포트를 닫는다 (접속 거부), false면 다시 연다</summary>
    public bool Cut
    {
        get => _cut;
        set
        {
            if (_cut == value) return;
            _cut = value;
            if (value)
            {
                StopListening();
                CloseAll();
            }
            else
            {
                StartListening();
            }
        }
    }

    public void Start() => StartListening();

    void StartListening()
    {
        _acceptCts = new CancellationTokenSource();
        _listener = new TcpListener(_listen);
        _listener.Start();
        var ct = _acceptCts.Token;
        _ = Task.Run(() => AcceptLoopAsync(_listener, ct));
    }

    void StopListening()
    {
        _acceptCts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient a;
            try { a = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }   // 리스너가 닫힘 (끊김 장애·종료)

            var b = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            a.NoDelay = true;
            try { await b.ConnectAsync(_target, ct); }
            catch (Exception)
            {
                a.Dispose();
                b.Dispose();
                continue;
            }
            lock (_lock) _pairs.Add((a, b));
            _ = PumpPairAsync(a, b);
        }
    }

    async Task PumpPairAsync(TcpClient a, TcpClient b)
    {
        try
        {
            using var sa = a.GetStream();
            using var sb = b.GetStream();
            // 한쪽이 끝나면 다른 쪽도 끊는다
            await Task.WhenAny(sa.CopyToAsync(sb), sb.CopyToAsync(sa));
        }
        catch (Exception) { /* 끊김 */ }
        finally
        {
            lock (_lock) _pairs.Remove((a, b));
            a.Dispose();
            b.Dispose();
        }
    }

    void CloseAll()
    {
        List<(TcpClient A, TcpClient B)> pairs;
        lock (_lock) pairs = _pairs.ToList();
        foreach (var (a, b) in pairs)
        {
            // FIN 대신 RST: 케이블을 뽑은 것처럼 상대가 정상 종료 절차 없이 끊김을 알게 한다
            try { a.Client.LingerState = new LingerOption(true, 0); } catch (Exception) { }
            a.Dispose();
            b.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        StopListening();
        CloseAll();
        return ValueTask.CompletedTask;
    }
}
