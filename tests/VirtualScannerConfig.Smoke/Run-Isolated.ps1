param(
    [switch]$InteractionOnly,
    [string]$LogPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $LogPath) { $LogPath = Join-Path $root 'build\verify-ui-isolated.log' }
$LogPath = [IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Force -Path (Split-Path $LogPath -Parent) | Out-Null

dotnet build (Join-Path $PSScriptRoot 'VirtualScannerConfig.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Smoke project build failed.' }

# All test windows and native TWAIN hosts run on a separate, hidden desktop.
# Never switch desktops or inject input into the user's interactive desktop.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class MbfTwainIsolatedSmoke
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, string command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static int Run(string executable, string command, string directory)
    {
        string name = "mbfTwain-Smoke-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = name };
            ProcessInfo process;
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
                IntPtr.Zero, directory, ref startup, out process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (WaitForSingleObject(process.process, 0xffffffff) != 0)
                    throw new Win32Exception("Waiting for the isolated smoke process failed.");
                uint code;
                if (!GetExitCodeProcess(process.process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return (int)code;
            }
            finally { CloseHandle(process.thread); CloseHandle(process.process); }
        }
        finally { CloseDesktop(desktop); }
    }
}
'@

$smokeExe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\VirtualScannerConfig.Smoke.exe'
$smokeFlag = if ($InteractionOnly) { ' --interaction-only' } else { '' }
$quotedExe = $smokeExe.Replace("'", "''")
$quotedLog = $LogPath.Replace("'", "''")
$body = "& '$quotedExe'$smokeFlag *> '$quotedLog'; exit `$LASTEXITCODE"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
$powerShellExe = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $powerShellExe)) { $powerShellExe = Join-Path $PSHOME 'pwsh.exe' }
$command = '"' + $powerShellExe + '" -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $encoded
$result = [MbfTwainIsolatedSmoke]::Run($powerShellExe, $command, $root)
Get-Content -LiteralPath $LogPath
if ($result -ne 0) { throw "Isolated smoke failed with exit code $result. Log: $LogPath" }
