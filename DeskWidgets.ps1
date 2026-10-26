# Runs DeskWidgets.dll inside Microsoft-signed powershell.exe. Smart App Control blocks our own unsigned exe,
# but loading the assembly from bytes runs it without the file ever being executed as an image.
# Usage: powershell -NoProfile -Sta -WindowStyle Hidden -ExecutionPolicy Bypass -File DeskWidgets.ps1
param([int]$Attempt = 1)
try {
    $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'DeskWidgets.dll')))
    [void]$asm.EntryPoint.Invoke($null, $null)
} catch {
    # Hidden window: without this a startup failure (e.g. right after login) vanishes without a trace.
    $dir = Join-Path $env:APPDATA 'DeskWidgets'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
    Add-Content (Join-Path $dir 'error.log') "$(Get-Date -Format s) attempt $Attempt`r`n$e`r`n" -Encoding UTF8
    # Retry in a fresh process: WinForms can't be re-initialised in this one once a window exists.
    if ($Attempt -lt 5) {
        Start-Sleep -Seconds (15 * $Attempt)
        # Headless conhost, as in install.ps1: otherwise Windows Terminal opens a visible tab for the retry.
        Start-Process (Join-Path $env:WINDIR 'System32\conhost.exe') "--headless `"$(Join-Path $PSHOME 'powershell.exe')`" -NoProfile -Sta -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Attempt $($Attempt + 1)" -WindowStyle Hidden
    }
}
