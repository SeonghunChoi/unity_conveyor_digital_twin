# AGENTS.md

This file provides guidance to Codex (claude.ai/code) when working with code in this repository.

## Unity Conveyor Digital Twin

이 파일은 Codex가 세션 시작 시 읽는 프로젝트 컨텍스트입니다.
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
| 더미 서버 | WPF (.NET 8) + MQTTnet 브로커 내장 + 데이터 발행 | `src/DummyServer` (4일 차 생성) |
| 트윈 | Unity 6 LTS, URP, Newtonsoft JSON, MQTTnet(netstandard 빌드) | `Twin` |
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
| 2~3 | mk STEP 받기, FreeCAD 변환, Unity 임포트·계층 정리 (2일 초과 시 타임박스) | ⏳ 다음 |
| 4~5 | WPF 서버 골격, 브로커 내장, 정상 데이터 발행, 로그 UI | |
| 6~7 | 트윈 MQTT 수신, 벨트 구동, 물건 생성·이동 | |
| 8~9 | 장애 버튼 + 트윈 대응 로직, 잼 판단 ★ | |
| 10 | 상태별 셰이더 하이라이트, 알람 | |
| 11~12 | HMI 대시보드, 연결 상태 표시, 서버 자동 실행 | |
| 13 | 단일 exe 배포, 클린 PC 실행 테스트 | |
| 14 | README, 아키텍처 구조도 | |
| 15 | 시연 대본, 영상 촬영·편집 | |

작업을 마치면 위 표의 상태를 갱신한다.

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
dotnet build                       # 솔루션 전체 (현재는 Shared만 포함)
```

- DLL 복사는 `Twin/Assets`가 있을 때만 일어난다 (csproj의 `CopyToUnity` 타깃). 복사된 DLL은 `.gitignore` 대상이므로,
  Shared를 고친 뒤에는 반드시 다시 빌드해야 Unity가 새 규약을 본다.
- 테스트 프로젝트는 아직 없다.
- 하위 프로젝트 생성 절차는 각 폴더 README에 있다: `Twin/README.md`(Unity 프로젝트 생성·패키지), `src/DummyServer/README.md`(4일 차 `dotnet new`/`sln add` 명령).

## 알려진 사항

- `src/Shared` 빌드 검증 완료 (2026-10-20, 경고·오류 0).
- 아직 존재하지 않는 것: `Twin`의 Unity 프로젝트(README만 있음), `src/DummyServer` 프로젝트, `docs/cad-pipeline.md`(2일 차 작성 예정).
- 벨트 길이(잼 판단 기준값)는 CAD 확정 후 `docs/protocol.md`에 기입.
