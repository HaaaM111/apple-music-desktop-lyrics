param(
 [ValidateSet('Status', 'PreviewInstall', 'Install', 'Uninstall')]
 [string]$Action = 'Status'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputsDir = Join-Path $root 'outputs'
$standbyDir = Join-Path $root 'work\standby'
$stableLauncher = Join-Path $standbyDir 'AppleMusicLyricsStandby.exe'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'AMDesktopLyricsStandby'
$command = "`"$stableLauncher`""

function Find-LatestBuildFile([string]$FileName) {
 if (-not (Test-Path -LiteralPath $outputsDir -PathType Container)) { return $null }
 $build = Get-ChildItem -LiteralPath $outputsDir -Directory -Filter 'build-*' |
  Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $FileName) -PathType Leaf } |
  Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName $FileName)).LastWriteTimeUtc } -Descending |
  Select-Object -First 1
 if ($null -eq $build) { return $null }
 return (Join-Path $build.FullName $FileName)
}

function Get-StartupRegistryValue {
 if (-not (Test-Path -LiteralPath $runKeyPath)) { return $null }
 $item = Get-ItemProperty -LiteralPath $runKeyPath -Name $valueName -ErrorAction SilentlyContinue
 if ($null -eq $item) { return $null }
 return $item.$valueName
}

function Invoke-WatchCommand {
 param(
  [string]$WatchExe,
  [string]$Arguments
 )
 $psi = New-Object System.Diagnostics.ProcessStartInfo
 $psi.FileName = $WatchExe
 $psi.Arguments = $Arguments
 $psi.WorkingDirectory = Split-Path -Parent $WatchExe
 $psi.UseShellExecute = $false
 $psi.CreateNoWindow = $true
 $psi.RedirectStandardOutput = $true
 $psi.RedirectStandardError = $true

 $proc = [System.Diagnostics.Process]::Start($psi)
 try {
  $stdout = $proc.StandardOutput.ReadToEnd()
  $stderr = $proc.StandardError.ReadToEnd()
  if (-not $proc.WaitForExit(5000)) {
   return @{ ExitCode = -1; Stdout = $stdout.Trim(); Stderr = 'Timed out waiting for AppleMusicLyricsWatch.exe.' }
  }
  return @{ ExitCode = $proc.ExitCode; Stdout = $stdout.Trim(); Stderr = $stderr.Trim() }
 } finally {
  $proc.Dispose()
 }
}

switch ($Action) {
 'Status' {
  $currentValue = Get-StartupRegistryValue
  $watchExe = Find-LatestBuildFile 'AppleMusicLyricsWatch.exe'
  $launcherBuilt = Find-LatestBuildFile 'AppleMusicLyricsStandby.exe'
  Write-Output "RegistryPath: $runKeyPath"
  Write-Output "ValueName: $valueName"
  Write-Output "StableLauncherPath: $stableLauncher"
  Write-Output ('StableLauncherExists: ' + (Test-Path -LiteralPath $stableLauncher -PathType Leaf))
  Write-Output ('LatestLauncherBuild: ' + ($(if ($null -eq $launcherBuilt) { 'NotBuilt' } else { $launcherBuilt })))
  if ($null -eq $currentValue) {
   Write-Output 'StartupEntry: NotInstalled'
  } else {
   Write-Output 'StartupEntry: Installed'
   Write-Output "StartupCommand: $currentValue"
   Write-Output ('MatchesExpected: ' + ($currentValue -eq $command))
  }
  if ($null -eq $watchExe) {
   Write-Output 'WatcherBinary: NotBuilt'
   Write-Output 'WatcherStatus: UNKNOWN'
  } else {
   Write-Output "WatcherBinary: $watchExe"
   $res = Invoke-WatchCommand -WatchExe $watchExe -Arguments '--status'
   $statusText = if ([string]::IsNullOrWhiteSpace($res.Stdout)) { "EXIT_$($res.ExitCode)" } else { $res.Stdout }
   Write-Output "WatcherStatus: $statusText (ExitCode=$($res.ExitCode))"
  }
 }
 'PreviewInstall' {
  $currentValue = Get-StartupRegistryValue
  $launcherBuilt = Find-LatestBuildFile 'AppleMusicLyricsStandby.exe'
  Write-Output 'Action: PreviewInstall (read-only; no changes applied)'
  Write-Output "RegistryPath: $runKeyPath"
  Write-Output "ValueName: $valueName"
  Write-Output "Command: $command"
  Write-Output ('SourceLauncher: ' + ($(if ($null -eq $launcherBuilt) { '<needs build>' } else { $launcherBuilt })))
  if ($null -eq $currentValue) {
   Write-Output 'CurrentValue: <none>'
  } else {
   Write-Output "CurrentValue: $currentValue"
  }
  Write-Output "UndoCommand: powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Action Uninstall"
 }
 'Install' {
  $launcherBuilt = Find-LatestBuildFile 'AppleMusicLyricsStandby.exe'
  if ($null -eq $launcherBuilt) {
   $null = & (Join-Path $PSScriptRoot 'Build.ps1')
   $launcherBuilt = Find-LatestBuildFile 'AppleMusicLyricsStandby.exe'
  }
  if ($null -eq $launcherBuilt) { throw 'AppleMusicLyricsStandby.exe could not be built.' }
  if (-not (Test-Path -LiteralPath $standbyDir -PathType Container)) {
   $null = New-Item -ItemType Directory -Path $standbyDir -Force
  }
  Copy-Item -LiteralPath $launcherBuilt -Destination $stableLauncher -Force
  if (-not (Test-Path -LiteralPath $runKeyPath)) {
   $null = New-Item -Path $runKeyPath -Force
  }
  Set-ItemProperty -LiteralPath $runKeyPath -Name $valueName -Value $command -Type String
  Write-Output "Installed startup entry: $runKeyPath\$valueName -> $command"
  Start-Process -FilePath $stableLauncher -WorkingDirectory $standbyDir
 }
 'Uninstall' {
  $removed = $false
  if ($null -ne (Get-StartupRegistryValue)) {
   Remove-ItemProperty -LiteralPath $runKeyPath -Name $valueName -ErrorAction Stop
   $removed = $true
  }
  Write-Output "RegistryEntryRemoved: $removed"
  $watchExe = Find-LatestBuildFile 'AppleMusicLyricsWatch.exe'
  if ($null -ne $watchExe) {
   $res = Invoke-WatchCommand -WatchExe $watchExe -Arguments '--stop'
   Write-Output "WatcherStopExitCode: $($res.ExitCode)"
  } else {
   Write-Output 'WatcherStopExitCode: NotBuilt'
  }
  if (Test-Path -LiteralPath $stableLauncher -PathType Leaf) {
   Remove-Item -LiteralPath $stableLauncher -Force -ErrorAction SilentlyContinue
   Write-Output "RemovedLauncher: $stableLauncher"
  }
 }
}
