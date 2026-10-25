# Compiles DeskWidgets.exe with the C# compiler that ships with .NET Framework 4.8 (no SDK needed).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'DeskWidgets.exe'
$src = Join-Path $PSScriptRoot 'src\*.cs'

Get-Process DeskWidgets -ErrorAction SilentlyContinue | Stop-Process -Force
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $src
if ($LASTEXITCODE -eq 0) { Write-Host "OK -> $out" } else { exit $LASTEXITCODE }
