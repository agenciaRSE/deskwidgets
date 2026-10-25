# Compiles DeskWidgets.dll with the C# compiler that ships with .NET Framework 4.8 (no SDK needed).
# It is a WinForms app; DeskWidgets.ps1 loads and runs it, because Smart App Control blocks unsigned exes.
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'DeskWidgets.dll'
$src = Join-Path $PSScriptRoot 'src\*.cs'

& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $src
if ($LASTEXITCODE -eq 0) { Write-Host "OK -> $out" } else { exit $LASTEXITCODE }
