# Twin (Unity 6 LTS, URP)

Unity 6000.3.20f1 (Unity 6.3 LTS), Universal 3D 템플릿.

## 새로 받은 PC에서 여는 순서

1. 저장소 루트에서 `dotnet build src` 실행. 아래 두 DLL이 `Assets/Plugins/`로 복사된다 (둘 다 빌드 산출물이라 커밋하지 않음).
   - `Plugins/Shared/ConveyorTwin.Shared.dll`: 토픽·페이로드 규약 (`src/Shared`)
   - `Plugins/MQTTnet/MQTTnet.dll`: MQTTnet 4.3.7 netstandard2.1 (`src/UnityPlugins`가 NuGet에서 받음)
2. CAD glb 생성: `tools/cad/export_conveyor.py` ([docs/cad-pipeline.md](../docs/cad-pipeline.md)). `Assets/ThirdParty/Cad/conveyor.glb`로 복사된다.
3. Unity로 이 폴더를 열고 메뉴 `Tools/Twin/Setup Scene` 실행 → `Assets/Scenes/Twin.unity` 생성.
4. 더미 서버 실행 (`dotnet run --project src/DummyServer` 또는 VS에서 F5) 후 Play.

패키지: `com.unity.cloud.gltfast`, `com.unity.nuget.newtonsoft-json` (manifest.json에 기록됨).

## 스크립트 구성 (`Assets/Scripts/Twin`)

| 파일 | 역할 |
| --- | --- |
| `MqttLink.cs` | 브로커 접속·구독, 1초 간격 재접속. 수신은 스레드 풀에서 큐에만 넣는다 |
| `MessageGate.cs` | sid 변경 감지(서버 재시작), 같은 세션의 중복·과거 seq 폐기 |
| `ConveyorTwinController.cs` | 메인 스레드에서 메시지 해석, 측정값 보관, 드럼 회전, 물건 배치 |
| `ItemTracker.cs` | 센서 엣지 FIFO로 물건 추적, 위치 추정과 출구 보정 |
| `TwinHud.cs` | 검증용 텍스트 HUD (수신값, 경과 시간, 물건 수, 출구 보정량, 경고) |

## 트윈이 하는 추정

트윈은 값을 만들지 않는다. 신호 사이를 채워야 하는 값만 받은 신호로 추정하고, 다음 신호로 보정한다.

| 추정 | 근거 신호 | 보정 |
| --- | --- | --- |
| 물건 위치 | 입구 감지 시각 + 벨트 속도 적분 | 출구 감지 시 앞단을 출구 센서 위치로 맞춤 (HUD "출구보정") |
| 물건 길이 | 입구 센서가 가려진 시간 × 속도 | - |
| 드럼 회전 | 벨트 속도 / 벨트 외경 반지름(0.0275m) | - |

- 출구 감지 전의 물건은 출구 센서 앞에서 기다린다. 확인되지 않은 이동을 지어내지 않기 위해서이며, 8일 차 잼 판단의 기반이 된다.
- 설비 offline이거나 브로커 연결이 끊기면 속도를 알 수 없으므로 애니메이션을 멈춘다.
- 서버 sid가 바뀌면 이전 세션의 물건 추적을 모두 지운다.
- 벨트 표면은 UV가 없어 아직 움직임을 표현하지 않는다 (10일 차 셰이더에서 오브젝트 공간 기준으로 처리).

## 좌표

반송 방향 = Unity +Z (입구 센서 = `Drum_Tail`, 출구 센서 = `Drum_Head`). 센서 위치와 방향은 CAD 드럼 위치에서 런타임에 읽는다.
