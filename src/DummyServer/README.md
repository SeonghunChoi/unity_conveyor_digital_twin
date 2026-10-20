# DummyServer (WPF, .NET 8)

가상 컨베이어 겸 MQTT 브로커입니다. 트윈 입장에서는 "현실"이며, 원시 신호만 발행합니다.

## 실행

```bash
dotnet run --project src/DummyServer
```

배포 (13일 차): `dotnet publish src/DummyServer -r win-x64 --self-contained -p:PublishSingleFile=true`

## 구성

| 파일 | 역할 |
| --- | --- |
| `Mqtt/EmbeddedBroker.cs` | MQTTnet 브로커. `127.0.0.1:1883`(IPv6 `::1` 포함)에서만 대기, 접속 클라이언트 목록 제공 |
| `Mqtt/DevicePublisher.cs` | 가상 설비의 MQTT 클라이언트. 헤더(sid, seq, ts) 부여, JSON 직렬화, QoS·Retained 적용, 접속 시 `status` Last Will 등록 |
| `Simulation/ConveyorSimulator.cs` | 20ms 주기로 벨트·모터 물리량과 물건 위치를 진행시키고, 센서 엣지·측정값을 발행 |
| `MainViewModel.cs`, `MainWindow.xaml` | 상태 표시와 송신 로그 (100ms마다 일괄 반영, 최근 1000건) |

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
| 물건 | 길이 0.3 m 박스, 2~15 kg, 2.5~5 s 간격 투입, 앞 물건과 0.4 m 이상 간격 | |
| 센서 | 입구 = 0 m, 출구 = `Equipment.BeltLengthM`(1.95 m) | 앞단 도달 시 `blocked: true`, 뒷단 통과 시 `false` |

물건 위치는 시간이 아니라 **속도를 적분**해서 구합니다. 속도가 0이 되면(모터 정지 장애) 물건도 그 자리에 멈춥니다.

## 검증 결과 (2026-10-20, 구독 클라이언트로 25초 수신)

- 토픽별 seq 연속, 센서 엣지 true/false 교대 확인
- 재접속 시 retained 측정값·`status`·`state/run` 즉시 수신, 센서 엣지는 retained 없음
- 입구 감지 → 출구 감지 3.90~3.92 s (이론값 1.95 / 0.5 = 3.9 s)
- 발행 → 수신 지연 평균 0.4 ms
- 창을 닫으면 `status`에 `online: false` 발행

## MQTTnet 버전

서버는 MQTTnet **5.2** (`MQTTnet` + `MQTTnet.Server`)를 씁니다. 5.x는 net8.0/net10.0 전용이라 Unity에서 쓸 수 없으므로,
트윈은 netstandard 빌드가 있는 **4.3.x**를 씁니다. 두 쪽은 MQTT 프로토콜로만 통신하므로 버전이 달라도 문제없습니다.
