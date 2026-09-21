# End to end tests of out\SnowRunnerGTAO.exe through its command line form, on scratch copies in the temp folder.
# The game's own shader.pak is never touched and no game file is stored in this repository: pass the path of an
# ORIGINAL (unpatched) shader.pak.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File test\run_tests.ps1 -OriginalPak "<game>\preload\paks\client\shader.pak"
param([Parameter(Mandatory = $true)][string]$OriginalPak)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$exe = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'out\SnowRunnerGTAO.exe'
$work = Join-Path $env:TEMP ('gtao-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$state = Join-Path $work 'state'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$pak = Join-Path $work 'shader.pak'
$log = Join-Path $work 'last.log'
$script:failed = 0

function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Run([string]$command, [string]$target) {
  $p = Start-Process -FilePath $exe -ArgumentList @('--cli', $command, ('"' + $target + '"'), '--state', ('"' + $state + '"'), '--log', ('"' + $log + '"')) -Wait -PassThru -WindowStyle Hidden
  [pscustomobject]@{ Code = $p.ExitCode; Line = (Get-Content -LiteralPath $log -Raw).Trim() }
}
function Check([string]$name, [bool]$ok, [string]$detail) {
  if ($ok) { "PASS  $name" } else { $script:failed++; "FAIL  $name   $detail" }
}
function Strays { @(Get-ChildItem -LiteralPath $work -File | Where-Object { $_.Name -like '*.gtao-tmp' }).Count }
function Backups { if (Test-Path -LiteralPath $state) { @(Get-ChildItem -LiteralPath $state -Filter 'backup-*.pak').Count } else { 0 } }

Copy-Item -LiteralPath $OriginalPak -Destination $pak
$original = Sha $pak

$r = Run 'status' $pak
Check 'original pak reads as not installed' ($r.Code -eq 0 -and $r.Line -eq 'state=NotInstalled') $r.Line

$r = Run 'install' $pak
$patched = Sha $pak
Check 'install succeeds and changes the pak' ($r.Code -eq 0 -and $r.Line -like 'result=Done*' -and $patched -ne $original) $r.Line
$backups = @(Get-ChildItem -LiteralPath $state -Filter 'backup-*.pak')
Check 'one backup, identical to the original' ($backups.Count -eq 1 -and (Sha $backups[0].FullName) -eq $original) "$($backups.Count) backups"
Check 'install list has one line' (@(Get-Content -LiteralPath (Join-Path $state 'installs.txt')).Count -eq 1) ''

# an independent zip reader must accept the result, and only the shader cache may differ
$za = [IO.Compression.ZipFile]::OpenRead($OriginalPak); $zb = [IO.Compression.ZipFile]::OpenRead($pak)
$sha = [Security.Cryptography.SHA256]::Create(); $differs = @()
foreach ($e in $za.Entries) {
  $o = $zb.GetEntry($e.FullName)
  $s1 = $e.Open(); $h1 = [BitConverter]::ToString($sha.ComputeHash($s1)); $s1.Dispose()
  $s2 = $o.Open(); $h2 = [BitConverter]::ToString($sha.ComputeHash($s2)); $s2.Dispose()
  if ($h1 -ne $h2) { $differs += ($e.FullName -split '\\')[-1] }
}
$count = $zb.Entries.Count; $za.Dispose(); $zb.Dispose()
Check 'zip reader opens the result, only the shader cache differs' ($count -eq 11 -and $differs.Count -eq 1 -and $differs[0] -eq 'shadercachedx11.sdc') ($differs -join ', ')
Check 'patched pak ends on a 4096 byte boundary like the original' (((Get-Item -LiteralPath $pak).Length % 4096) -eq 0) ''

$r = Run 'status' $pak
Check 'patched pak reads as installed' ($r.Line -eq 'state=Installed') $r.Line
$r = Run 'install' $pak
Check 'second install changes nothing' ($r.Code -eq 0 -and (Sha $pak) -eq $patched) $r.Line

$r = Run 'remove' $pak
Check 'remove restores the original byte for byte' ($r.Code -eq 0 -and (Sha $pak) -eq $original) $r.Line
Check 'remove deletes the used backup and its list line' ((Backups) -eq 0 -and @(Get-Content -LiteralPath (Join-Path $state 'installs.txt') | Where-Object { $_ }).Count -eq 0) ''
$r = Run 'remove' $pak
Check 'second remove changes nothing' ($r.Code -eq 0 -and $r.Line -like 'result=The mod is not installed*' -and (Sha $pak) -eq $original) $r.Line

# backup lost: remove must refuse and leave the patched file alone
$null = Run 'install' $pak
Remove-Item -LiteralPath $state -Recurse -Force
$before = Sha $pak
$r = Run 'remove' $pak
Check 'remove without a backup refuses and points to the store' ($r.Code -eq 1 -and $r.Line -like 'error=There is no backup*' -and (Sha $pak) -eq $before) $r.Line

# not a pak, and a cut off pak
$junk = Join-Path $work 'junk.pak'
[IO.File]::WriteAllBytes($junk, [byte[]](1..200000 | ForEach-Object { $_ % 251 }))
$before = Sha $junk
$r = Run 'install' $junk
Check 'a file that is no pak is refused untouched' ($r.Code -eq 1 -and (Sha $junk) -eq $before) $r.Line
$cut = Join-Path $work 'cut.pak'
$bytes = [IO.File]::ReadAllBytes($OriginalPak); [IO.File]::WriteAllBytes($cut, $bytes[0..9999999]); $bytes = $null
$before = Sha $cut
$r = Run 'install' $cut
Check 'a cut off pak is refused untouched' ($r.Code -eq 1 -and (Sha $cut) -eq $before) $r.Line

# read only target: the write fails, nothing may be left behind
Copy-Item -LiteralPath $OriginalPak -Destination $pak -Force
Set-ItemProperty -LiteralPath $pak -Name IsReadOnly -Value $true
$r = Run 'install' $pak
Check 'a write Windows refuses leaves the pak alone, no temp file, no backup' ($r.Code -eq 2 -and (Sha $pak) -eq $original -and (Strays) -eq 0 -and (Backups) -eq 0) $r.Line
Set-ItemProperty -LiteralPath $pak -Name IsReadOnly -Value $false

# game running: a copy of ping.exe under the game's process name stands in for it
$fake = Join-Path $work 'SnowRunner.exe'
Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\PING.EXE') -Destination $fake
$game = Start-Process -FilePath $fake -ArgumentList '-n 30 127.0.0.1' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$r = Run 'install' $pak
Check 'install refuses while the game runs' ($r.Code -eq 1 -and $r.Line -like 'error=SnowRunner is running*' -and (Sha $pak) -eq $original) $r.Line
Stop-Process -Id $game.Id -Force

Check 'no stray temp files' ((Strays) -eq 0) ''
Remove-Item -LiteralPath $work -Recurse -Force
if ($script:failed) { "`n$($script:failed) test(s) FAILED"; exit 1 } else { "`nall tests passed" }
