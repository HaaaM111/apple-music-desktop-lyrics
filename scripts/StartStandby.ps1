$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputsDir = Join-Path $root 'outputs'
$build = $null
if (Test-Path -LiteralPath $outputsDir -PathType Container) {
 $build = Get-ChildItem -LiteralPath $outputsDir -Directory -Filter 'build-*' |
  Where-Object {
   (Test-Path -LiteralPath (Join-Path $_.FullName 'AppleMusicLyricsWatch.exe') -PathType Leaf) -and
   (Test-Path -LiteralPath (Join-Path $_.FullName 'AppleMusicLyrics.exe') -PathType Leaf) -and
   (Test-Path -LiteralPath (Join-Path $_.FullName 'MediaSession.ps1') -PathType Leaf)
  } |
  Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'AppleMusicLyricsWatch.exe')).LastWriteTimeUtc } -Descending |
  Select-Object -First 1
}
if ($null -eq $build) {
 $output = & (Join-Path $PSScriptRoot 'Build.ps1')
 $buildPath = @($output)[-1]
} else {
 $buildPath = $build.FullName
}
$watchExe = Join-Path $buildPath 'AppleMusicLyricsWatch.exe'
if (-not (Test-Path -LiteralPath $watchExe -PathType Leaf)) {
 throw "Missing standby watcher executable: $watchExe"
}
Start-Process -FilePath $watchExe -WorkingDirectory $buildPath
