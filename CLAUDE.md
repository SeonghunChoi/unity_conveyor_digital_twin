# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Unity Conveyor Digital Twin

이 파일은 Claude Code가 세션 시작 시 읽는 프로젝트 컨텍스트입니다.
claude.ai에서 진행한 기획 대화의 결정 사항을 옮겨 왔습니다. 결정이 바뀌면 이 파일을 함께 갱신하세요.

## 프로젝트 개요

- **목적**: 디지털 트윈 회사 채용 담당자에게 보여줄 포트폴리오
- **증명할 것**: "게임 클라이언트 개발자가 디지털 트윈 개발에 적응할 수 있는가"에 대한 답.
  게임 개발에 없던 세 가지 — **CAD 임포트, 외부 데이터 연동, HMI/배포** — 를 직접 해봤다는 증거
- **개발자**: Unity 클라이언트 19년 차 (DOTS/ECS, URP 커스텀 셰이더, Addressables, 빌드 자동화에 능숙).
  디지털 트윈·산업 도메인(PLC, 산업 프로토콜, 공장 설비)은 처음이므로, 도메인 개념은 설명을 곁들이고 Unity/C# 기초 설명은 생략한다.
- **1단계 범위**: 벨트 컨베이어 1대로 파이프라인 전체를 한 바퀴 완주

## 핵심 설계 원칙

1. **트윈은 현실의 거울이다.** 게임과 달리 데이터의 주인은 현실(더미 서버)이며, 트윈은 값을 만들거나 고치지 않는다.
2. **서버는 원시 신호만 보낸다.** 잼 같은 상태 판단은 트윈이 신호 조합과 시간 관계로 추론한다.
3. **장애는 현실 쪽에서 일어난다.** 장애 버튼은 더미 서버 UI에 있고, 트윈은 대응만 한다.
4. **믿되 맹신하지 않는다.** 트윈은 순서, 중복, 이상값, 끊김을 스스로 검증한다.

## 아키텍처와 기술 스택

| 구성 | 기술 | 위치 |
| --- | --- | --- |
| 공용 규약 | netstandard2.1 클래스 라이브러리, 외부 의존성 없음 | `src/Shared` |
| 더미 서버 | WPF (.NET 8) + MQTTnet 5.2 브로커 내장 + 데이터 발행 | `src/DummyServer` |
| 트윈 | Unity 6.3 LTS, URP, Newtonsoft JSON, MQTTnet 4.3.7(netstandard2.1, `src/UnityPlugins`가 복사. 5.x는 net8+ 전용이라 불가) | `Twin` |
| 통신 | MQTT, `127.0.0.1:1883`, 토픽 `dt/conveyor01/#` | `docs/protocol.md` |
| CAD | mk GUF-P 2000 STEP → FreeCAD → glTF → glTFast | `cad` (저장소 미포함) |

- 플랫폼: **Windows 빌드만**. 웹 빌드는 1단계 제외.
- 트윈 실행 시 서버 미실행을 감지하면 트윈이 서버 exe를 자동 실행한다.
- 서버 배포: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
- Unity의 Asset Transformer(Pixyz)는 유료라 사용하지 않는다.

## 통신 규약 요약 (상세: docs/protocol.md)

- 모든 데이터 메시지 헤더: `sid`(세션 ID), `seq`(토픽별 순번), `ts`(Unix ms)
- 신호: 벨트 속도, 모터 온도, 모터 전류, 로드셀 무게, 입구·출구 광전 센서 엣지, 가동 상태, 연결 상태
- 주기 측정값은 QoS 0 + Retained, 상태·이벤트는 QoS 1, 센서 엣지는 Retained 금지
- 연결 끊김은 `status` 토픽의 Last Will로 감지
- 광전 센서는 물건 ID를 모르므로 트윈은 엣지 순서(FIFO)로 물건을 추적
- 잼 판단: 입구 감지 후 `벨트 길이 / 속도 × 1.5`초 안에 출구 감지가 없으면 잼
- **규약을 바꾸면 `src/Shared` 코드와 `docs/protocol.md`를 반드시 함께 수정한다.** 둘이 어긋나면 코드가 기준이다.

### Shared 코드가 양쪽에서 하는 역할

- `Topics`: 토픽 문자열은 항상 `Topics.*(deviceId)` 헬퍼로 만든다. 서버·트윈 어디에도 토픽 리터럴을 직접 쓰지 않는다.
- `SignalSpec`/`Signals`: **하나의 사양을 서버와 트윈이 반대 용도로 쓴다.** 서버는 주기·범위·QoS·Retained로 값을 생성·발행하고,
  트윈은 같은 사양으로 이상값(`IsInRange`)과 stale(`StaleAfterMs` = 주기 × 3, 이벤트성은 0)을 판정한다. 신호를 추가하면 여기와 `Topics`에 함께 넣는다.
- `Equipment`: 벨트 길이(1.95m)와 잼 판단 배수(1.5). 서버는 물건 이동 시뮬레이션에, 트윈은 잼 판단에 쓴다.
- `RobotCell` (2단계): 로봇 셀 배치·적재 패턴. 좌표는 **로봇 베이스 좌표계**(ABB base frame, m, Z-up 오른손, +X = 컨베이어 쪽).
  트윈에서 Unity 월드 = (0, 0, 1.70) + (y, z, −x). 박스 크기는 1단계 값(0.30×0.20×0.15)과 같아야 한다. 상세 `docs/robot-cell.md`.
- `Messages`: 데이터 메시지는 모두 `Envelope`(sid/seq/ts)를 상속한다. 예외로 `StatusMessage`는 seq가 없다 (Last Will로 브로커가 대신 발행하므로).
  트윈의 seq 기록은 `status`의 sid가 바뀔 때 초기화된다.
- 직렬화는 양쪽 모두 Newtonsoft JSON에 camelCase 네이밍 + 소문자 문자열 enum 설정을 각자 해야 `docs/protocol.md`의 페이로드와 맞는다.

## 장애 시나리오 (서버 버튼 → 트윈 대응)

연결 끊김, 전송 지연, 순서 뒤섞기, 이상값 송신, 물건 걸림(출구 미감지 + 전류 상승), 모터 정지.
서버는 실시간 송신 로그를 표시해 "보낸 것"과 트윈이 "받은 것"을 비교할 수 있게 한다.

## 1단계 일정과 현재 상태

하루 6~8시간 기준 15 작업일. **9일 차가 핵심 마일스톤**(장애 버튼 → 트윈 반응 동작).

| 일차 | 작업 | 상태 |
| --- | --- | --- |
| 1 | 저장소 구성, 토픽·페이로드 규약 정의 | ✅ 완료 |
| 2~3 | mk STEP 받기, FreeCAD 변환, Unity 임포트·계층 정리 (2일 초과 시 타임박스) | ✅ 완료 (`docs/cad-pipeline.md`) |
| 4~5 | WPF 서버 골격, 브로커 내장, 정상 데이터 발행, 로그 UI | ✅ 완료 |
| 6~7 | 트윈 MQTT 수신, 벨트 구동, 물건 생성·이동 | ✅ 완료 (벨트 표면 흐름은 10일 차 셰이더) |
| 8~9 | 장애 버튼 + 트윈 대응 로직, 잼 판단 ★ | ✅ 완료 (서버 시각 기준 재생, 6종 장애 Unity 확인) |
| 10 | 상태별 셰이더 하이라이트, 알람 | ✅ 완료 (StatusLit 셰이더, 벨트 흐름, ISA-18.2식 알람) |
| 11~12 | HMI 대시보드, 연결 상태 표시, 서버 자동 실행 | ✅ 완료 (UI Toolkit HMI, 서버 자동 실행·재실행 확인) |
| 13 | 단일 exe 배포, 클린 PC 실행 테스트 | ✅ 완료 (`Tools/Twin/Build Release` → `dist/`, Windows 샌드박스에서 실행 확인) |
| 14 | README, 아키텍처 구조도 | ✅ 완료 (`README.md`, `docs/architecture.md`) |
| 15 | 시연 대본, 영상 촬영·편집 | ✅ 완료 (YouTube 공개, README에 영상 링크·스크린샷) |

작업을 마치면 위 표의 상태를 갱신한다.

## 2단계: ABB 로봇 셀 + OPC UA (2026-10-23 확정)

- **범위**: 컨베이어 출구에서 박스를 집어 팔레트에 쌓는 로봇 셀 1개. 컨베이어는 MQTT 그대로, **로봇만 OPC UA**로 노출한다
  (실제 공장처럼 두 프로토콜이 섞인 구성을 트윈이 함께 다루는 것을 보여준다).
- **로봇 데이터의 주인은 서버**: 더미 서버가 가상 로봇 컨트롤러 역할을 하고 관절 6개 각도를 발행한다.
  트윈은 각도를 그대로 따라 그리며 IK·경로 계산을 하지 않는다 (원칙 1).
- **물건 인계**: 서버는 출구 도착 → 픽 → 팔레트 적재를 시뮬레이션하고, 트윈은 1단계 FIFO 추적을 이어받아 그리퍼·팔레트로 넘긴다.
- **OPC UA 스택**: OPC Foundation UA-.NETStandard (MIT). Unity는 netstandard2.1을 포함한 마지막 계열인 **1.5.378.x**만 쓸 수 있다
  (2.0은 net8+/net48 전용, MQTTnet 4/5와 같은 상황). 서버도 같은 1.5.378.x로 맞춘다. 기본 포트 4840.
- **기종(확정)**: **IRB 2600-20/1.65** (6축, 가반 20kg, 도달 1.65m). CAD는 ABB Library 9AKK107492A4717 STEP을 `cad/irb2600/`에 둔다.
  링크별 파일로 나뉘어 있고 홈 자세 좌표 그대로다. 관절 축(mm, STEP Z-up): J1 Z@(0,0,0), J2 Y@(150,0,445), J3 Y@(150,0,1145),
  J4 X@z1260, J5 Y@(945,0,1260), J6 X@z1260, 플랜지 x=1030. 분석: `tools/cad/inspect_robot.py`, 상세 `docs/cad-robot.md`.
- **로봇 glb (2일 차)**: `tools/cad/export_robot.py` → `cad/irb2600.glb` (+ `Twin/Assets/ThirdParty/Cad/`), 계층 `Robot/Base/J1/…/J6`.
  Unity에서 팔은 홈 자세에 −X로 뻗는다. 트윈은 `localRotation = AngleAxis(θ, 축)`, 축 J1 (0,−1,0) / J2·J3·J5 (0,0,1) / J4·J6 (1,0,0)
  (거울 반전 때문에 STEP 축을 옮긴 방향의 반대. `Tools/CAD/Check Robot Import`가 FK로 검증).
- **Mono 제약 (1일 차 스파이크, `docs/opcua-spike.md`)**: Unity 배포 빌드(Mono)에서 OPC UA 접속·구독은 되지만
  ① 스택의 TCP 접속이 Mono에서 실패하므로 `MonoTcpTransport`로 `opc.tcp` 바인딩을 교체해야 하고,
  ② Mono는 자체 서명 인증서를 만들지 못하므로 클라이언트 인증서를 .NET 쪽에서 발급해 넣어야 한다.
  ③ `Aes256_Sha256_RsaPss`는 Mono에서 불가 → 트윈은 Basic256Sha256을 쓴다.

| 일차 | 작업 | 상태 |
| --- | --- | --- |
| 1 | 로봇 기종·CAD 확정, 셀 배치, OPC UA 스파이크 (Unity 배포 빌드에서 접속 확인) | ✅ 완료 (`docs/opcua-spike.md`, `docs/cad-robot.md`) |
| 2 | 로봇 CAD 변환 (기존 파이프라인 재사용), 관절 피벗·계층 정리, 그리퍼·팔레트 | ✅ 완료 (관절 계층 glb, 진공 그리퍼·EUR 팔레트, 셀 배치 48칸 도달 확인 `docs/robot-cell.md`) |
| 3 | 서버 OPC UA 서버 골격, 로봇 정보 모델 (관절각, 모드, 프로그램 상태, 그리퍼, 사이클 수) | |
| 4 | 서버 가상 로봇 컨트롤러: 관절 공간 보간, 출구 도착 → 픽 사이클, 팔레트 적재 패턴 | |
| 5 | 트윈 OPC UA 클라이언트: 구독, StatusCode·SourceTimestamp 검증, 재연결 | |
| 6 | 트윈 로봇 미러링, 물건 인계 (컨베이어 → 그리퍼 → 팔레트) | |
| 7 | 셀 장애 버튼 + 트윈 대응 (OPC UA 세션 끊김, 비상정지, 픽 실패 등) ★ | |
| 8 | HMI·알람에 로봇 셀 통합 | |
| 9 | 배포 빌드, 클린 PC 실행 테스트 | |
| 10 | README·아키텍처 갱신, 시연 영상 | |

## 1단계에서 하지 않는 것

컨베이어 2대 이상, 실제 물건 모델(박스로 대체), 카메라 비전·바코드, 데이터 이력 저장, 웹 빌드, OPC UA.
요청 없이 범위를 넓히지 말 것. 2단계(ABB 로봇 셀, OPC UA 검토)에서 다룬다.

## 결과물

담당자 확인 순서: **2~3분 시연 영상 → 실행 빌드 → 코드 저장소(README)**.
README에는 구조도, 페이로드 규약, 장애 시나리오, CAD 출처·라이선스 안내를 담는다.

## 코딩 규칙

- `src/Shared`는 Unity에서도 쓰므로 **C# 9 범위**로 작성하고 `record`, `init` 접근자는 쓰지 않는다 (netstandard2.1에 `IsExternalInit` 없음).
- Shared에는 직렬화 라이브러리 의존성을 넣지 않는다. JSON 설정(camelCase, enum 소문자 문자열)은 서버·트윈 각자 담당.
- 코드 주석과 문서는 한국어.
- **CAD 원본과 변환 메시는 절대 커밋하지 않는다** (`.gitignore` 처리됨, 제조사 라이선스).

## 자주 쓰는 명령

```bash
dotnet build src/Shared            # 빌드 + Twin/Assets/Plugins/Shared 로 DLL 자동 복사
dotnet build src                   # 솔루션 전체 (DummyServer, Shared, UnityPlugins). Shared·MQTTnet DLL을 Twin/Assets/Plugins로 복사
dotnet run --project src/DummyServer  # 더미 서버 실행 (브로커 127.0.0.1:1883 + 가상 컨베이어)
# 배포 빌드: Unity 메뉴 Tools/Twin/Build Release → dist/ConveyorTwin/ (트윈 + Server/ 단일 exe, gitignore)
```

- DLL 복사는 `Twin/Assets`가 있을 때만 일어난다 (csproj의 `CopyToUnity` 타깃). 복사된 DLL은 `.gitignore` 대상이므로,
  Shared를 고친 뒤에는 반드시 다시 빌드해야 Unity가 새 규약을 본다.
- 테스트 프로젝트는 아직 없다.
- `src/UnityPlugins`는 OPC UA 클라이언트 DLL(1.5.378.x)과 의존 DLL도 `Twin/Assets/Plugins/OpcUa`로 복사한다 (Newtonsoft.Json은 Unity 패키지와 겹쳐 제외).
- `src/Spikes/`는 솔루션에 넣지 않은 버림용 스파이크다. 재현 절차는 `docs/opcua-spike.md`.
- 하위 프로젝트 생성 절차는 각 폴더 README에 있다: `Twin/README.md`(Unity 프로젝트 생성·패키지), `src/DummyServer/README.md`(4일 차 `dotnet new`/`sln add` 명령).

### CAD 변환 (STEP → glb)

```bash
"C:/Users/seong/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/export_conveyor.py   # cad/MK163596.stp → cad/conveyor.glb
"C:/Users/seong/AppData/Local/Programs/FreeCAD 1.1/bin/python.exe" -I tools/cad/inspect_glb.py cad/conveyor.glb   # 노드·삼각형·UV·크기 점검
```

- FreeCADCmd는 추가 인수를 열 파일로 해석하므로 스크립트에 인수를 넘기지 않는다 (경로는 스크립트 안에서 결정).
- glTF 내보내기는 형상에 저장된 삼각분할을 그대로 쓴다. `Shape.tessellate()`는 각도 허용치가 없어 8배가량 과분할되므로 `MeshPart.meshFromShape`로 미리 채운다.
- 출력 노드: `Conveyor/{Belt, Drum_Head, Drum_Tail, DriveMotor, ControlBox, Frame, Legs}`. 볼트·너트류 225개 제거 후 약 9.9만 삼각형.
  드럼 노드는 원점이 축 중심이라 그대로 회전시키면 된다. `EREG230AC`는 모터가 아니라 속도 조절기(제어 박스)다.
- glb 좌표: m 단위, Y-up, 반송 방향은 glTF +Z (아이들 드럼 z=-0.975 → 구동 드럼 z=+0.975). UV와 머티리얼은 없다.
- OCCT writer는 B-rep 면마다 primitive를 만들므로(약 4,000개) 스크립트가 메시별로 하나로 병합한 뒤
  `Twin/Assets/ThirdParty/Cad/conveyor.glb`로 복사한다 (gitignore 대상). Unity 쪽은 glTFast 기본 임포트만 쓴다.
- 트윈 씬은 메뉴 `Tools/Twin/Setup Scene`이 glb로 다시 구성한다 (`Assets/Scenes/Twin.unity`). 트윈 구조·추정 방식은 `Twin/README.md`.
  로봇 glb가 있으면 `RobotCellBuilder`가 로봇·그리퍼·팔레트도 놓는다. 배치모드: `-executeMethod ConveyorTwin.EditorTools.TwinSceneSetup.SetupBatch`.
- 셀 점검: `Tools/Twin/Check Robot Cell` (`RobotCellCheck.CheckBatch`) — 픽 위치 실측 비교, 48칸 IK 도달·가동 범위, TCP 오차, `dist/cellcheck/*.png` 캡처.
- Unity 점검: 메뉴 `Tools/CAD/Check Conveyor Import` (`Twin/Assets/Editor/CadImportCheck.cs`)가 서브메시·축·드럼 간 거리를 콘솔에 출력한다.
- **Unity 좌표 (2026-10-20 확인)**: 1유닛 = 1m, 반송 방향 = Unity **+Z** (Drum_Tail z=-0.975 → Drum_Head z=+0.975),
  벨트 윗면 y≈0.850, 드럼 축 y=0.823. glTFast가 X를 반전하므로 구동부·제어 박스는 +X 쪽에 있다.
  드럼은 로컬 X축 기준으로 회전시키며, 벨트 속도 v일 때 각속도 = v / 0.0275 rad/s (벨트 감은 외경 기준).

## 알려진 사항

- `src/Shared` 빌드 검증 완료 (2026-10-20, 경고·오류 0).
- 더미 서버의 가상 설비(`DevicePublisher`)는 브로커 내부 API가 아니라 **일반 MQTT 클라이언트로 접속**해 발행한다.
  그래야 Last Will이 실제로 동작한다 (8일 차 "연결 끊김" 장애). Will로 온 offline의 `ts`는 접속 시각이므로 트윈은 수신 시각을 쓴다.
- 서버 시뮬레이션은 물건 위치를 속도 적분으로 진행시키므로, 속도를 0으로 만들면 물건도 멈춘다 (모터 정지·물건 걸림 장애의 기반).
