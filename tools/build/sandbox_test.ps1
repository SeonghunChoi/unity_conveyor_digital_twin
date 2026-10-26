# 클린 PC 실행 테스트: Windows 샌드박스에서 dist/ConveyorTwin 을 실행한다.
#
# 샌드박스는 .NET·Unity·VC++ 런타임 등 설치 프로그램이 없는 깨끗한 Windows라, 배포물이 혼자 도는지 확인하기에 맞다.
# - 빌드 폴더를 읽기 전용으로 바탕 화면에 연결 (배포물이 자기 폴더에 쓰지 않는지도 확인됨)
# - 네트워크 끔: 통신이 이 PC 안(127.0.0.1)에서만 이루어지는지 확인
#
# 사전 조건: Windows 기능 켜기/끄기 → "Windows 샌드박스" (Windows 10/11 Pro 이상, 관리자 권한·재부팅 필요)
# 실행 (저장소 루트):
#   powershell -ExecutionPolicy Bypass -File tools/build/sandbox_test.ps1          # 수동: 샌드박스 안에서 직접 실행
#   powershell -ExecutionPolicy Bypass -File tools/build/sandbox_test.ps1 -Auto    # 자동 (2단계 9일 차): 자체 점검 + 로봇 셀 장애 시나리오
#
# -Auto: 샌드박스 로그온 명령이 tools/build/sandbox_inside.ps1을 실행한다. 트윈을 -selfcheck 모드로 띄우고,
#        로봇이 접속하면 서버 창의 장애 버튼을 UI Automation으로 누른다. 결과(selfcheck.log, 스크린샷, orchestra.log)는
#        쓰기 가능한 dist/sandbox-results로 나오고, 끝나면(done.txt) 샌드박스를 닫는다.
param([switch]$Auto, [int]$Seconds = 150)
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

$desk = "C:\Users\WDAGUtilityAccount\Desktop"
$inside = "$desk\ConveyorTwin"
$results = Join-Path $repo "dist\sandbox-results"
$tools = Join-Path $repo "tools\build"

if ($Auto) {
    if (Test-Path $results) { Remove-Item -Recurse -Force $results }
    New-Item -ItemType Directory -Force $results | Out-Null
    $extra = @"
    <MappedFolder>
      <HostFolder>$tools</HostFolder>
      <SandboxFolder>$desk\tools</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$results</HostFolder>
      <SandboxFolder>$desk\results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
"@
    $logon = "powershell.exe -ExecutionPolicy Bypass -File $desk\tools\sandbox_inside.ps1 -Seconds $Seconds"
} else {
    $extra = ""
    $logon = "explorer.exe $inside"
}

$wsb = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$dist</HostFolder>
      <SandboxFolder>$inside</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
$extra  </MappedFolders>
  <Networking>Disable</Networking>
  <vGPU>Enable</vGPU>
  <LogonCommand>
    <Command>$logon</Command>
  </LogonCommand>
</Configuration>
"@
$path = Join-Path $env:TEMP "ConveyorTwin_sandbox.wsb"
Set-Content -Path $path -Value $wsb -Encoding UTF8
Write-Host "샌드박스 시작: $path"
Start-Process $path

if (-not $Auto) {
    Write-Host "샌드박스 안에서 열린 폴더의 ConveyorTwin.exe 를 실행하세요."
    return
}

# 자동 모드: 결과를 기다렸다가 샌드박스를 닫는다
$done = Join-Path $results "done.txt"
$deadline = (Get-Date).AddSeconds($Seconds + 420)   # 샌드박스 부팅 + 트윈 실행 여유
while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if (Test-Path $done) { Write-Host "완료: $(Get-Content $done)" } else { Write-Host "시간 초과: done.txt 없음" }
Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "결과: $results"
