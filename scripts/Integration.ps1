param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $root ('work\integration-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $folder
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$exe = Join-Path $folder 'MediaIntegration.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) ('/out:' + $exe) (Join-Path $root 'src\Contracts.cs') (Join-Path $root 'src\Lyrics.cs') (Join-Path $root 'src\MediaSource.cs') (Join-Path $root 'tests\MediaIntegration.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed.' }
$log = & $exe (Join-Path $BuildDirectory 'MediaSession.ps1')
$code = $LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $folder 'media-integration.txt') -Encoding UTF8
$log | Write-Output
Write-Output ('INTEGRATION_OUTPUT=' + $folder)
if ($code -ne 0) { throw 'Media integration failed.' }
