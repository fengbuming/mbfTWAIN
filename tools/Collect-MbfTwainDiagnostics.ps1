$ErrorActionPreference = 'Continue'

$report = Join-Path ([Environment]::GetFolderPath('Desktop')) (
    'mbfTwain-diagnostic-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date)
)
$lines = [System.Collections.Generic.List[string]]::new()
function Add-Line([string]$Text = '') { $script:lines.Add($Text) }

$powershellBitness = if ([Environment]::Is64BitProcess) { '64-bit' } else { '32-bit' }
Add-Line "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
Add-Line "Computer: $env:COMPUTERNAME"
Add-Line "PowerShell: $powershellBitness"
Add-Line "MBF_TWAIN_FORCE_UI (Machine): $([Environment]::GetEnvironmentVariable('MBF_TWAIN_FORCE_UI', 'Machine'))"

Add-Line "`r`n=== Installed data sources ==="
foreach ($path in @(
    "$env:windir\twain_32\mbfVirtualTwainDS.ds",
    "$env:windir\twain_64\mbfVirtualTwainDS.ds"
)) {
    if (Test-Path -LiteralPath $path) {
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Add-Line "Path: $path"
        Add-Line "Length: $($file.Length); Modified: $($file.LastWriteTime); FileVersion: $($file.VersionInfo.FileVersion)"
        Add-Line "SHA256: $hash"
    } else {
        Add-Line "MISSING: $path"
    }
}

$processes = Get-CimInstance Win32_Process
Add-Line "`r`n=== Candidate processes ==="
foreach ($process in $processes | Sort-Object ProcessId) {
    if ($process.Name -match 'scan|twain|mbf' -or $process.ExecutablePath -match 'scan|twain|mbf') {
        Add-Line "PID=$($process.ProcessId) Name=$($process.Name) Path=$($process.ExecutablePath)"
    }
}

Add-Line "`r`n=== Loaded TWAIN modules ==="
foreach ($process in $processes | Sort-Object ProcessId) {
    try {
        $modules = (Get-Process -Id $process.ProcessId -ErrorAction Stop).Modules |
            Where-Object { $_.ModuleName -match 'mbfVirtualTwainDS|twaindsm|twain' }
        foreach ($module in $modules) {
            Add-Line "PID=$($process.ProcessId) Process=$($process.Name) Module=$($module.ModuleName) Path=$($module.FileName)"
        }
    } catch {
        if ($process.Name -match 'scan|twain|mbf') {
            Add-Line "MODULE_READ_FAILED PID=$($process.ProcessId) Name=$($process.Name) Error=$($_.Exception.Message)"
        }
    }
}

Add-Line "`r`n=== Recent MBF diagnostics ==="
$log = Join-Path $env:TEMP 'mbfTwain-diagnostics.log'
if (Test-Path -LiteralPath $log) {
    Get-Content -LiteralPath $log -Tail 250 | ForEach-Object { Add-Line $_ }
} else {
    Add-Line "Not found: $log"
}

$lines | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host "Report saved to: $report"
Read-Host 'Press Enter to close this window'
