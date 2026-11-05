# Compila com o csc do .NET Framework 4 (já vem no Windows, sem precisar de SDK).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& $csc /nologo /codepage:65001 /target:winexe /optimize+ /out:"$out\RODamageMeter.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $root 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "falha na compilação" }

Copy-Item (Join-Path $root 'data\skills.tsv') $out -Force
Write-Host "OK: $out\RODamageMeter.exe"
