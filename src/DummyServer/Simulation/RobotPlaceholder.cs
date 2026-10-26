using ConveyorTwin.DummyServer.OpcUa;

namespace ConveyorTwin.DummyServer.Simulation;

/// <summary>
/// 3일 차 임시 로봇: 홈 자세에서 자동 모드·모터 켜짐·프로그램 정지 상태를 50ms마다 반영한다.
/// 4일 차 가상 로봇 컨트롤러(관절 보간, 픽·적재 사이클)로 교체한다.
/// </summary>
public sealed class RobotPlaceholder
{
    const int PeriodMs = 50;
    readonly RobotOpcUaServer _server;

    public RobotPlaceholder(RobotOpcUaServer server) => _server = server;

    public async Task RunAsync(CancellationToken ct)
    {
        var home = RobotSnapshot.Home();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PeriodMs));
        while (await timer.WaitForNextTickAsync(ct))
            _server.Publish(home);
    }
}
