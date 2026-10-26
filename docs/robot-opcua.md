# 로봇 OPC UA 규약 (2단계)

더미 서버의 가상 로봇 컨트롤러(서버)와 트윈(클라이언트)이 지키는 OPC UA 계약입니다.
코드 정의는 `src/Shared/RobotNodes.cs`이고, 이 문서와 코드가 다르면 코드를 기준으로 문서를 고칩니다.
컨베이어는 그대로 MQTT([protocol.md](protocol.md))이고, 로봇만 OPC UA입니다.

## 1. 왜 로봇만 OPC UA인가

현장에서는 프로토콜이 하나로 통일되어 있지 않습니다. 컨베이어 같은 단순 설비는 PLC 게이트웨이가 MQTT로 값을 흘려보내고,
로봇 컨트롤러는 자체 OPC UA 서버를 내장하는 경우가 많습니다. 트윈이 두 프로토콜을 함께 받아 하나의 화면으로 합치는 것이 이 단계의 요점입니다.

| | MQTT (컨베이어) | OPC UA (로봇) |
| --- | --- | --- |
| 데이터 구조 | 토픽 이름 규칙 + JSON (1단계에서 직접 정의) | 서버가 노출하는 주소 공간(노드 트리)을 클라이언트가 탐색 |
| 시각 | 페이로드의 `ts` | 값마다 SourceTimestamp (표준) |
| 신뢰도 | 페이로드의 `quality` | 값마다 StatusCode (표준) |
| 순서·중복 | `sid` + `seq`로 트윈이 직접 검증 | 구독 알림의 일련번호를 스택이 관리 |
| 끊김 감지 | `status` 토픽의 Last Will | 세션 KeepAlive (표준) |
| 보안 | 없음 (루프백 전용) | 인증서 기반 보안 채널 (서명 + 암호화) |

1단계에서 MQTT 위에 직접 만든 `sid`/`seq`/`ts`/`quality`가 OPC UA에는 표준으로 들어 있습니다.

## 2. 접속

| 항목 | 값 |
| --- | --- |
| 엔드포인트 | `opc.tcp://127.0.0.1:4840` (더미 서버 프로세스에 내장) |
| 보안 정책 | None (점검 도구용), SignAndEncrypt: Basic256Sha256 / Aes128_Sha256_RsaOaep / Aes256_Sha256_RsaPss |
| 트윈이 쓰는 정책 | **SignAndEncrypt + Basic256Sha256** (Mono는 RsaPss 불가, [opcua-spike.md](opcua-spike.md)) |
| 사용자 인증 | 익명 |
| 쓰기 | 없음 — 모든 변수가 읽기 전용 (트윈은 값을 만들거나 고치지 않는다, 원칙 1) |

### 인증서 발급과 신뢰

Unity(Mono)는 자체 서명 인증서를 만들지 못하므로 **서버 exe가 트윈 인증서를 발급**합니다.
현장에서 GDS(Global Discovery Server)가 장비 인증서를 발급·배포하는 역할을 축소한 형태입니다.

```bash
ConveyorTwin.DummyServer.exe --provision-twin-cert <트윈 pki 경로>     # 창 없이 실행, 종료 코드 0 = 성공, <pki>/provision.log
```

1. 트윈 신원(`OpcUaEndpoint.TwinApplicationUri`, `TwinSubject`)으로 인증서를 만들어 `<트윈 pki>/own`에 넣는다 (있으면 그대로 둔다)
2. 서버 인증서를 `<트윈 pki>/trusted`에, 트윈 인증서를 서버 `pki/trusted`에 넣는다

양쪽 모두 **자동 수락을 끄고** 신뢰 목록으로만 상대를 믿습니다. 서버 PKI는 `%LOCALAPPDATA%\ConveyorTwin\DummyServer\pki`.
서버는 시작할 때 신뢰 목록을 읽어 캐시하므로, 실행 중에 발급한 인증서는 검증 이벤트에서 디스크의 신뢰 목록을 다시 확인해 받아들입니다.

## 3. 정보 모델

[OPC 40010 Robotics](https://reference.opcfoundation.org/Robotics/) 동반 규격의 구조와 이름을 따르되,
표준 NodeSet(DI·Robotics 타입)을 불러오지 않고 서버가 직접 만든 **단순화 모델**입니다. 네임스페이스 `urn:conveyortwin:robotcell`,
노드 ID는 모두 문자열 ID입니다 (`ns=<인덱스>;s=RobotCell01.…`, 인덱스는 접속 후 NamespaceUri로 찾음).

```
Objects/RobotCell01                                   MotionDeviceSystem
  MotionDevices/IRB2600                               MotionDevice
    Manufacturer = "ABB", Model = "IRB 2600-20/1.65", SerialNumber   (Property)
    JointPositions            Double[6]  [deg]        ← 트윈이 구독 (벤더 확장)
    Axes/Axis1..6/ParameterSet/ActualPosition  Double [deg]
  Controllers/IRC5                                    Controller
    MotorsOn                  Boolean
    SpeedOverride             Double [%]
    TaskControls/T_ROB1/ParameterSet                  TaskControl
      TaskProgramName         String  "PalletizeBoxes"
      ExecutionState          Int32   0=Stopped, 1=Running
      CycleCount              UInt32  완료한 픽·적재 사이클 수
    IO
      DO_VacuumOn             Boolean 진공 켜기 출력
      DI_VacuumOk             Boolean 진공 압력 스위치 (물건 흡착됨)
  SafetyStates/SafetyState/ParameterSet               SafetyState
    OperationalMode           Int32   40010 OperationalModeEnumeration (3 = Automatic)
    EmergencyStop             Boolean
    ProtectiveStop            Boolean
```

### 설계 메모

- **JointPositions 배열**: 40010은 축마다 `ActualPosition`을 둡니다. 축별 노드를 따로 구독하면 같은 순간의 6축이 서로 다른 알림으로
  나뉘어 들어와 자세가 잠깐 찢어질 수 있으므로, 한 SourceTimestamp에 6축을 담은 배열을 따로 둡니다. 축별 노드도 같은 값으로 갱신합니다.
- **원시 신호만**: 그리퍼는 "출력(진공 켜기)"과 "입력(압력 스위치)"을 따로 냅니다. 진공을 켰는데 압력이 안 잡히면 픽 실패 —
  이 판단은 서버가 아니라 트윈이 합니다 (원칙 2). 정지 원인도 `MotorsOn`·`EmergencyStop`·`ProtectiveStop`·`ExecutionState` 조합으로 트윈이 추론합니다.
- **열거형은 Int32**: 40010은 열거 DataType을 쓰지만, 단순화 모델에서는 Int32 + 설명(Description)으로 대신합니다. 값은 `Shared.OperationalMode`·`ProgramExecutionState`.
- **값이 바뀔 때만 갱신**: 같은 값이면 노드를 갱신하지 않으므로 SourceTimestamp는 "마지막으로 바뀐 시각"입니다.
  서버의 데이터 변경 필터도 같은 값은 알리지 않으므로, 트윈은 **값의 갱신 주기로 끊김을 판단하지 않고 세션 KeepAlive로** 판단합니다 (5일 차).
- **표준 대비 생략**: EngineeringUnits 속성, 40010 타입 정의(ObjectType), DI의 DeviceSet. 2단계 범위에서는 구조와 이름을 맞추는 데까지만 합니다.

## 4. 점검 도구 (`src/Tools/OpcUaProbe`)

트윈과 같은 신원·정책으로 접속해 주소 공간·값·구독을 출력합니다 (UaExpert 대신 명령줄로 확인, 솔루션 미포함).

```bash
ConveyorTwin.DummyServer.exe --provision-twin-cert C:\temp\twin-pki
dotnet run --project src/Tools/OpcUaProbe -- C:\temp\twin-pki            # Basic256Sha256
dotnet run --project src/Tools/OpcUaProbe -- C:\temp\twin-pki None       # 보안 없음
```

### 결과 (2026-10-26, 3일 차)

| 시험 | 결과 |
| --- | --- |
| 발급받은 인증서로 Basic256Sha256 접속 | ✅ 154ms, `Shared.RobotNodes` 노드 19개 모두 존재, 변수 20개 |
| None 접속 | ✅ 82ms |
| 서버가 발급하지 않은 인증서 (주체 이름은 같음) | ✅ 거부 `BadSecurityChecksFailed` |
| 서버 실행 중에 새로 발급한 인증서 | ✅ 접속 (신뢰 목록 재확인 경로) |
| 단일 exe 배포본 (`dotnet publish … PublishSingleFile`)의 발급 모드·접속·구독 | ✅ |
| JointPositions 구독 | 홈 자세 고정이라 최초 1건 (값이 바뀌지 않으면 알림 없음 — 정상) |

3일 차의 로봇은 홈 자세·자동 모드·모터 켜짐·프로그램 정지 상태만 내는 임시 구현(`Simulation/RobotPlaceholder`)이고,
4일 차 가상 로봇 컨트롤러가 관절 보간과 픽·적재 사이클로 값을 채웁니다.
