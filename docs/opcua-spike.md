# OPC UA 스파이크 (2단계 1일 차)

**질문**: Unity 6.3 배포 빌드(Mono, Windows x64)의 트윈이 OPC UA 서버에 접속해 구독할 수 있는가?

**답**: 된다. 단, Mono 런타임의 한계 두 가지를 우회해야 한다. (2026-10-23 측정)

## 구성

| 쪽 | 코드 | 스택 |
| --- | --- | --- |
| 서버 | `src/Spikes/OpcUaSpikeServer` (.NET 8 콘솔, 솔루션 미포함) | OPCFoundation.NetStandard.Opc.Ua.Server 1.5.378.182 |
| 클라이언트 | `Twin/Assets/Spikes/OpcUaSpike/` (빈 씬 + `OpcUaSpikeClient`) | Opc.Ua.Client 1.5.378.182 netstandard2.1 (`src/UnityPlugins`가 `Assets/Plugins/OpcUa`로 복사) |

- 서버는 `opc.tcp://127.0.0.1:4840`에 엔드포인트 4개(None, SignAndEncrypt × Basic256Sha256 / Aes128_Sha256_RsaOaep / Aes256_Sha256_RsaPss)를 열고,
  `RobotSpike/Joints`(double[6], 50ms 갱신)와 `RobotSpike/Counter`를 노출한다.
- 클라이언트는 정책별로 접속 → `Counter` 읽기 → `Joints` 구독 3초(샘플링 50ms, 발행 100ms)를 하고 결과를 로그에 남긴 뒤 종료 코드로 알린다.
- 빌드는 배포 빌드(`TwinBuild`)와 같은 Mono 백엔드, 관리 코드 스트리핑 Disabled.

### 재현

```bash
dotnet build src/UnityPlugins                                   # OPC UA DLL → Twin/Assets/Plugins/OpcUa
dotnet run --project src/Spikes/OpcUaSpikeServer                 # 서버 (Ctrl+C 종료)
# Twin을 에디터에서 닫은 상태에서
"C:/Program Files/Unity/Hub/Editor/6000.3.20f1/Editor/Unity.exe" -batchmode -quit -projectPath Twin -executeMethod ConveyorTwin.Spikes.EditorTools.OpcUaSpikeBuild.BuildBatch -logFile spike-build.log
# 클라이언트 인증서 발급 (Mono는 직접 못 만든다 — 아래 문제 1)
dotnet run --project src/Spikes/OpcUaSpikeServer -- provision-client "%USERPROFILE%\AppData\LocalLow\SeonghunChoi\ConveyorTwin\pki"
dist/OpcUaSpike/OpcUaSpike.exe -batchmode -nographics -logFile spike-run.log   # "[OpcUaSpike]" 줄 확인
```

## 결과 (Mono 6.13.0, 우회 적용 후)

| 정책 | 결과 | 접속 | 알림 (3초) | 마지막 값 지연 |
| --- | --- | --- | --- | --- |
| None | ✅ | 92ms | 58건 | 131ms |
| SignAndEncrypt / Basic256Sha256 | ✅ | 177ms | 58건 | 116ms |
| SignAndEncrypt / Aes128_Sha256_RsaOaep | ✅ | 73ms | 60건 | 45ms |
| SignAndEncrypt / Aes256_Sha256_RsaPss | ❌ | — | — | "Could not send an Open Secure Channel request" |

알림 수는 50ms 샘플링 × 3초 = 60건 기대치와 맞다. 지연은 발행 주기(100ms) 안쪽이다.

## 발견한 문제와 우회

### 1. Mono는 자체 서명 인증서를 만들지 못한다

`CheckApplicationInstanceCertificatesAsync`가 인증서를 새로 만들 때
`CertificateBuilder.CreateForRSA`에서 `PlatformNotSupportedException`이 난다 (Mono의 `CertificateRequest` 미지원).
이미 있는 인증서(`pki/own/certs/*.der` + `pki/own/private/*.pfx`)를 **읽고 쓰는 것은 된다**.

- 우회: .NET 8 쪽에서 클라이언트와 같은 설정(ApplicationUri·주체 이름·저장소 경로)으로 인증서를 만들어 트윈 저장소에 넣는다.
  스파이크 서버의 `provision-client <pkiRoot>` 모드가 이 일을 한다.
- 보안 없음(None)만 쓰면 인증서가 필요 없다.

### 2. Mono는 스택의 TCP 접속 방식을 지원하지 않는다

스택의 `TcpMessageSocket.ConnectAsync`는 주소 체계를 지정하지 않은 소켓(Mono에서 IPv6 + DualMode)으로 `DnsEndPoint`에 접속한다.
Mono에서는 이 조합이 호스트 표기와 관계없이 실패한다.

| DnsEndPoint 호스트 | Mono 결과 |
| --- | --- |
| `127.0.0.1`, `localhost`, `::1`, `[::1]` | 모두 `ArgumentException: None of the discovered or specified addresses match the socket address family` |

서버 주소를 IPv6로 바꿔도 해결되지 않는다 (측정함).

- 우회: `MonoTcpTransport` — `opc.tcp` 전송 바인딩을 교체한다. 주소를 먼저 풀고 **그 주소 체계로 소켓을 만들어 `IPEndPoint`로 접속**한 뒤,
  연결된 소켓을 받는 `TcpMessageSocket` 생성자(서버가 accept 소켓에 쓰는 것)에 넘겨 메시지 처리는 스택에 그대로 맡긴다.
  `TransportBindings.Channels`는 `ITransportChannelBindings`로 노출되지만 실제 구현이 `ITransportBindings<ITransportChannelFactory>`라서
  캐스팅해 `SetBinding`을 호출한다 (공개 API 범위 안).

### 3. RSA-PSS 정책은 쓸 수 없다

`Aes256_Sha256_RsaPss`는 보안 채널을 열지 못한다 (Mono의 RSA-PSS 미지원으로 추정). 서버가 다른 정책도 함께 열면 문제없다.

## 2단계 설계에 반영할 것

- 트윈 쪽 보안 정책은 **Basic256Sha256**(또는 Aes128_Sha256_RsaOaep)으로 고정한다. RsaPss는 쓰지 않는다.
- `MonoTcpTransport`는 5일 차에 `Twin/Assets/Scripts/Twin`으로 옮겨 정식 코드로 쓴다.
- **클라이언트 인증서 발급 주체를 정해야 한다** (3·5일 차). 후보:
  1. 더미 서버 exe에 발급 모드를 두고, 트윈의 `ServerLauncher`가 인증서가 없을 때 한 번 호출한다.
     현장에서 GDS(Global Discovery Server)가 인증서를 발급·배포하는 역할을 축소한 형태로 설명할 수 있다. ← 권장
  2. 배포 빌드 때 인증서를 만들어 동봉한다. 간단하지만 모든 배포본이 같은 개인 키를 공유한다.
  3. None만 쓴다. 가장 쉽지만 OPC UA를 쓰는 의미(보안 채널)가 약해진다.
- 스파이크는 `AutoAcceptUntrustedCertificates = true`로 서로의 인증서를 무조건 신뢰했다. 정식 구현은 신뢰 목록(`pki/trusted`)으로 관리한다.
- 구독을 지운 직후 `Publish ... BadNoSubscription` 오류 로그가 한 번 찍힌다. 이미 보낸 Publish 요청에 대한 응답이라 무해하며, 정식 코드에서는 이 로그를 거른다.

## 확인하지 못한 것

- IL2CPP 백엔드 (배포 빌드는 Mono를 쓰므로 범위 밖)
- 클린 PC(Windows 샌드박스) 실행 — 9일 차 배포 테스트에서 함께 확인
- 장시간 연결, 서버 재시작 후 재연결 (`SessionReconnectHandler`) — 5일 차
