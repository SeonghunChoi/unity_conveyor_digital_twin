# DummyServer (WPF, .NET 8) — 4일 차에 생성 예정

- MQTTnet 브로커 내장 + 데이터 발행
- 장애 버튼: 연결 끊김, 전송 지연, 순서 뒤섞기, 이상값, 물건 걸림, 모터 정지
- 실시간 송신 로그
- 단일 exe 배포: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`

생성 명령 (4일 차):
```
dotnet new wpf -n ConveyorTwin.DummyServer -o src/DummyServer -f net8.0
dotnet sln add src/DummyServer/ConveyorTwin.DummyServer.csproj
dotnet add src/DummyServer reference src/Shared/ConveyorTwin.Shared.csproj
dotnet add src/DummyServer package MQTTnet
dotnet add src/DummyServer package Newtonsoft.Json
```
