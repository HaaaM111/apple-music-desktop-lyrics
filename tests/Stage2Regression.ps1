[CmdletBinding()]
param(
 [Parameter(Mandatory = $true)][string]$BuildDirectory
)

$ErrorActionPreference = 'Stop'

$build = Get-Item -LiteralPath $BuildDirectory
if (-not $build.PSIsContainer) { throw 'BuildDirectory must be a directory.' }
$hostExe = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyrics.exe')
$watchExe = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyricsWatch.exe')

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework C# compiler is not available.' }

$workspace = Split-Path -Parent $PSScriptRoot
$testSource = Join-Path $workspace 'tests\Stage2Tests.cs'
if (-not (Test-Path -LiteralPath $testSource -PathType Leaf)) { throw "Required test source not found: $testSource" }

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runDir = Join-Path $workspace ('work\qa-stage2-' + $timestamp)
$binDir = Join-Path $runDir 'bin'
$stateDir = Join-Path $runDir 'state'
foreach ($dir in @($runDir, $binDir, $stateDir)) {
 $null = New-Item -ItemType Directory -Path $dir
}
$reportPath = Join-Path $runDir 'stage2-regression.txt'

$realSettingsPath = Join-Path $workspace 'work\state\settings.json'
$realSettingsExistedBefore = Test-Path -LiteralPath $realSettingsPath -PathType Leaf
$realSettingsHashBefore = if ($realSettingsExistedBefore) { (Get-FileHash -Algorithm SHA256 -LiteralPath $realSettingsPath).Hash } else { $null }

$script:reportLines = New-Object 'System.Collections.Generic.List[string]'
function Write-Report([string]$Line) {
 $script:reportLines.Add($Line)
 Write-Output $Line
}

Write-Report "BUILD_HOST $($hostExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $hostExe.FullName).Hash)"
Write-Report "BUILD_WATCH $($watchExe.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $watchExe.FullName).Hash)"
Write-Report "ARTIFACTS $runDir"

$localHostExe = Join-Path $binDir 'AppleMusicLyrics.exe'
Copy-Item -LiteralPath $hostExe.FullName -Destination $localHostExe
$testExe = Join-Path $binDir 'Stage2Tests.exe'

$references = @(
 'System.dll',
 'System.Core.dll',
 'System.Web.Extensions.dll',
 'WindowsBase.dll',
 'PresentationCore.dll',
 'PresentationFramework.dll',
 'UIAutomationClient.dll',
 'UIAutomationTypes.dll'
)
$compileArgs = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', ('/out:' + $testExe), ('/reference:' + $localHostExe))
foreach ($refName in $references) {
 $refPath = Join-Path $framework $refName
 if (-not (Test-Path -LiteralPath $refPath -PathType Leaf)) {
  $refPath = Join-Path (Join-Path $framework 'WPF') $refName
 }
 if (-not (Test-Path -LiteralPath $refPath -PathType Leaf)) {
  throw "Required framework reference not found: $refName"
 }
 $compileArgs += '/reference:' + $refPath
}
$compileArgs += $testSource

& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) {
 $script:reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8
 throw "Stage2Tests compilation failed. Artifacts: $runDir"
}

$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
 $testOutput = & $testExe $stateDir $watchExe.FullName 2>&1
 $testExit = $LASTEXITCODE
} finally {
 $ErrorActionPreference = $prevEap
}

foreach ($line in @($testOutput)) {
 Write-Report ($line.ToString())
}

$realSettingsExistedAfter = Test-Path -LiteralPath $realSettingsPath -PathType Leaf
$realSettingsHashAfter = if ($realSettingsExistedAfter) { (Get-FileHash -Algorithm SHA256 -LiteralPath $realSettingsPath).Hash } else { $null }
$settingsUntouched = ($realSettingsExistedBefore -eq $realSettingsExistedAfter) -and ($realSettingsHashBefore -eq $realSettingsHashAfter)
if ($settingsUntouched) {
 Write-Report 'PASS Safety: live work\state\settings.json was untouched'
} else {
 Write-Report 'FAIL Safety: live work\state\settings.json changed during regression'
}

$script:reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Report "REPORT $reportPath"

if ($testExit -ne 0 -or -not $settingsUntouched) {
 throw "Stage2Regression failed (testExit=$testExit, settingsUntouched=$settingsUntouched). Report: $reportPath"
}
