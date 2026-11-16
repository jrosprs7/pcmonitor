# End-to-end auto-update test. Runs an older build (stamped for self-update) and checks that it
# replaces itself with the latest GitHub release, the old process exits, the new one keeps running,
# and the leftover .old file is removed.
param(
  [Parameter(Mandatory)] [string] $Exe,             # the old build to start
  [Parameter(Mandatory)] [string] $ExpectedVersion  # e.g. 1.0.2
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$before = (Get-Item $Exe).VersionInfo.FileVersion
Write-Host "Starting $Exe (version $before); expecting it to update itself to $ExpectedVersion."
$old = Start-Process $Exe -PassThru

$deadline = (Get-Date).AddMinutes(5)
do {
  Start-Sleep -Seconds 5
  $ver = (Get-Item $Exe).VersionInfo.FileVersion
} until ($ver -like "$ExpectedVersion*" -or (Get-Date) -gt $deadline)
Write-Host "File version is now $ver"

# Give the hand-over and the clean-up time to finish.
$dir = Split-Path $Exe
$leftoverFiles = @("$Exe.old", "$Exe.new", (Join-Path $dir 'TaskbarMonitor.update.exe'))
$settle = (Get-Date).AddSeconds(90)
do {
  Start-Sleep -Seconds 5
  $oldAlive = [bool](Get-Process -Id $old.Id -ErrorAction SilentlyContinue)
  $leftover = @($leftoverFiles | Where-Object { Test-Path $_ })
} while (($oldAlive -or $leftover) -and (Get-Date) -lt $settle)
Start-Sleep -Seconds 3

$procs = @(Get-Process TaskbarMonitor -ErrorAction SilentlyContinue)
$procs | Format-Table Id, StartTime, Path -AutoSize | Out-String | Write-Host
$new = $procs | Where-Object { $_.Id -ne $old.Id -and $_.Path -eq $Exe }

$log = Join-Path $env:LOCALAPPDATA 'TaskbarMonitor\TaskbarMonitor.log'
if (Test-Path $log) { Write-Host "---- app log ----"; Get-Content $log | Write-Host; Write-Host "-----------------" }

$procs | Stop-Process -Force -ErrorAction SilentlyContinue

$errors = @()
if ($ver -notlike "$ExpectedVersion*") { $errors += "exe is still version $ver" }
if ($oldAlive) { $errors += "the old copy (pid $($old.Id)) is still running" }
if (-not $new) { $errors += "the new version is not running" }
if ($leftover) { $errors += "leftover files: $($leftover -join ', ')" }
if ($errors) { throw "Update test failed: " + ($errors -join '; ') }
Write-Host "Update test passed: $before -> $ver, old copy exited, new copy running, no leftovers."
