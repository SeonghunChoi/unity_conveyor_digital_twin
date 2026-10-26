using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ConveyorTwin.Shared;
using ConveyorTwin.Twin;
using ConveyorTwin.Twin.OpcUa;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ConveyorTwin.EditorTools
{
    /// <summary>
    /// 로봇 OPC UA 연결 점검 (2단계 5일 차). 트윈과 같은 RobotOpcUaLink + RobotGate를 에디터(Mono)에서 N초 돌리고
    /// 상태 전환·검증 통계·수신 지연을 출력한다. 더미 서버가 실행 중이어야 한다.
    ///
    /// 명령줄: Unity.exe -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.EditorTools.RobotLinkCheck.CheckBatch
    ///         -linkSeconds 30 [-freshPki] -logFile link-check.log
    ///   -freshPki: 트윈 PKI를 지우고 시작해 서버 exe 인증서 발급 경로까지 시험한다
    /// 점검 중 서버를 껐다 켜면 재연결·서버 재시작 감지를 확인할 수 있다.
    /// </summary>
    public static class RobotLinkCheck
    {
        [MenuItem("Tools/Twin/Check Robot OPC UA Link (10s)")]
        static void CheckMenu() => Run(10, false);

        public static void CheckBatch()
        {
            var args = Environment.GetCommandLineArgs();
            int seconds = 30;
            int i = Array.IndexOf(args, "-linkSeconds");
            if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
            bool fresh = args.Contains("-freshPki");
            EditorApplication.Exit(Run(seconds, fresh) ? 0 : 1);
        }

        static bool Run(int seconds, bool freshPki)
        {
            var pki = Path.Combine(Application.persistentDataPath, "pki");
            if (freshPki && Directory.Exists(pki))
                Directory.Delete(pki, true);

            var sb = new StringBuilder($"[Link] 로봇 OPC UA 연결 점검 {seconds}s, PKI {pki}{(freshPki ? " (새로 발급)" : "")}\n");
            var gate = new RobotGate();
            int restarts = 0;
            gate.ServerRestarted += (a, b) => { restarts++; sb.AppendLine($"    서버 재시작 감지: {a:HH:mm:ss} → {b:HH:mm:ss}"); };

            var perNode = new Dictionary<string, int>();
            var lagMs = new List<double>();
            var rejected = new List<string>();
            var link = new RobotOpcUaLink(pki, ServerLauncher.FindServerExe);
            var sw = Stopwatch.StartNew();
            var lastState = (RobotLinkState)(-1);
            double connectedAt = -1, firstJointAt = -1;
            int jointConnect = -1;   // 접속마다 첫 관절각은 "현재값 재전송"이라 지연 통계에서 뺀다
            try
            {
                link.Start();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(50);
                    if (link.State != lastState)
                    {
                        lastState = link.State;
                        var line = $"  {sw.Elapsed.TotalSeconds,6:F2}s  상태 {lastState}" +
                                   (link.LastError != null ? $"  ({link.LastError})" : "") +
                                   (lastState == RobotLinkState.Connected ? $"  접속 {link.ConnectCount}회째, 서버 시작 {link.ServerStartUtc:HH:mm:ss}" : "");
                        sb.AppendLine(line);
                        Debug.Log("[LinkState]" + line);   // 진행 중에도 로그 파일에 남겨 외부 스크립트가 시점을 맞출 수 있게
                        if (lastState == RobotLinkState.Connected && connectedAt < 0) connectedAt = sw.Elapsed.TotalSeconds;
                    }
                    if (link.IsConnected) gate.ObserveServerStart(link.ServerStartUtc);

                    var now = DateTime.UtcNow;
                    while (link.TryDequeue(out var s))
                    {
                        if (!gate.Accept(s, now, out var reason))
                        {
                            if (reason != null) rejected.Add($"{Short(s.Node)}: {reason}");
                            continue;
                        }
                        perNode.TryGetValue(s.Node, out var n);
                        perNode[s.Node] = n + 1;
                        if (s.Node == RobotNodes.JointPositions)
                        {
                            if (firstJointAt < 0) firstJointAt = sw.Elapsed.TotalSeconds;
                            if (jointConnect == link.ConnectCount)
                                lagMs.Add(s.ReceivedMs - new DateTimeOffset(s.SourceUtc).ToUnixTimeMilliseconds());
                            jointConnect = link.ConnectCount;
                        }
                    }
                }
            }
            finally
            {
                link.Dispose();
            }

            if (link.ProvisionLog != null) sb.AppendLine($"  인증서 발급: {link.ProvisionLog}");
            sb.AppendLine($"  첫 접속 {connectedAt:F2}s, 첫 관절각 {firstJointAt:F2}s, 접속 {link.ConnectCount}회, 서버 재시작 감지 {restarts}회");
            sb.AppendLine("  노드별 수신 (검증 통과):");
            foreach (var kv in perNode.OrderBy(k => k.Key))
                sb.AppendLine($"    {Short(kv.Key),-36} {kv.Value}");
            if (lagMs.Count > 0)
            {
                var sorted = lagMs.OrderBy(x => x).ToList();
                sb.AppendLine($"  관절각 {lagMs.Count}건 ({lagMs.Count / (double)seconds:F0}/s), SourceTimestamp → 수신 지연 " +
                              $"중앙값 {sorted[sorted.Count / 2]:F0}ms, 95% {sorted[(int)(sorted.Count * 0.95)]:F0}ms, 최대 {sorted.Last():F0}ms");
            }
            sb.AppendLine($"  검증: 통과 {gate.Accepted}, Bad {gate.RejectedBad}, 중복·과거 {gate.RejectedOld}, 미래 {gate.RejectedFuture}, " +
                          $"범위 밖 {gate.RejectedRange}, Uncertain {gate.Uncertain}, 중복(재구독 현재값) {gate.Duplicates}");
            foreach (var r in rejected.GroupBy(r => r).Take(10))
                sb.AppendLine($"    버림: {r.Key} ×{r.Count()}");

            bool ok = link.ConnectCount > 0 && perNode.ContainsKey(RobotNodes.JointPositions) &&
                      gate.RejectedBad + gate.RejectedOld + gate.RejectedFuture + gate.RejectedRange == 0;
            sb.AppendLine(ok ? "  결과: 통과" : "  결과: 실패");
            if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
            return ok;
        }

        static string Short(string node) => node.Replace(RobotNodes.System + ".", "");
    }
}
