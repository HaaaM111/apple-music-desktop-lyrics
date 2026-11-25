[CmdletBinding()]
param(
 [Parameter(Mandatory = $true)][string]$BuildDirectory,
 [int]$SoakSeconds = 300,
 [int]$WarmupSeconds = 5
)

$ErrorActionPreference = 'Stop'
if ($SoakSeconds -lt 5) { throw 'SoakSeconds must be at least 5.' }
if ($WarmupSeconds -lt 1) { throw 'WarmupSeconds must be at least 1.' }

$build = Get-Item -LiteralPath $BuildDirectory
if (-not $build.PSIsContainer) { throw 'BuildDirectory must be a directory.' }
$hostExe = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyrics.exe')
$watchExe = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyricsWatch.exe')
$launcherExe = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyricsStandby.exe')
$mediaScript = Get-Item -LiteralPath (Join-Path $build.FullName 'MediaSession.ps1')

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework C# compiler is not available.' }

$workspace = Split-Path -Parent $PSScriptRoot
$monitorSource = Join-Path $workspace 'src\AppleMusicMonitor.cs'
$unitSource = Join-Path $workspace 'tests\LifecycleTests.cs'
foreach ($requiredFile in @($monitorSource, $unitSource)) {
 if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
  throw "Required file not found: $requiredFile"
 }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runDir = Join-Path $workspace ('work\qa-soak-' + $timestamp)
$unitDir = Join-Path $runDir 'unit'
$standbyDir = Join-Path $runDir 'standby'
$realStateDir = Join-Path $runDir 'real-state'
foreach ($dir in @($runDir, $unitDir, $standbyDir, $realStateDir)) {
 $null = New-Item -ItemType Directory -Path $dir
}
$reportPath = Join-Path $runDir 'standby-soak-report.txt'
$soakScope = 'soak-' + [guid]::NewGuid().ToString('N')
$realScope = 'real-' + [guid]::NewGuid().ToString('N')

$realSettingsPath = Join-Path $workspace 'work\state\settings.json'
$realSettingsExistedBefore = Test-Path -LiteralPath $realSettingsPath -PathType Leaf
$realSettingsHashBefore = if ($realSettingsExistedBefore) { (Get-FileHash -Algorithm SHA256 -LiteralPath $realSettingsPath).Hash } else { $null }

$runRegPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'AMDesktopLyricsStandby'
function Get-StandbyRunEntry {
 if (-not (Test-Path -LiteralPath $runRegPath)) { return $null }
 $item = Get-ItemProperty -LiteralPath $runRegPath -Name $runValueName -ErrorAction SilentlyContinue
 if ($null -eq $item) { return $null }
 return $item.$runValueName
}
$standbyRunBefore = Get-StandbyRunEntry

$script:reportLines = New-Object 'System.Collections.Generic.List[string]'
function Write-Report([string]$Line) {
 $script:reportLines.Add($Line)
 Write-Output $Line
}
function Assert-True([bool]$Condition, [string]$Message) {
 if ($Condition) {
  Write-Report ('PASS ' + $Message)
  return
 }
 Write-Report ('FAIL ' + $Message)
 throw $Message
}

function Invoke-NativeCaptured([scriptblock]$Action) {
 $prevEap = $ErrorActionPreference
 $ErrorActionPreference = 'Continue'
 try {
  $output = & $Action 2>&1
  $exitCode = $LASTEXITCODE
  return [pscustomobject]@{
   ExitCode = $exitCode
   Output   = @($output | ForEach-Object { $_.ToString() })
  }
 } finally {
  $ErrorActionPreference = $prevEap
 }
}

function Get-ScopedProcessInfo {
 $filter = "Name = 'AppleMusicLyrics.exe' OR Name = 'AppleMusicLyricsWatch.exe' OR Name = 'powershell.exe'"
 return @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue)
}

function Get-ScopedMatches([string]$ProcessName, [string]$ScopeValue) {
 $all = Get-ScopedProcessInfo
 return @($all | Where-Object {
  $_.Name -ieq $ProcessName -and
  $null -ne $_.CommandLine -and
  $_.CommandLine.IndexOf($ScopeValue, [StringComparison]::OrdinalIgnoreCase) -ge 0
 })
}

function Get-ScopedMediaWorkers([string]$ScopeValue) {
 $hosts = @(Get-ScopedMatches 'AppleMusicLyrics.exe' $ScopeValue)
 if ($hosts.Count -eq 0) { return @() }
 $hostPids = @($hosts | ForEach-Object { [int]$_.ProcessId })
 $all = Get-ScopedProcessInfo
 return @($all | Where-Object {
  $_.Name -ieq 'powershell.exe' -and
  ($hostPids -contains [int]$_.ParentProcessId) -and
  $null -ne $_.CommandLine -and
  $_.CommandLine.IndexOf('MediaSession.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0
 })
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 8000, [int]$IntervalMs = 150) {
 $sw = [Diagnostics.Stopwatch]::StartNew()
 while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
  if (& $Condition) { return $true }
  Start-Sleep -Milliseconds $IntervalMs
 }
 return [bool](& $Condition)
}

function Invoke-HostIpc([string]$UnitExe, [string]$ScopeValue, [string]$CommandText, [int]$TimeoutMs = 8000) {
 $sw = [Diagnostics.Stopwatch]::StartNew()
 $lastErr = ''
 while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
  $ipcRun = Invoke-NativeCaptured { & $UnitExe --ipc $ScopeValue $CommandText }
  $text = ($ipcRun.Output -join "`n").Trim()
  if ($ipcRun.ExitCode -eq 0) {
   if ($text -match 'OK Enabled=(True|False) Locked=(True|False) Visible=(True|False) Polling=(True|False)') {
    return [pscustomobject]@{
     Enabled = [bool]::Parse($Matches[1])
     Locked  = [bool]::Parse($Matches[2])
     Visible = [bool]::Parse($Matches[3])
     Polling = [bool]::Parse($Matches[4])
     Raw     = $text
    }
   }
   throw "Unexpected IPC output for '$CommandText': $text"
  }
  $lastErr = $text
  Start-Sleep -Milliseconds 150
 }
 throw "IPC '$CommandText' timed out after ${TimeoutMs}ms: $lastErr"
}

Write-Report "BUILD_HOST $($hostExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $hostExe.FullName).Hash)"
Write-Report "BUILD_WATCH $($watchExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $watchExe.FullName).Hash)"
Write-Report "BUILD_LAUNCHER $($launcherExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $launcherExe.FullName).Hash)"
Write-Report "MEDIA_SCRIPT $($mediaScript.FullName)"
Write-Report "ARTIFACTS $runDir"
Write-Report "SOAK_SCOPE $soakScope"
Write-Report "REAL_SCOPE $realScope"

Copy-Item -LiteralPath $hostExe.FullName -Destination (Join-Path $unitDir 'AppleMusicLyrics.exe')
$unitExePath = Join-Path $unitDir 'LifecycleTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/out:' + $unitExePath) ('/reference:' + (Join-Path $unitDir 'AppleMusicLyrics.exe')) $monitorSource $unitSource
if ($LASTEXITCODE -ne 0) { throw "LifecycleTests compilation failed. Artifacts: $runDir" }

$stagedLauncher = Join-Path $standbyDir 'AppleMusicLyricsStandby.exe'
Copy-Item -LiteralPath $launcherExe.FullName -Destination $stagedLauncher

try {
 # Part A: Native launcher resolution + 5-minute P6 idle soak
 $launcherProc = Start-Process -FilePath $stagedLauncher -ArgumentList @('--target-process', 'AMNeverRunningTarget.exe', '--ipc-scope', $soakScope) -WorkingDirectory $standbyDir -PassThru
 Assert-True ($launcherProc.WaitForExit(5000) -and $launcherProc.ExitCode -eq 0) 'Part A: AppleMusicLyricsStandby.exe launched watcher and exited 0'

 Assert-True (Wait-Until { @(Get-ScopedMatches 'AppleMusicLyricsWatch.exe' $soakScope).Count -eq 1 } 8000) 'Part A: scoped AppleMusicLyricsWatch.exe started via native launcher'
 $watchWmi = @(Get-ScopedMatches 'AppleMusicLyricsWatch.exe' $soakScope)[0]
 Assert-True ([string]::Equals($watchWmi.ExecutablePath, $watchExe.FullName, [StringComparison]::OrdinalIgnoreCase)) "Part A: launcher resolved latest build ($($watchWmi.ExecutablePath))"

 $watchPid = [int]$watchWmi.ProcessId
 $watchProc = [System.Diagnostics.Process]::GetProcessById($watchPid)

 $statusRun = Invoke-NativeCaptured { & $watchExe.FullName --status --ipc-scope $soakScope }
 Assert-True ($statusRun.ExitCode -eq 0) 'Part A: AppleMusicLyricsWatch.exe --status returned 0'

 Start-Sleep -Seconds $WarmupSeconds
 $watchProc.Refresh()
 $startCpu = $watchProc.TotalProcessorTime
 $soakTimer = [Diagnostics.Stopwatch]::StartNew()
 $memSamples = New-Object 'System.Collections.Generic.List[double]'
 $maxTcpCount = 0

 $steps = [Math]::Max(1, [int][Math]::Ceiling($SoakSeconds / 5.0))
 $midpointStep = [Math]::Max(1, [int][Math]::Floor($steps / 2))
 for ($step = 1; $step -le $steps; $step++) {
  $remainingMs = [int](($SoakSeconds * 1000) - $soakTimer.ElapsedMilliseconds)
  if ($remainingMs -le 0) { break }
  Start-Sleep -Milliseconds ([Math]::Min(5000, $remainingMs))
  $watchProc.Refresh()
  if ($watchProc.HasExited) { throw 'Standby watcher exited unexpectedly during 5-minute idle soak.' }
  $memSamples.Add($watchProc.PrivateMemorySize64 / 1MB)
  if ($step -eq 1 -or $step -eq $midpointStep -or $step -eq $steps) {
   $tcpCount = @(Get-NetTCPConnection -OwningProcess $watchPid -ErrorAction SilentlyContinue).Count
   if ($tcpCount -gt $maxTcpCount) { $maxTcpCount = $tcpCount }
  }
 }
 $soakTimer.Stop()
 $watchProc.Refresh()
 $endCpu = $watchProc.TotalProcessorTime
 $elapsedSec = [Math]::Max(0.1, $soakTimer.Elapsed.TotalSeconds)
 $cpuPercent = (($endCpu - $startCpu).TotalSeconds / ($elapsedSec * [Environment]::ProcessorCount)) * 100.0
 $memStats = $memSamples | Measure-Object -Minimum -Average -Maximum
 $minMemMiB = $memStats.Minimum
 $avgMemMiB = $memStats.Average
 $peakMemMiB = $memStats.Maximum
 $childCount = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId = " + $watchPid) -ErrorAction SilentlyContinue).Count
 $scopedHostCount = @(Get-ScopedMatches 'AppleMusicLyrics.exe' $soakScope).Count

 Write-Report ("SOAK_METRICS duration={0:F2}s pollMs=1500 cpu={1:F4}% memMin={2:F2}MiB memAvg={3:F2}MiB memPeak={4:F2}MiB children={5} scopedHosts={6} maxTcp={7}" -f $elapsedSec, $cpuPercent, $minMemMiB, $avgMemMiB, $peakMemMiB, $childCount, $scopedHostCount, $maxTcpCount)
 Assert-True ($peakMemMiB -le 30.0) ("Part A P6: peak private memory {0:F2} MiB <= 30.00 MiB" -f $peakMemMiB)
 Assert-True ($cpuPercent -le 0.1) ("Part A P6: 5-minute idle CPU {0:F4}% <= 0.1000%" -f $cpuPercent)
 Assert-True ($childCount -eq 0 -and $scopedHostCount -eq 0) 'Part A P6: zero child processes and zero scoped hosts during idle soak'
 Assert-True ($maxTcpCount -eq 0) 'Part A P6: zero TCP connections opened by idle watcher'

 $stopLauncher = Start-Process -FilePath $stagedLauncher -ArgumentList @('--stop', '--ipc-scope', $soakScope) -WorkingDirectory $standbyDir -PassThru
 Assert-True ($stopLauncher.WaitForExit(5000) -and $stopLauncher.ExitCode -eq 0) 'Part A: AppleMusicLyricsStandby.exe --stop forwarded stop command cleanly'
 Assert-True ($watchProc.WaitForExit(5000) -and $watchProc.HasExited) 'Part A: standby watcher exited after launcher --stop'

 # Part B: Non-invasive live AppleMusic.exe detection
 $appleProcs = @(Get-Process -Name AppleMusic -ErrorAction SilentlyContinue)
 if ($appleProcs.Count -eq 0) {
  Write-Report 'SKIP Part B: AppleMusic.exe is not currently running'
 } else {
  Write-Report ("INFO Part B: detected live AppleMusic.exe count=$($appleProcs.Count) pids=" + (($appleProcs | ForEach-Object { $_.Id }) -join ','))
  $realWatchArgs = @(
   '--target-process', 'AppleMusic.exe',
   '--host-exe', $hostExe.FullName,
   '--settings', (Join-Path $realStateDir 'settings.json'),
   '--ipc-scope', $realScope,
   '--debounce-ms', '500',
   '--poll-ms', '500'
  )
  $realWatchProc = Start-Process -FilePath $watchExe.FullName -ArgumentList $realWatchArgs -WorkingDirectory $build.FullName -PassThru
  $realWatchExited = $false
  try {
   Assert-True (Wait-Until { @(Get-ScopedMatches 'AppleMusicLyrics.exe' $realScope).Count -eq 1 } 10000) 'Part B: watcher detected live AppleMusic.exe and auto-launched isolated host'
   $state = Invoke-HostIpc $unitExePath $realScope 'GET_STATE'
   Assert-True (-not $state.Enabled -and -not $state.Visible -and -not $state.Polling) "Part B: isolated host stayed hidden with Polling=false ($($state.Raw))"
   Start-Sleep -Milliseconds 300
   Assert-True (@(Get-ScopedMediaWorkers $realScope).Count -eq 0) 'Part B: zero MediaSession.ps1 workers started for Enabled=false'

   $null = Invoke-HostIpc $unitExePath $realScope 'SHUTDOWN'
   Assert-True (Wait-Until { @(Get-ScopedMatches 'AppleMusicLyrics.exe' $realScope).Count -eq 0 } 6000) 'Part B: isolated host exited cleanly after IPC SHUTDOWN'

   $null = Invoke-NativeCaptured { & $watchExe.FullName --stop --ipc-scope $realScope }
   $realWatchExited = $realWatchProc.WaitForExit(5000) -and $realWatchProc.HasExited
  } finally {
   if ($null -ne $realWatchProc -and -not $realWatchProc.HasExited) {
    $null = Invoke-NativeCaptured { & $watchExe.FullName --stop --ipc-scope $realScope }
    if (-not $realWatchProc.WaitForExit(5000)) { $realWatchProc.Kill(); $null = $realWatchProc.WaitForExit(2000) }
   }
   if ($null -ne $realWatchProc) { $realWatchProc.Dispose() }
  }
  Assert-True $realWatchExited 'Part B: live-detection watcher exited cleanly after --stop'
 }

 # Part C: Safety check
 $standbyRunAfter = Get-StandbyRunEntry
 Assert-True ($standbyRunAfter -eq $standbyRunBefore) 'Part C: HKCU Run AMDesktopLyricsStandby was not created or altered'
 $realSettingsExistedAfter = Test-Path -LiteralPath $realSettingsPath -PathType Leaf
 $realSettingsHashAfter = if ($realSettingsExistedAfter) { (Get-FileHash -Algorithm SHA256 -LiteralPath $realSettingsPath).Hash } else { $null }
 Assert-True (($realSettingsExistedBefore -eq $realSettingsExistedAfter) -and ($realSettingsHashBefore -eq $realSettingsHashAfter)) 'Part C: live work\state\settings.json was untouched'

 Write-Report 'PASS ALL STANDBY SOAK AND LIVE DETECTION CHECKS'
} finally {
 foreach ($scopeValue in @($soakScope, $realScope)) {
  $null = Invoke-NativeCaptured { & $watchExe.FullName --stop --ipc-scope $scopeValue }
  foreach ($proc in (Get-ScopedMediaWorkers $scopeValue)) {
   try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
  }
  foreach ($proc in (Get-ScopedMatches 'AppleMusicLyrics.exe' $scopeValue)) {
   try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
  }
  foreach ($proc in (Get-ScopedMatches 'AppleMusicLyricsWatch.exe' $scopeValue)) {
   try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
  }
 }
 $script:reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8
 Write-Output "REPORT $reportPath"
}
