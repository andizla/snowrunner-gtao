# Builds out\SnowRunnerGTAO.exe: compiles the shader with fxc (Windows SDK), then the installer with the C# compiler
# that ships with Windows (.NET Framework 4.x). The compiled shader is embedded in the exe.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
$fxc = $null
if (Test-Path -LiteralPath $kits) {
  $fxc = Get-ChildItem -LiteralPath $kits -Directory | Where-Object { $_.Name -like '10.*' } | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\fxc.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $fxc) { throw 'fxc.exe not found. Install the Windows SDK (any Windows 10 or 11 version).' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe not found. It ships with the .NET Framework 4.x of Windows 10 and 11.' }

$cso = Join-Path $out 'gtao.cso'
& $fxc -nologo -T ps_5_0 -E main -Fo $cso (Join-Path $root 'src\shader\gtao.hlsl') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'shader compile failed' }

$exe = Join-Path $out 'SnowRunnerGTAO.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src\installer') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /warn:4 "/out:$exe" "/win32manifest:$(Join-Path $root 'src\installer\app.manifest')" "/resource:$cso,gtao.cso" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }
'{0}  {1:N0} bytes' -f $exe, (Get-Item -LiteralPath $exe).Length
