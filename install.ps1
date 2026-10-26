# Per-user install (no admin): builds, copies to %LOCALAPPDATA%\Programs\DeskWidgets,
# adds a Start menu shortcut, starts with Windows and launches it.
# Uninstall: .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'Programs\DeskWidgets'
$lnks = 'Programs', 'Desktop' | ForEach-Object { Join-Path ([Environment]::GetFolderPath($_)) 'DeskWidgets.lnk' }
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$psArgs = "-NoProfile -Sta -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$dir\DeskWidgets.ps1`""

# The app runs inside powershell.exe, so find it by the launcher in its command line.
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
    Where-Object { $_.CommandLine -like '*DeskWidgets.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty -Path $run -Name DeskWidgets -ErrorAction SilentlyContinue
    Remove-Item ($lnks + $dir) -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'DeskWidgets desinstalado. Los ajustes siguen en %APPDATA%\DeskWidgets.'
    return
}

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'DeskWidgets.dll'), (Join-Path $PSScriptRoot 'DeskWidgets.ps1') $dir -Force

# Shortcut icon, drawn by the app itself (loaded from bytes, like DeskWidgets.ps1 does).
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $dir 'DeskWidgets.dll')))
$asm.GetType('Program').GetMethod('SaveIcon').Invoke($null, [object[]]@([string](Join-Path $dir 'DeskWidgets.ico')))

# Start menu and desktop. Opening either while it runs brings hidden widgets back.
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in $lnks) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $ps
    $s.Arguments = $psArgs
    $s.WorkingDirectory = $dir
    $s.WindowStyle = 7
    $s.IconLocation = Join-Path $dir 'DeskWidgets.ico'
    $s.Description = 'Widgets de red, rendimiento y Claude'
    $s.Save()
}

Set-ItemProperty -Path $run -Name DeskWidgets -Value "`"$ps`" $psArgs"
Start-Process $ps $psArgs -WindowStyle Hidden
Write-Host "Instalado en $dir (inicia con Windows)."
