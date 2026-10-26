# DummyServer (WPF, .NET 8)

가상 컨베이어 겸 MQTT 브로커입니다. 트윈 입장에서는 "현실"이며, 원시 신호만 발행합니다.

## 실행

```bash
dotnet run --project src/DummyServer
```

배포 (13일 차): `dotnet publish src/DummyServer -r win-x64 --self-contained -p:PublishSingleFile=true`

트윈 OPC UA 인증서 발급 (2단계, 창 없이 실행): `ConveyorTwin.DummyServer.exe --provision-twin-cert <트윈 pki 경로>`

## 구성

| 파일 | 역할 |
| --- | --- |
| `Mqtt/EmbeddedBroker.cs` | MQTTnet 브로커. `127.0.0.1:1883`(IPv6 `::1` 포함)에서만 대기, 접속 클라이언트 목록 제공 |
| `Mqtt/DevicePublisher.cs` | 가상 설비의 MQTT 클라이언트. 헤더(sid, seq, ts) 부여, JSON 직렬화, QoS·Retained 적용, 접속 시 `status` Last Will 등록 |
| `Simulation/ConveyorSimulator.cs` | 20ms 주기로 벨트·모터 물리량과 물건 위치를 진행시키고, 센서 엣지·측정값을 발행 |
| `MainViewModel.cs`, `MainWindow.xaml` | 상태 표시와 송신 로그 (100ms마다 일괄 반영, 최근 1000건) |
| `OpcUa/RobotOpcUaServer.cs` | (2단계) 로봇 OPC UA 서버 `opc.tcp://127.0.0.1:4840`, 신뢰 목록 기반 보안, 트윈 인증서 발급(`CertificateProvisioning`) |
| `OpcUa/RobotNodeManager.cs` | (2단계) OPC 40010 구조를 따른 로봇 주소 공간 (`Shared.RobotNodes`, [docs/robot-opcua.md](../../docs/robot-opcua.md)) |
| `Simulation/RobotController.cs` | (2단계) 가상 로봇 컨트롤러: 출구 스토퍼의 물건을 집어 팔레트 48칸에 적재, 관절 공간 5차 보간 ([docs/robot-cell.md](../../docs/robot-cell.md)) |

### 왜 브로커에 "클라이언트로" 발행하나

브로커 내부 API로 메시지를 주입하면 접속·끊김이 없어서 Last Will이 동작하지 않습니다.
가상 설비를 일반 클라이언트로 접속시켜 두면, 8일 차 "연결 끊김" 장애를 실제 비정상 종료로 재현할 수 있습니다.
정상 종료(창 닫기) 때는 Will이 발행되지 않으므로 `online: false`를 직접 발행한 뒤 접속을 끊습니다.

### 시뮬레이션 모델

| 항목 | 값 | 비고 |
| --- | --- | --- |
| 벨트 속도 | 0.5 m/s ± 0.004 | |
| 모터 전류 | 1.8 A + 벨트 위 하중 × 0.02 A/kg | 물건이 올라가면 전류가 오른다 (물건 걸림 장애에서 전류 상승의 근거) |
| 모터 온도 | 25 °C에서 시작, 목표값 `25 + 12 × 전류`로 1차 지연(시정수 90 s) | 정상 상태 약 47~50 °C |
| 물건 | 길이 0.3 m 박스, 2~15 kg, 6~8 s 간격 투입, 앞 물건과 0.4 m 이상 간격 | 2단계에서 로봇 사이클(약 4.5 s)보다 길게 늘림 (1단계 2.5~5 s) |
| 출구 스토퍼 | 앞단이 출구 센서에 닿으면 멈춤 | 로봇이 집어 들 때 벨트에서 빠지고 출구 `blocked: false` (`RequestPick`) |
| 센서 | 입구 = 0 m, 출구 = `Equipment.BeltLengthM`(1.95 m) | 앞단 도달 시 `blocked: true`, 뒷단 통과 시 `false` |

물건 위치는 시간이 아니라 **속도를 적분**해서 구합니다. 속도가 0이 되면(모터 정지 장애) 물건도 그 자리에 멈춥니다.

## 장애 버튼 (8~9일 차)

| 버튼 | 구현 위치 | 동작 |
| --- | --- | --- |
| 연결 끊김 | `DevicePublisher.DropConnection` | 클라이언트를 DISCONNECT 없이 `Dispose` → 브로커가 Last Will 발행. 끄면 새로 접속해 online 발행. 끊긴 동안의 메시지는 "미송신"으로 로그에 남는다 |
| 전송 지연 3초 | `DevicePublisher` 송신 큐 | 순서를 지키는 큐에서 3초 뒤 송신. 꺼도 밀린 메시지가 먼저 다 나간다 |
| 순서 뒤섞기 | `DevicePublisher.SendLaterAsync` | 메시지마다 0~250ms 무작위 지연 (순서가 바뀜), 5%는 300~900ms 뒤 중복 재전송 |
| 이상값 송신 | `ConveyorSimulator` | 온도 999.9 (측정 범위 -20~150 밖), 전류 `quality: bad` |
| 물건 걸림 | `ConveyorSimulator` | 입구를 지난 맨 앞 물건이 멈춤 (없으면 다음 물건이 1.2m에서). 뒤 물건은 추월하지 못해 쌓이고, 모터 전류 +1.2A |
| 모터 정지 | `ConveyorSimulator` | `state/run` = `fault` (`motor overload trip`), 속도·전류 0 |

통신 장애는 "통신 경로"인 `DevicePublisher`가, 설비 장애는 "현실"인 `ConveyorSimulator`가 만든다.

로봇 셀 장애 4종(로봇 통신 끊김, 비상정지, 흡착 실패, 관절 엔코더 이상)은 [docs/robot-faults.md](../../docs/robot-faults.md).
같은 원칙으로 통신 끊김은 네트워크 경로인 `OpcUa/NetworkRelay`(4840→4841 중계)가, 나머지는 `Simulation/RobotController`가 만든다.
연결이 끊겨도 시뮬레이션은 계속 진행된다 (현실은 멈추지 않는다).

검증 (UI Automation으로 버튼을 누르고 구독 클라이언트로 기록): 6종 모두 위 동작대로 수신됨.
전송 지연을 끈 직후에도 토픽 간 `ts` 역전 0건, 밀린 메시지가 약 2.9초에 걸쳐 차례로 나간 뒤 지연 0으로 돌아옴.

## 검증 결과 (2026-10-20, 구독 클라이언트로 25초 수신)

- 토픽별 seq 연속, 센서 엣지 true/false 교대 확인
- 재접속 시 retained 측정값·`status`·`state/run` 즉시 수신, 센서 엣지는 retained 없음
- 입구 감지 → 출구 감지 3.90~3.92 s (이론값 1.95 / 0.5 = 3.9 s)
- 발행 → 수신 지연 평균 0.4 ms
- 창을 닫으면 `status`에 `online: false` 발행

## MQTTnet 버전

서버는 MQTTnet **5.2** (`MQTTnet` + `MQTTnet.Server`)를 씁니다. 5.x는 net8.0/net10.0 전용이라 Unity에서 쓸 수 없으므로,
트윈은 netstandard 빌드가 있는 **4.3.x**를 씁니다. 두 쪽은 MQTT 프로토콜로만 통신하므로 버전이 달라도 문제없습니다.
