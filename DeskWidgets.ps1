# Runs DeskWidgets.dll inside Microsoft-signed powershell.exe. Smart App Control blocks our own unsigned exe,
# but loading the assembly from bytes runs it without the file ever being executed as an image.
# Usage: conhost --headless powershell -NoProfile -Sta -WindowStyle Hidden -ExecutionPolicy Bypass -File DeskWidgets.ps1
# Launched plainly it is a small supervisor: it runs the widgets in a child process (-Child) and starts them
# again if that process dies without the user choosing "Salir", logging how it ended.
param([switch]$Child)
$log = Join-Path $env:APPDATA 'DeskWidgets\error.log'
function Write-Log($text) {
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Add-Content $log "$(Get-Date -Format s) $text`r`n" -Encoding UTF8
}

if ($Child) {
    try {
        $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'DeskWidgets.dll')))
        [void]$asm.EntryPoint.Invoke($null, $null)
        exit 0
    } catch {
        # Hidden window: without this a startup failure (e.g. right after login) vanishes without a trace.
        $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
        Write-Log "pid $PID`r`n$e"
        exit 1
    }
}

# Exit code 0 = the user quit, or another instance was already running. Anything else is a crash or an
# outside kill; the code tells which (0xE0434352 .NET exception, 0xC0000005 access violation, 1 = see above).
$crashes = @()
while ($true) {
    $started = Get-Date
    # The child shares this process's headless console (install.ps1 starts us in conhost --headless). A new
    # console would land in a Windows Terminal tab, and closing that tab kills the widgets. Not wrapped in
    # another conhost either: conhost always exits with 0, hiding the child's exit code.
    $si = New-Object Diagnostics.ProcessStartInfo (Join-Path $PSHOME 'powershell.exe'), "-NoProfile -Sta -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Child"
    $si.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($si)
    $p.WaitForExit()
    if ($p.ExitCode -eq 0) { break }
    $mins = [int]((Get-Date) - $started).TotalMinutes
    Write-Log ("widgets ended after {0} min, exit code 0x{1:X8}; restarting" -f $mins, $p.ExitCode)
    # Give up if it keeps dying: 5 crashes within an hour means restarting will not help.
    $crashes = @($crashes | Where-Object { $_ -gt (Get-Date).AddHours(-1) }) + (Get-Date)
    if ($crashes.Count -ge 5) { Write-Log 'too many crashes in an hour; not restarting'; break }
    Start-Sleep -Seconds (15 * $crashes.Count)
}
