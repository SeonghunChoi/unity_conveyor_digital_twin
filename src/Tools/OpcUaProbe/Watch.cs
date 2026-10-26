using System.Collections.Concurrent;
using ConveyorTwin.Shared;
using MQTTnet;
using Opc.Ua;
using Opc.Ua.Client;

/// <summary>
/// watch 모드 (4일 차 가상 로봇 컨트롤러 검증). 같은 PC라 서버의 SourceTimestamp와 MQTT 수신 시각을 한 시계로 비교할 수 있다.
///  - 사이클 시간 (CycleCount 증가 간격)
///  - 인계 순서: 출구 센서 감지 → 진공 켜짐 → 압력 스위치 → 출구 센서 해제 → 진공 꺼짐 → 사이클 증가
///  - 관절 속도: 연속 샘플의 Δ각도/Δ시각이 관절 최대 속도를 넘지 않는지
///  - 출구 대기: 출구 센서가 막혀 있던 시간 (로봇이 늦으면 뒤 물건이 쌓인다)
/// </summary>
static class Watch
{
    sealed record Ev(DateTime T, string Name, object Value);

    public static async Task<int> RunAsync(ISession session, ushort ns, int seconds)
    {
        var events = new ConcurrentQueue<Ev>();
        var joints = new ConcurrentQueue<(DateTime T, double[] Q)>();

        // ---- MQTT: 출구·입구 센서 엣지 ----
        var mqtt = new MqttClientFactory().CreateMqttClient();
        mqtt.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            bool blocked = e.ApplicationMessage.ConvertPayloadToString().Contains("\"blocked\":true");
            events.Enqueue(new Ev(DateTime.UtcNow, topic.EndsWith("exit") ? "Exit" : "Entry", blocked));
            return Task.CompletedTask;
        };
        await mqtt.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", 1883).WithClientId("opcua-probe-watch").Build());
        await mqtt.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(Topics.EntrySensor(Topics.DefaultDeviceId))
            .WithTopicFilter(Topics.ExitSensor(Topics.DefaultDeviceId)).Build());

        // ---- OPC UA: 로봇 신호 ----
        var sub = new Subscription(session.DefaultSubscription) { PublishingInterval = 100, KeepAliveCount = 10 };
        MonitoredItem Item(string id, string name, int samplingMs, int queue)
        {
            var mi = new MonitoredItem(sub.DefaultItem)
            {
                StartNodeId = new NodeId(id, ns), AttributeId = Attributes.Value, SamplingInterval = samplingMs, QueueSize = (uint)queue,
                DisplayName = name,
            };
            mi.Notification += (_, e) =>
            {
                if (e.NotificationValue is not MonitoredItemNotification n) return;
                if (name == "Joints") joints.Enqueue((n.Value.SourceTimestamp, (double[])n.Value.Value));
                else events.Enqueue(new Ev(n.Value.SourceTimestamp, name, n.Value.Value));
            };
            return mi;
        }
        sub.AddItems(new[]
        {
            Item(RobotNodes.JointPositions, "Joints", 20, 50),
            Item(RobotNodes.VacuumOn, "VacuumOn", 0, 10),
            Item(RobotNodes.VacuumOk, "VacuumOk", 0, 10),
            Item(RobotNodes.CycleCount, "Cycle", 0, 10),
            Item(RobotNodes.PalletPresent, "Pallet", 0, 10),
            Item(RobotNodes.ExecutionState, "Exec", 0, 10),
        });
        session.AddSubscription(sub);
        await sub.CreateAsync();
        Console.WriteLine($"[Watch] {seconds}초 기록 중...");
        await Task.Delay(seconds * 1000);
        await sub.DeleteAsync(true);
        await mqtt.DisconnectAsync();

        return Analyze(events.OrderBy(e => e.T).ToList(), joints.OrderBy(j => j.T).ToList(), seconds);
    }

    static int Analyze(List<Ev> ev, List<(DateTime T, double[] Q)> js, int seconds)
    {
        bool ok = true;
        var t0 = ev.Count > 0 ? ev[0].T : DateTime.UtcNow;
        string At(DateTime t) => $"{(t - t0).TotalSeconds,6:F2}s";

        // ---- 타임라인 (진공·사이클·출구) ----
        Console.WriteLine("[Watch] 타임라인 (입구 센서 제외)");
        foreach (var e in ev.Where(e => e.Name != "Entry" && e.Name != "Exec"))
            Console.WriteLine($"  {At(e.T)}  {e.Name,-9} {e.Value}");

        // ---- 사이클 시간 ----
        var cycles = ev.Where(e => e.Name == "Cycle").ToList();
        var intervals = cycles.Zip(cycles.Skip(1), (a, b) => (b.T - a.T).TotalSeconds).ToList();
        Console.WriteLine($"[Watch] 사이클 증가 {Math.Max(0, cycles.Count - 1)}회, 간격 " +
            (intervals.Count > 0 ? $"평균 {intervals.Average():F2}s (최소 {intervals.Min():F2}, 최대 {intervals.Max():F2})" : "-") +
            "  ※ 간격은 물건 투입 간격에 좌우됨");

        // ---- 인계 순서: 출구 감지마다 다음 이벤트 순서 확인 ----
        var exitOn = ev.Where(e => e.Name == "Exit" && (bool)e.Value).ToList();
        var waits = new List<double>();
        var pickLatency = new List<double>();
        int orderErrors = 0, checkedPicks = 0;
        foreach (var x in exitOn)
        {
            var after = ev.Where(e => e.T >= x.T).ToList();
            var vacOn = after.FirstOrDefault(e => e.Name == "VacuumOn" && (bool)e.Value);
            var vacOk = after.FirstOrDefault(e => e.Name == "VacuumOk" && (bool)e.Value);
            var exitOff = after.FirstOrDefault(e => e.Name == "Exit" && !(bool)e.Value);
            var vacOff = vacOk == null ? null : after.FirstOrDefault(e => e.Name == "VacuumOn" && !(bool)e.Value && e.T > vacOk.T);
            var cyc = vacOff == null ? null : after.FirstOrDefault(e => e.Name == "Cycle" && e.T >= vacOff.T);
            if (cyc == null) continue;   // 기록 끝에 걸린 사이클
            checkedPicks++;
            bool inOrder = vacOn!.T <= vacOk!.T && vacOk.T <= exitOff!.T.AddMilliseconds(50) && exitOff.T <= vacOff!.T && vacOff.T <= cyc.T;
            if (!inOrder) { orderErrors++; Console.WriteLine($"  순서 오류: 출구 감지 {At(x.T)}"); }
            waits.Add((exitOff!.T - x.T).TotalSeconds);
            pickLatency.Add((vacOn.T - x.T).TotalSeconds);
        }
        ok &= orderErrors == 0 && checkedPicks > 0;
        Console.WriteLine($"[Watch] 인계 {checkedPicks}건, 순서 오류 {orderErrors}건 (출구 감지 → 진공 켜짐 → 압력 스위치 → 출구 해제 → 진공 꺼짐 → 사이클)");
        if (waits.Count > 0)
            Console.WriteLine($"[Watch] 출구 감지 → 진공 켜짐 평균 {pickLatency.Average():F2}s, 출구가 막혀 있던 시간 평균 {waits.Average():F2}s / 최대 {waits.Max():F2}s");

        // ---- 관절 속도 ----
        double worstRatio = 0;
        int worstJoint = 0;
        for (int k = 1; k < js.Count; k++)
        {
            double dt = (js[k].T - js[k - 1].T).TotalSeconds;
            if (dt <= 0) continue;
            for (int i = 0; i < 6; i++)
            {
                double ratio = Math.Abs(js[k].Q[i] - js[k - 1].Q[i]) / dt / RobotKinematics.MaxSpeedDegPerS[i];
                if (ratio > worstRatio) { worstRatio = ratio; worstJoint = i; }
            }
        }
        bool speedOk = worstRatio <= 1.0;
        ok &= speedOk;
        Console.WriteLine($"[Watch] 관절 샘플 {js.Count}개 ({js.Count / (double)seconds:F0}/s), 최대 속도 = 한계의 {worstRatio:P0} (J{worstJoint + 1}) {(speedOk ? "OK" : "초과")}");

        var entries = ev.Count(e => e.Name == "Entry" && (bool)e.Value);
        Console.WriteLine($"[Watch] 입구 투입 {entries}건, 출구 도착 {exitOn.Count}건");
        Console.WriteLine(ok ? "[Watch] 결과: 통과" : "[Watch] 결과: 실패");
        return ok ? 0 : 1;
    }
}
