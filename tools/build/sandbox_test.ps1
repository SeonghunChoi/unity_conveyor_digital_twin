# 클린 PC 실행 테스트: Windows 샌드박스에서 dist/ConveyorTwin 을 실행한다.
#
# 샌드박스는 .NET·Unity·VC++ 런타임 등 설치 프로그램이 없는 깨끗한 Windows라, 배포물이 혼자 도는지 확인하기에 맞다.
# - 빌드 폴더를 읽기 전용으로 바탕 화면에 연결 (배포물이 자기 폴더에 쓰지 않는지도 확인됨)
# - 네트워크 끔: 통신이 이 PC 안(127.0.0.1)에서만 이루어지는지 확인
#
# 사전 조건: Windows 기능 켜기/끄기 → "Windows 샌드박스" (Windows 10/11 Pro 이상, 관리자 권한·재부팅 필요)
# 실행 (저장소 루트):  powershell -ExecutionPolicy Bypass -File tools/build/sandbox_test.ps1
$ErrorActionPreference = "Stop"

$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dist = Join-Path $repo "dist\ConveyorTwin"
if (-not (Test-Path (Join-Path $dist "ConveyorTwin.exe"))) {
    throw "빌드가 없습니다: $dist (Unity 메뉴 Tools/Twin/Build Release 먼저 실행)"
}
$sandbox = Join-Path $env:WINDIR "System32\WindowsSandbox.exe"
if (-not (Test-Path $sandbox)) {
    throw "Windows 샌드박스가 꺼져 있습니다. 'Windows 기능 켜기/끄기'에서 'Windows 샌드박스'를 켜고 재부팅하세요."
}

$inside = "C:\Users\WDAGUtilityAccount\Desktop\ConveyorTwin"
$wsb = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$dist</HostFolder>
      <SandboxFolder>$inside</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <Networking>Disable</Networking>
  <vGPU>Enable</vGPU>
  <LogonCommand>
    <Command>explorer.exe $inside</Command>
  </LogonCommand>
</Configuration>
"@
$path = Join-Path $env:TEMP "ConveyorTwin_sandbox.wsb"
Set-Content -Path $path -Value $wsb -Encoding UTF8
Write-Host "샌드박스 시작: $path"
Write-Host "샌드박스 안에서 열린 폴더의 ConveyorTwin.exe 를 실행하세요."
Start-Process $path
