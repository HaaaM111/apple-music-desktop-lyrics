[CmdletBinding()]
param(
 [Parameter(Mandatory = $true)][string]$BuildDirectory,
 [int]$IdleSampleSeconds = 20
)

$ErrorActionPreference = 'Stop'
if ($IdleSampleSeconds -lt 1) { throw 'IdleSampleSeconds must be at least 1.' }

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
$dummySource = Join-Path $workspace 'tests\DummyTarget.cs'
$manageStandbyScript = Join-Path $workspace 'scripts\ManageStandby.ps1'
foreach ($requiredFile in @($monitorSource, $unitSource, $dummySource, $manageStandbyScript)) {
 if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
  throw "Required file not found: $requiredFile"
 }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runDir = Join-Path $workspace ('work\qa-lifecycle-' + $timestamp)
$unitDir = Join-Path $runDir 'unit'
$targetDir = Join-Path $runDir 'target'
$stateDir = Join-Path $runDir 'state'
foreach ($dir in @($runDir, $unitDir, $targetDir, $stateDir)) {
 $null = New-Item -ItemType Directory -Path $dir
}
$reportPath = Join-Path $runDir 'lifecycle-regression.txt'
$settingsPath = Join-Path $stateDir 'settings.json'
$scope = 'qa-' + [guid]::NewGuid().ToString('N')

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

Write-Report "BUILD_HOST $($hostExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $hostExe.FullName).Hash)"
Write-Report "BUILD_WATCH $($watchExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $watchExe.FullName).Hash)"
Write-Report "BUILD_LAUNCHER $($launcherExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $launcherExe.FullName).Hash)"
Write-Report "MEDIA_SCRIPT $($mediaScript.FullName)"
Write-Report "ARTIFACTS $runDir"
Write-Report "IPC_SCOPE $scope"

# Compile static tests\DummyTarget.cs and tests\LifecycleTests.cs into isolated qa directory.
$dummyExePath = Join-Path $targetDir 'DummyTarget.exe'
& $compiler /nologo /target:winexe /platform:x64 /utf8output ('/out:' + $dummyExePath) $dummySource
if ($LASTEXITCODE -ne 0) { throw "DummyTarget compilation failed. Artifacts: $runDir" }

Copy-Item -LiteralPath $hostExe.FullName -Destination (Join-Path $unitDir 'AppleMusicLyrics.exe')
$unitExePath = Join-Path $unitDir 'LifecycleTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/out:' + $unitExePath) ('/reference:' + (Join-Path $unitDir 'AppleMusicLyrics.exe')) $monitorSource $unitSource
if ($LASTEXITCODE -ne 0) { throw "LifecycleTests compilation failed. Artifacts: $runDir" }

$unitRun = Invoke-NativeCaptured { & $unitExePath (Join-Path $unitDir 'tmp') $dummyExePath }
foreach ($line in $unitRun.Output) { Write-Report ('UNIT ' + $line) }
Assert-True ($unitRun.ExitCode -eq 0) 'LifecycleTests.exe unit suite passed'

$trackedTargets = New-Object 'System.Collections.Generic.List[object]'
$trackedWatches = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$trackedManualHosts = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'

function Get-ScopedProcessInfo {
 $filter = "Name = 'AppleMusicLyrics.exe' OR Name = 'AppleMusicLyricsWatch.exe' OR Name = 'powershell.exe' OR Name = 'DummyTarget.exe'"
 return @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue)
}

function Get-ScopedHosts {
 $all = Get-ScopedProcessInfo
 return @($all | Where-Object {
  $_.Name -ieq 'AppleMusicLyrics.exe' -and
  $null -ne $_.CommandLine -and
  $_.CommandLine.IndexOf($scope, [StringComparison]::OrdinalIgnoreCase) -ge 0
 })
}

function Get-ScopedWatchers {
 $all = Get-ScopedProcessInfo
 return @($all | Where-Object {
  $_.Name -ieq 'AppleMusicLyricsWatch.exe' -and
  $null -ne $_.CommandLine -and
  $_.CommandLine.IndexOf($scope, [StringComparison]::OrdinalIgnoreCase) -ge 0
 })
}

function Get-ScopedMediaWorkers {
 $hosts = @(Get-ScopedHosts)
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

function Get-OwnDummyTargets {
 $all = Get-ScopedProcessInfo
 return @($all | Where-Object {
  $_.Name -ieq 'DummyTarget.exe' -and
  $null -ne $_.ExecutablePath -and
  [string]::Equals($_.ExecutablePath, $dummyExePath, [StringComparison]::OrdinalIgnoreCase)
 })
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 8000, [int]$IntervalMs = 150) {
 $sw = [Diagnostics.Stopwatch]::StartNew()
 while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
  $value = & $Condition
  if ($value) { return $true }
  Start-Sleep -Milliseconds $IntervalMs
 }
 return [bool](& $Condition)
}

function Invoke-HostIpc([string]$CommandText, [int]$TimeoutMs = 8000) {
 $sw = [Diagnostics.Stopwatch]::StartNew()
 $lastErr = ''
 while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
  $ipcRun = Invoke-NativeCaptured { & $unitExePath --ipc $scope $CommandText }
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

function Start-DummyTarget([string]$Tag) {
 $eventName = 'Local\AMQADummy_' + $scope + '_' + $Tag
 $waitEvent = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $eventName)
 $proc = Start-Process -FilePath $dummyExePath -ArgumentList $eventName -PassThru
 $entry = [pscustomobject]@{ Process = $proc; Event = $waitEvent; EventName = $eventName }
 $trackedTargets.Add($entry)
 return $entry
}

function Stop-DummyTarget($Entry, [int]$TimeoutMs = 5000) {
 if ($null -eq $Entry) { return }
 try {
  if ($null -ne $Entry.Event) { $null = $Entry.Event.Set() }
 } catch { }
 if ($null -ne $Entry.Process -and -not $Entry.Process.HasExited) {
  if (-not $Entry.Process.WaitForExit($TimeoutMs)) {
   try { $Entry.Process.Kill() } catch { }
   $null = $Entry.Process.WaitForExit(2000)
  }
 }
 if ($null -ne $Entry.Event) {
  try { $Entry.Event.Dispose() } catch { }
 }
}

function Read-SettingsEnabled {
 if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { return $null }
 $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
 return [bool]$json.Enabled
}

$watchProc = $null
try {
 $watchArgs = @(
  '--target-process', 'DummyTarget.exe',
  '--host-exe', $hostExe.FullName,
  '--settings', $settingsPath,
  '--ipc-scope', $scope,
  '--debounce-ms', '300',
  '--poll-ms', '400'
 )
 $watchProc = Start-Process -FilePath $watchExe.FullName -ArgumentList $watchArgs -PassThru
 $trackedWatches.Add($watchProc)
 Start-Sleep -Milliseconds 500
 Assert-True (-not $watchProc.HasExited) 'Standby watcher started and remains alive in idle'
 Assert-True (@(Get-ScopedHosts).Count -eq 0) 'No scoped host running before DummyTarget launches'

 # 1) First run / Enabled=false: auto-launch stays hidden and does not start MediaSession.ps1
 $t1 = Start-DummyTarget 'step1'
 $step1Launched = Wait-Until { @(Get-ScopedHosts).Count -eq 1 } 10000
 if (-not $step1Launched) {
  $watchProc.Refresh()
  $t1.Process.Refresh()
  Write-Report ("DIAG step1 dummyExited=$($t1.Process.HasExited) watchExited=$($watchProc.HasExited) scopedHosts=$(@(Get-ScopedHosts).Count) ownDummies=$(@(Get-OwnDummyTargets).Count)")
 }
 Assert-True $step1Launched 'Step 1: watcher auto-launched scoped AppleMusicLyrics.exe'
 $s1 = Invoke-HostIpc 'GET_STATE'
 Assert-True (-not $s1.Enabled -and -not $s1.Visible -and -not $s1.Polling) "Step 1: GET_STATE confirms Enabled=false, Visible=false, Polling=false ($($s1.Raw))"
 Start-Sleep -Milliseconds 300
 Assert-True (@(Get-ScopedMediaWorkers).Count -eq 0) 'Step 1: zero scoped MediaSession.ps1 PowerShell child processes when Enabled=false'

 # 2) SET_ENABLED 1 -> Visible=true, Polling=true; SET_ENABLED 0 -> Visible=false, Polling=false & worker exits
 $s2On = Invoke-HostIpc 'SET_ENABLED 1'
 Assert-True ($s2On.Enabled -and $s2On.Visible -and $s2On.Polling) "Step 2: SET_ENABLED 1 reports Enabled=true, Visible=true, Polling=true ($($s2On.Raw))"
 Assert-True (Wait-Until { @(Get-ScopedMediaWorkers).Count -eq 1 }) 'Step 2: MediaSession.ps1 child process started when Enabled=true'

 $s2Off = Invoke-HostIpc 'SET_ENABLED 0'
 Assert-True (-not $s2Off.Enabled -and -not $s2Off.Visible -and -not $s2Off.Polling) "Step 2: SET_ENABLED 0 reports Enabled=false, Visible=false, Polling=false ($($s2Off.Raw))"
 Assert-True (Wait-Until { @(Get-ScopedMediaWorkers).Count -eq 0 }) 'Step 2: MediaSession.ps1 child process exited after SET_ENABLED 0'

 # 3) SET_ENABLED 1 then DummyTarget exits -> host exits, Enabled=true preserved in settings.json, no orphan workers
 $s3On = Invoke-HostIpc 'SET_ENABLED 1'
 Assert-True ($s3On.Enabled -and $s3On.Visible -and $s3On.Polling) 'Step 3: SET_ENABLED 1 re-enabled overlay before target exit'
 Assert-True (Wait-Until { @(Get-ScopedMediaWorkers).Count -eq 1 }) 'Step 3: MediaSession.ps1 active prior to target exit'
 Stop-DummyTarget $t1
 Assert-True (Wait-Until { @(Get-ScopedHosts).Count -eq 0 } 8000) 'Step 3: scoped host exited automatically after DummyTarget exit'
 Assert-True (Wait-Until { @(Get-ScopedMediaWorkers).Count -eq 0 } 5000) 'Step 3: no orphan MediaSession.ps1 after host auto-exit'
 Assert-True ((Read-SettingsEnabled) -eq $true) 'Step 3: isolated settings.json retains Enabled=true after exit'

 # 4) Re-launch DummyTarget with Enabled=true -> auto-launch shows immediately and polls
 $t4 = Start-DummyTarget 'step4'
 Assert-True (Wait-Until { @(Get-ScopedHosts).Count -eq 1 } 8000) 'Step 4: watcher auto-launched scoped host on second DummyTarget launch'
 $s4 = Invoke-HostIpc 'GET_STATE'
 Assert-True ($s4.Enabled -and $s4.Visible -and $s4.Polling) "Step 4: auto-launched host immediately shows Visible=true, Polling=true ($($s4.Raw))"
 Assert-True (Wait-Until { @(Get-ScopedMediaWorkers).Count -eq 1 } 6000) 'Step 4: MediaSession.ps1 active for Enabled=true auto-launch'
 Stop-DummyTarget $t4
 Assert-True (Wait-Until { @(Get-ScopedHosts).Count -eq 0 -and @(Get-ScopedMediaWorkers).Count -eq 0 } 8000) 'Step 4: scoped host and worker exited cleanly after DummyTarget exit'

 # 5) Manual launch while watcher active -> DummyTarget start/exit triggers CLIENT_EXITED and manual host exits, Enabled=true kept
 $manualArgs = @('--settings', $settingsPath, '--ipc-scope', $scope)
 $manualProc = Start-Process -FilePath $hostExe.FullName -ArgumentList $manualArgs -PassThru
 $trackedManualHosts.Add($manualProc)
 Assert-True (Wait-Until { @(Get-ScopedHosts).Count -eq 1 } 8000) 'Step 5: manual host started while watcher is running'
 $s5 = Invoke-HostIpc 'GET_STATE'
 Assert-True ($s5.Visible) "Step 5: manual host shows overlay directly ($($s5.Raw))"

 $t5 = Start-DummyTarget 'step5'
 Start-Sleep -Milliseconds 1500
 Stop-DummyTarget $t5
 Assert-True (Wait-Until { $manualProc.HasExited -and @(Get-ScopedHosts).Count -eq 0 -and @(Get-ScopedMediaWorkers).Count -eq 0 } 8000) 'Step 5: manual host exited via CLIENT_EXITED when DummyTarget stopped'
 Assert-True ((Read-SettingsEnabled) -eq $true) 'Step 5: isolated settings.json still retains Enabled=true'

 # 6) 10 consecutive DummyTarget start/stop cycles
 for ($cycle = 1; $cycle -le 10; $cycle++) {
  Assert-True (@(Get-ScopedWatchers).Count -eq 1 -and -not $watchProc.HasExited) "Step 6 cycle ${cycle}: watcher remains single instance"
  $tc = Start-DummyTarget ("cycle_$cycle")
  $appeared = Wait-Until { @(Get-ScopedHosts).Count -eq 1 } 8000
  $hostsNow = @(Get-ScopedHosts)
  Assert-True ($appeared -and $hostsNow.Count -eq 1) "Step 6 cycle ${cycle}: exactly 1 scoped host launched"
  $sc = Invoke-HostIpc 'GET_STATE'
  Assert-True ($sc.Enabled -and $sc.Visible -and $sc.Polling) "Step 6 cycle ${cycle}: host state active ($($sc.Raw))"
  Stop-DummyTarget $tc
  $cleaned = Wait-Until { @(Get-ScopedHosts).Count -eq 0 -and @(Get-ScopedMediaWorkers).Count -eq 0 } 8000
  Assert-True $cleaned "Step 6 cycle ${cycle}: host and MediaSession.ps1 cleaned up after target exit"
 }

 # 7) P6 idle resource measurement + --status + --stop
 Assert-True (@(Get-OwnDummyTargets).Count -eq 0 -and @(Get-ScopedHosts).Count -eq 0 -and @(Get-ScopedMediaWorkers).Count -eq 0) 'Step 7: idle baseline has zero targets, hosts, and media workers'
 Start-Sleep -Milliseconds 1000
 $watchProc.Refresh()
 $startCpu = $watchProc.TotalProcessorTime
 $idleTimer = [Diagnostics.Stopwatch]::StartNew()
 $memSamples = New-Object 'System.Collections.Generic.List[double]'
 for ($sec = 1; $sec -le $IdleSampleSeconds; $sec++) {
  Start-Sleep -Milliseconds 1000
  $watchProc.Refresh()
  if ($watchProc.HasExited) { throw 'Standby watcher exited unexpectedly during idle resource measurement.' }
  $memSamples.Add($watchProc.PrivateMemorySize64 / 1MB)
 }
 $idleTimer.Stop()
 $watchProc.Refresh()
 $endCpu = $watchProc.TotalProcessorTime
 $elapsedSec = [Math]::Max(0.1, $idleTimer.Elapsed.TotalSeconds)
 $cpuPercent = (($endCpu - $startCpu).TotalSeconds / ($elapsedSec * [Environment]::ProcessorCount)) * 100.0
 $peakMemMiB = ($memSamples | Measure-Object -Maximum).Maximum
 $avgMemMiB = ($memSamples | Measure-Object -Average).Average
 $watcherChildren = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId = " + $watchProc.Id) -ErrorAction SilentlyContinue)

 Write-Report ("IDLE_METRICS duration={0:F2}s cpu={1:F4}% memAvg={2:F2}MiB memPeak={3:F2}MiB children={4}" -f $elapsedSec, $cpuPercent, $avgMemMiB, $peakMemMiB, $watcherChildren.Count)
 Assert-True ($peakMemMiB -le 30.0) ("Step 7 P6: watcher peak private memory {0:F2} MiB <= 30.00 MiB" -f $peakMemMiB)
 Assert-True ($cpuPercent -le 0.1) ("Step 7 P6: watcher idle CPU {0:F4}% <= 0.1000%" -f $cpuPercent)
 Assert-True ($watcherChildren.Count -eq 0) 'Step 7 P6: watcher has 0 child processes while idle'

 $statusRun = Invoke-NativeCaptured { & $watchExe.FullName --status --ipc-scope $scope }
 Write-Report ("WATCH_STATUS exit=$($statusRun.ExitCode) output=" + (($statusRun.Output -join ' | ').Trim()))
 Assert-True ($statusRun.ExitCode -eq 0) 'Step 7: AppleMusicLyricsWatch.exe --status exited with code 0'

 $stopRun = Invoke-NativeCaptured { & $watchExe.FullName --stop --ipc-scope $scope }
 Write-Report ("WATCH_STOP exit=$($stopRun.ExitCode) output=" + (($stopRun.Output -join ' | ').Trim()))
 Assert-True ($stopRun.ExitCode -eq 0) 'Step 7: AppleMusicLyricsWatch.exe --stop exited with code 0'
 Assert-True ($watchProc.WaitForExit(5000) -and $watchProc.HasExited) 'Step 7: standby watcher exited after --stop'

 # 8) Read-only ManageStandby.ps1 checks + registry/real-settings safety verification
 $psExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
 foreach ($action in @('Status', 'PreviewInstall')) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $psExe
  $psi.Arguments = "-NoLogo -NoProfile -NonInteractive -InputFormat Text -ExecutionPolicy Bypass -File `"$manageStandbyScript`" -Action $action"
  $psi.WorkingDirectory = $workspace
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $proc = [System.Diagnostics.Process]::Start($psi)
  try {
   $stdout = $proc.StandardOutput.ReadToEnd()
   $stderr = $proc.StandardError.ReadToEnd()
   if (-not $proc.WaitForExit(15000)) {
    $proc.Kill()
    throw "ManageStandby.ps1 -Action $action timed out."
   }
   $manageExit = $proc.ExitCode
   $manageOut = ((@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' | ').Trim() -replace '[\r\n]+', ' | '
  } finally {
   $proc.Dispose()
  }
  Write-Report ("MANAGE_STANDBY action=$action exit=$manageExit output=" + $manageOut)
  Assert-True ($manageExit -eq 0) "Step 8: ManageStandby.ps1 -Action $action exited with code 0"
  if ($action -eq 'PreviewInstall') {
   Assert-True ($manageOut.Contains('AppleMusicLyricsStandby.exe') -and -not $manageOut.Contains('powershell.exe -NoLogo') -and -not $manageOut.Contains('-WindowStyle Hidden')) 'Step 8: PreviewInstall uses native WinExe launcher without hidden PowerShell'
  }
 }

 $standbyRunAfter = Get-StandbyRunEntry
 Write-Report ("STANDBY_RUN_KEY before=" + ($(if ($null -eq $standbyRunBefore) { '<missing>' } else { $standbyRunBefore })) + " after=" + ($(if ($null -eq $standbyRunAfter) { '<missing>' } else { $standbyRunAfter })))
 Assert-True ($standbyRunAfter -eq $standbyRunBefore) 'Step 8: HKCU Run AMDesktopLyricsStandby was not created or altered'

 $realSettingsExistedAfter = Test-Path -LiteralPath $realSettingsPath -PathType Leaf
 $realSettingsHashAfter = if ($realSettingsExistedAfter) { (Get-FileHash -Algorithm SHA256 -LiteralPath $realSettingsPath).Hash } else { $null }
 Assert-True (($realSettingsExistedBefore -eq $realSettingsExistedAfter) -and ($realSettingsHashBefore -eq $realSettingsHashAfter)) 'Safety: live work\state\settings.json was untouched'

 Write-Report 'PASS ALL LIFECYCLE REGRESSION CHECKS'
} finally {
 foreach ($entry in $trackedTargets) {
  Stop-DummyTarget $entry 2000
 }
 foreach ($manual in $trackedManualHosts) {
  try {
   if ($null -ne $manual -and -not $manual.HasExited) { $manual.Kill(); $null = $manual.WaitForExit(2000) }
  } catch { }
  if ($null -ne $manual) { try { $manual.Dispose() } catch { } }
 }
 foreach ($watcher in $trackedWatches) {
  try {
   if ($null -ne $watcher -and -not $watcher.HasExited) {
    $null = Invoke-NativeCaptured { & $watchExe.FullName --stop --ipc-scope $scope }
    if (-not $watcher.WaitForExit(3000)) {
     $watcher.Kill()
     $null = $watcher.WaitForExit(2000)
    }
   }
  } catch { }
  if ($null -ne $watcher) { try { $watcher.Dispose() } catch { } }
 }
 foreach ($proc in (Get-ScopedMediaWorkers)) {
  try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
 }
 foreach ($proc in (Get-ScopedHosts)) {
  try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
 }
 foreach ($proc in (Get-ScopedWatchers)) {
  try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
 }
 foreach ($proc in (Get-OwnDummyTargets)) {
  try { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
 }
 $script:reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8
 Write-Output "REPORT $reportPath"
}
