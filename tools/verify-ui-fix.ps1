# Targeted verification for the UI pipe root-cause fix.
# Exercises: (A) cross-elevation DACL connect, (B) duplicate-instance auto exit,
# (C) session-end auto exit, (D) idle-timeout auto exit.
param(
    [string]$UiExe = "E:\Project\mbfTwain\build\manual\x64\Release\mbfTwain.VirtualScannerConfig.exe",
    [int]$IdleMinutes = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$Failures = 0

function Write-Step([string]$Message) { Write-Host ""; Write-Host "== $Message ==" }

function Send-PipeCommand([string]$Command, [int]$TimeoutMs = 4000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".", "mbfTwain.VirtualScanner.v1",
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    try {
        $client.Connect($TimeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, [System.Text.UTF8Encoding]::new($false))
        $writer.WriteLine($Command)
        $lines = @()
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $lines += $line
            if ($line -eq "END" -or $line.StartsWith("OK ") -or $line.StartsWith("ERR ")) {
                if ($Command -notlike "GET_STATE*") { break }
                if ($line -eq "END") { break }
            }
        }
        return ($lines -join "|")
    } catch {
        return "ERROR: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    } finally {
        $client.Dispose()
    }
}

function Start-Ui([string]$Exe, [hashtable]$Env = @{}) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }
    return [System.Diagnostics.Process]::Start($psi)
}

function Get-UiProcesses {
    return @(Get-Process -Name "mbfTwain.VirtualScannerConfig" -ErrorAction SilentlyContinue)
}

function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "PASS: $Message" }
    else { Write-Host "FAIL: $Message"; $script:Failures++ }
}

# ---- pre-clean ----
Write-Step "Pre-clean stale UI processes"
Get-UiProcesses | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

# ---- A: cross-elevation DACL connect (non-elevated client -> UI) ----
Write-Step "A. Non-elevated PING through the pipe (DACL / integrity label)"
$uiA = Start-Ui $UiExe
Start-Sleep -Seconds 2
Assert-True (-not $uiA.HasExited) "UI instance started"
$ping = Send-PipeCommand "PING"
Assert-True ($ping -like "OK PONG*") "PING accepted from non-elevated process (got: $ping)"
$state = Send-PipeCommand "GET_STATE"
Assert-True ($state -like "OK STATE|revision *") "GET_STATE accepted from non-elevated process (got: $state)"
Write-Host "state=$state"

# ---- B: duplicate instance auto exit ----
Write-Step "B. Second UI instance must exit (pipe name in use)"
$uiB = Start-Ui $UiExe
Start-Sleep -Seconds 4
$uiB.Refresh()
Assert-True ($uiB.HasExited) "Second UI instance exited by itself"
if (-not $uiB.HasExited) { Stop-Process -Id $uiB.Id -Force -ErrorAction SilentlyContinue }

# ---- C: session-end auto exit (HIDE_SCAN_UI 0) ----
Write-Step "C. UI exits after HIDE_SCAN_UI 0 (session end)"
$uiA.Refresh()
Assert-True (-not $uiA.HasExited) "First UI still running before session-end test"
$begin = Send-PipeCommand "BEGIN_SCAN duplex=0 pixel=GRAY paper=A4 xres=150 yres=150"
Assert-True ($begin -like "OK BEGIN_SCAN*") "BEGIN_SCAN accepted (got: $begin)"
Start-Sleep -Milliseconds 500
$hide = Send-PipeCommand "HIDE_SCAN_UI 0"
Assert-True ($hide -like "OK HIDE*") "HIDE_SCAN_UI accepted (got: $hide)"
$deadline = (Get-Date).AddSeconds(8)
while ((Get-Date) -lt $deadline) {
    $uiA.Refresh()
    if ($uiA.HasExited) { break }
    Start-Sleep -Milliseconds 200
}
Assert-True ($uiA.HasExited) "UI exited within 8s after session end"
if (-not $uiA.HasExited) { Stop-Process -Id $uiA.Id -Force -ErrorAction SilentlyContinue }

# ---- D: idle timeout auto exit ----
Write-Step "D. UI exits after idle timeout ($IdleMinutes min)"
$uiD = Start-Ui $UiExe @{ "MBF_TWAIN_UI_IDLE_TIMEOUT_MINUTES" = "$IdleMinutes" }
Start-Sleep -Seconds 2
Assert-True (-not $uiD.HasExited) "Idle-test UI started"
$deadline = (Get-Date).AddMinutes($IdleMinutes).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $uiD.Refresh()
    if ($uiD.HasExited) { break }
    Start-Sleep -Seconds 2
}
Assert-True ($uiD.HasExited) "UI exited after idle timeout"
if (-not $uiD.HasExited) { Stop-Process -Id $uiD.Id -Force -ErrorAction SilentlyContinue }

# ---- summary ----
Write-Step "Result"
if ($Failures -eq 0) { Write-Host "ALL PASS" } else { Write-Host "$Failures FAILED" }
exit $Failures
