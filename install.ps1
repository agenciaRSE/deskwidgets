# Per-user install (no admin): builds, copies to %LOCALAPPDATA%\Programs\DeskWidgets,
# adds a Start menu shortcut, starts with Windows and launches it.
# Uninstall: .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'Programs\DeskWidgets'
$exe = Join-Path $dir 'DeskWidgets.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskWidgets.lnk'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process DeskWidgets -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty -Path $run -Name DeskWidgets -ErrorAction SilentlyContinue
    Remove-Item $lnk, $dir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'DeskWidgets desinstalado. Los ajustes siguen en %APPDATA%\DeskWidgets.'
    return
}

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }
# Smart App Control blocks unsigned executables, so never install one that is not signed.
& (Join-Path $PSScriptRoot 'sign.ps1')

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'DeskWidgets.exe') $exe -Force

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dir
$s.Description = 'Widgets de red y rendimiento'
$s.Save()

Set-ItemProperty -Path $run -Name DeskWidgets -Value "`"$exe`""
Start-Process $exe
Write-Host "Instalado en $exe (inicia con Windows)."
