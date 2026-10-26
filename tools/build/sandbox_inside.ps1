# 샌드박스 안에서 실행되는 자동 클린 PC 테스트 (sandbox_test.ps1 -Auto가 로그온 명령으로 실행).
# 1. 트윈을 자체 점검 모드로 실행 (-selfcheck): 트윈이 서버를 자동 실행하고, OPC UA 인증서를 발급받아 접속한다
# 2. 로봇이 접속하면 서버 창의 로봇 셀 장애 버튼을 UI Automation으로 차례로 누른다
# 3. 트윈이 끝나면 done.txt를 남긴다. 결과는 모두 Desktop\results (호스트의 dist\sandbox-results)
param([int]$Seconds = 150)
$desk = "C:\Users\WDAGUtilityAccount\Desktop"
$app = Join-Path $desk "ConveyorTwin\ConveyorTwin.exe"
$res = Join-Path $desk "results"
$log = Join-Path $res "selfcheck.log"
$orch = Join-Path $res "orchestra.log"
function Log($m) { "$(Get-Date -Format HH:mm:ss.fff) $m" | Out-File -Append -Encoding utf8 $orch }

Log "샌드박스 시작: $([Environment]::OSVersion.VersionString), .NET 설치 여부: $(Test-Path 'C:\Program Files\dotnet')"
$twin = Start-Process $app -ArgumentList "-selfcheck $Seconds -selfcheckOut `"$res`" -screen-width 1600 -screen-height 900 -screen-fullscreen 0" -PassThru
Log "트윈 실행 pid $($twin.Id)"

# 로봇 접속 대기 (서버 자동 실행 + 인증서 발급 포함)
$connected = $false
for ($i = 0; $i -lt 240; $i++) {
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern '\[PlayState\] robot connected' -Quiet)) { $connected = $true; break }
    if ($twin.HasExited) { break }
    Start-Sleep -Milliseconds 500
}
Log "로봇 접속: $connected"

if ($connected) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Conveyor01 Dummy Server')
    $win = $null
    for ($i = 0; $i -lt 40 -and $win -eq $null; $i++) { $win = $root.FindFirst('Children', $cond); if ($win -eq $null) { Start-Sleep -Milliseconds 500 } }
    if ($win -eq $null) { Log "서버 창을 찾지 못함" }
    else {
        function Toggle($name) {
            $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
            $b = $win.FindFirst('Descendants', $c)
            if ($b -eq $null) { Log "버튼 없음: $name"; return }
            $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Log "토글: $name"
        }
        $t0 = Get-Date
        function At($s) { $w = $s - ((Get-Date) - $t0).TotalSeconds; if ($w -gt 0) { Start-Sleep -Milliseconds ([int]($w * 1000)) } }
        At 8;  Toggle '비상정지'
        At 14; Toggle '비상정지'
        At 22; Toggle '흡착 실패'
        At 36; Toggle '흡착 실패'
        At 44; Toggle '관절 엔코더 이상'
        At 49; Toggle '관절 엔코더 이상'
        At 54; Toggle '로봇 통신 끊김'
        At 64; Toggle '로봇 통신 끊김'
        Log "시나리오 끝"
    }
}

$twin.WaitForExit(($Seconds + 60) * 1000) | Out-Null
Log "트윈 종료 코드 $($twin.ExitCode)"
"exit=$($twin.ExitCode)" | Out-File -Encoding utf8 (Join-Path $res "done.txt")
