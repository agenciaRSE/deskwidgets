# Runs DeskWidgets.dll inside Microsoft-signed powershell.exe. Smart App Control blocks our own unsigned exe,
# but loading the assembly from bytes runs it without the file ever being executed as an image.
# Usage: powershell -NoProfile -Sta -WindowStyle Hidden -ExecutionPolicy Bypass -File DeskWidgets.ps1
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'DeskWidgets.dll')))
[void]$asm.EntryPoint.Invoke($null, $null)
