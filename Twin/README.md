# Twin (Unity 6 LTS, URP)

이 폴더에 Unity 6 LTS 프로젝트를 생성합니다 (Unity Hub → New project → Universal 3D → 위치를 이 `Twin` 폴더로 지정).

## 생성 직후 할 일

1. Package Manager → Add package by name → `com.unity.nuget.newtonsoft-json`
2. 저장소 루트에서 `dotnet build src/Shared` 실행 → `Assets/Plugins/Shared/ConveyorTwin.Shared.dll` 자동 복사
3. MQTTnet DLL(netstandard2.1 빌드)을 `Assets/Plugins/MQTTnet/`에 배치 (6일 차)
4. glTF 임포트용 `com.unity.cloud.gltfast` 설치 (2~3일 차)
