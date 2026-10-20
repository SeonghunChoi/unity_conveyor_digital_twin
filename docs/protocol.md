# 통신 규약 (MQTT 토픽 · 페이로드)

더미 서버(발행자)와 트윈(구독자)이 지키는 데이터 계약입니다.
코드 정의는 `src/Shared`에 있으며, 이 문서와 코드가 다르면 코드를 기준으로 하고 문서를 고칩니다.

## 1. 기본 원칙

- **서버는 원시 신호만 보낸다.** "잼 발생" 같은 판단은 보내지 않는다. 판단은 트윈의 몫이다.
- **트윈은 데이터를 믿고 따라가되 맹신하지 않는다.** 순서, 중복, 이상값, 끊김을 스스로 검증한다.
- 브로커는 더미 서버 프로세스에 내장한다. 기본 주소 `127.0.0.1:1883`.

## 2. 토픽 구조

형식: `dt/{deviceId}/{category}/{signal}` (기본 deviceId = `conveyor01`)

| 토픽 | 내용 | 메시지 타입 | 주기 | QoS | Retained |
| --- | --- | --- | --- | --- | --- |
| `dt/conveyor01/status` | 연결 상태 (Last Will 대상) | StatusMessage | 변경 시 | 1 | O |
| `dt/conveyor01/state/run` | 가동 상태 | RunStateMessage | 변경 시 | 1 | O |
| `dt/conveyor01/telemetry/belt_speed` | 벨트 속도 (m/s) | TelemetryMessage | 500ms | 0 | O |
| `dt/conveyor01/telemetry/motor_temp` | 모터 온도 (degC) | TelemetryMessage | 1000ms | 0 | O |
| `dt/conveyor01/telemetry/motor_current` | 모터 전류 (A) | TelemetryMessage | 500ms | 0 | O |
| `dt/conveyor01/telemetry/load_weight` | 입구 로드셀 무게 (kg) | TelemetryMessage | 물건 통과 시 | 1 | X |
| `dt/conveyor01/sensor/entry` | 입구 광전 센서 엣지 | SensorEdgeMessage | 엣지 발생 시 | 1 | X |
| `dt/conveyor01/sensor/exit` | 출구 광전 센서 엣지 | SensorEdgeMessage | 엣지 발생 시 | 1 | X |

트윈 구독: `dt/conveyor01/#`

### QoS · Retained 선택 이유

- **주기 측정값은 QoS 0 + Retained.** 곧 다음 값이 오므로 하나쯤 잃어도 되고, 재접속 시 마지막 값으로 즉시 화면을 복원한다.
- **상태·이벤트는 QoS 1.** 가동 상태 변화와 센서 엣지는 하나라도 놓치면 물건 추적과 잼 판단이 틀어진다. 중복 수신 가능성은 seq로 걸러낸다.
- **센서 엣지는 Retained 하지 않는다.** 과거 엣지가 재접속 시 다시 오면 물건이 하나 더 생긴 것처럼 보이기 때문이다.

## 3. 페이로드

JSON, 필드명 camelCase, enum은 소문자 문자열.

### 공통 헤더 (Envelope)

| 필드 | 타입 | 설명 |
| --- | --- | --- |
| `sid` | string | 발행자 세션 ID. 서버를 재시작하면 바뀐다 |
| `seq` | long | 토픽별 순번. 세션 내에서 1부터 1씩 증가 |
| `ts` | long | 발행 시각, Unix epoch 밀리초 |

### TelemetryMessage

```json
{ "sid": "a1b2c3", "seq": 1024, "ts": 1792886400123, "value": 0.50, "unit": "m/s", "quality": "good" }
```

`quality`: `good` | `uncertain` | `bad` (센서 자체가 판단한 신뢰도. OPC UA Quality 개념의 단순화)

### SensorEdgeMessage

```json
{ "sid": "a1b2c3", "seq": 57, "ts": 1792886400456, "blocked": true }
```

`blocked: true` = 물체가 빛을 가리기 시작(감지), `false` = 물체가 빠져나감.
광전 센서는 물건의 ID를 모른다. 트윈은 입구·출구 엣지의 **순서(FIFO)** 로 물건을 추적한다.

### RunStateMessage

```json
{ "sid": "a1b2c3", "seq": 3, "ts": 1792886400000, "state": "running", "reason": null }
```

`state`: `stopped` | `running` | `fault`

### StatusMessage

```json
{ "online": true, "sid": "a1b2c3", "ts": 1792886399000 }
```

서버는 접속할 때 `online: true`를 retained로 발행하고, 같은 토픽에 `online: false`를 Last Will로 등록한다.

## 4. 트윈 쪽 수신 규칙

| 규칙 | 내용 |
| --- | --- |
| 세션 전환 | `status`의 `sid`가 바뀌면 모든 토픽의 seq 기록을 초기화한다 |
| 과거·중복 폐기 | 같은 sid에서 `seq <= 마지막 처리 seq`면 버린다 |
| 순서 정렬 | 센서 엣지는 짧은 재정렬 버퍼(기본 300ms)에 모았다가 seq 순으로 처리한다 |
| 이상값 판정 | `SignalSpec`의 유효 범위를 벗어나거나 `quality`가 `bad`면 표시값에 반영하지 않고 경고를 띄운다 |
| 끊김(stale) 판정 | 주기 신호가 주기의 3배 동안 오지 않으면 해당 값을 stale로 표시한다 |
| 연결 끊김 | `status`에서 `online: false`를 받으면 전체를 오프라인 상태로 전환한다 |
| 지연 측정 | `수신 시각 - ts`를 HMI에 표시한다 (같은 PC라 시계가 일치) |
| 잼 판단 | 입구 감지 후 `벨트 길이 / 벨트 속도 × 1.5`초 안에 출구 감지가 없으면 잼으로 판단한다 (벨트 길이 = 1.95m, 6절) |

## 5. 장애 버튼과 기대 동작

| 서버 장애 버튼 | 서버가 하는 일 | 트윈의 기대 동작 |
| --- | --- | --- |
| 연결 끊김 | 클라이언트 연결을 비정상 종료 → 브로커가 Last Will 발행 | 오프라인 표시, 재연결 후 retained로 상태 복원 |
| 전송 지연 | 발행을 N초간 지연 | 지연 시간 표시, stale 표시 |
| 순서 뒤섞기 | 일부 메시지의 발행 순서를 바꿈 | 재정렬 버퍼로 seq 순 처리, 과거 메시지 폐기 |
| 이상값 송신 | 범위 밖 값 또는 `quality: bad` 발행 | 표시값 유지 + 이상값 경고 |
| 물건 걸림 | 입구 엣지 후 출구 엣지를 보내지 않음, 모터 전류 상승 | 잼 판단, 해당 물건과 벨트 하이라이트 |
| 모터 정지 | `state/run`을 `fault`로, 속도 0으로 발행 | 벨트 정지, 알람 발생 |

## 6. 설비 치수 (mk GUF-P 2000 CAD 실측)

| 항목 | 값 | 비고 |
| --- | --- | --- |
| 벨트 길이 | **1.95m** | 구동 드럼과 아이들 드럼의 중심 간 거리. 잼 판단 기준값 |
| 센서 위치 | 양쪽 드럼 축 위 | 입구 = 아이들 드럼(텐션 쪽), 출구 = 구동 드럼. 센서 간 거리 = 벨트 길이 |
| 벨트 폭 | 0.29m | |
| 드럼 지름 | 약 53mm | 벨트를 감은 외경 약 55mm |
| 벨트 윗면 높이 | 약 0.85m | 바닥 기준 |
| 반송 방향 | 아이들 드럼 → 구동 드럼 | 구동 드럼이 벨트 윗면을 당기는 일반 배치 |

예: 정상 속도 0.5m/s에서 잼 판단 시간 = 1.95 / 0.5 × 1.5 = 5.85초.
센서를 드럼 안쪽으로 옮기면 4절의 "벨트 길이"를 "센서 간 거리"로 바꾸고 이 표를 함께 고친다.

## 7. 미정 사항

- 정상 운전 기준값(속도 0.5m/s, 모터 온도 40~60도 등)은 서버 구현 시 조정
