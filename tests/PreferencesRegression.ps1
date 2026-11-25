[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$BuildDirectory)

$ErrorActionPreference = 'Stop'
$build = Get-Item -LiteralPath $BuildDirectory
if (-not $build.PSIsContainer) { throw 'BuildDirectory must be a directory.' }
$assembly = Get-Item -LiteralPath (Join-Path $build.FullName 'AppleMusicLyrics.exe')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework C# compiler is not available.' }

$workspace = Split-Path -Parent $PSScriptRoot
$run = Join-Path $workspace ('work\qa-preferences-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $run 'project'
$buildOne = Join-Path $project 'outputs\build-probe-one\preview'
$buildTwo = Join-Path $project 'outputs\build-probe-two\preview'
foreach ($directory in @($buildOne, $buildTwo, (Join-Path $project 'src'), (Join-Path $project 'scripts'))) {
 $null = New-Item -ItemType Directory -Path $directory
}
$probeSource = Join-Path $run 'PreferencesProbe.cs'
$probeOne = Join-Path $buildOne 'PreferencesProbe.exe'
$probeTwo = Join-Path $buildTwo 'PreferencesProbe.exe'
$assemblyOne = Join-Path $buildOne $assembly.Name
$assemblyTwo = Join-Path $buildTwo $assembly.Name
Copy-Item -LiteralPath $assembly.FullName -Destination $assemblyOne
Copy-Item -LiteralPath $assembly.FullName -Destination $assemblyTwo

$source = @'
using System;
using System.IO;
using System.Linq;
using AMLyrics;

static class PreferencesProbe {
 const string TrackA = "qa.app|Track A|Artist|Album";
 const string TrackB = "qa.app|Track B|Artist|Album";
 const string Font = "Microsoft YaHei UI";
 const string Weight = "\u534A\u7C97";
 const string Fill = "#C8112233";
 const string Stroke = "#456789";

 static void Check(bool condition, string name) {
  if (!condition) throw new InvalidOperationException(name);
  Console.WriteLine("PASS " + name);
 }

 static bool Near(double value, double expected) {
  return Math.Abs(value - expected) < 0.000001;
 }

 static void CheckDisplay(LyricsWindowPreferences preferences, double size, string fill, string stroke, string label) {
  var display = preferences.Display;
  Check(Near(display.FontSize, size) && display.FontFamilyName == Font && display.FontWeightName == Weight, label + ": font family, size and weight");
  Check(display.ForegroundHex == fill && display.StrokeHex == stroke, label + ": distinct foreground and stroke HEX values");
 }

 static LyricsWindowPreferences Load(LyricsPreferencesStore store, string label) {
  var result = store.Load();
  Check(result.Status == LyricsPreferencesLoadStatus.Loaded, label + ": configuration loads (" + result.Status + ": " + result.Error + ")");
  return result.Preferences;
 }

 static void Save(LyricsPreferencesStore store, LyricsWindowPreferences preferences, string label) {
  var result = store.Save(preferences);
  Check(result.Succeeded, label + ": configuration saves (" + result.Status + ": " + result.Error + ")");
 }

 static LyricTiming ReadTiming(LyricsWindowPreferences preferences, double trackA, bool hasOverride, string label) {
  var timing = new LyricTiming();
  timing.LoadOffsets(preferences.TimingOffsets);
  var snapshot = timing.ReadOffsets();
  Check(Near(timing.Read(TrackA), trackA) && Near(timing.Read(TrackB), 1.5) &&
   snapshot.Count == (hasOverride ? 1 : 0) && snapshot.ContainsKey(TrackA) == hasOverride,
   label + ": Track A reads " + (hasOverride ? "1.7" : "1.5") + " seconds; Track B retains the 1.5-second default");
  return timing;
 }

 static LyricsPreferencesStore SharedStore(string project, string expectedBuild) {
  string processDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
  string expectedDirectory = Path.Combine(project, "outputs", expectedBuild, "preview");
  if (!String.Equals(processDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("Probe is not running from the expected synthetic build directory.");
  var store = new LyricsPreferencesStore();
  string expectedFile = Path.Combine(project, "work", "state", "settings.json");
  Check(String.Equals(store.FilePath, expectedFile, StringComparison.OrdinalIgnoreCase), expectedBuild + ": default path resolves to synthetic shared settings");
  return store;
 }

 static int Main(string[] args) {
  try {
   string project = Path.GetFullPath(args[0]);
   string isolated = Path.Combine(project, "work", "explicit", "settings.json");
   switch (args[1]) {
    case "save":
     var preferences = LyricsPreferencesStore.CreateDefaultPreferences();
     preferences.Display.FontSize = 42;
     preferences.Display.FontFamilyName = Font;
     preferences.Display.FontWeightName = Weight;
     preferences.Display.ForegroundHex = Fill;
     preferences.Display.StrokeHex = Stroke;
     var initial = new LyricTiming();
     initial.Adjust(TrackA, 0.2);
     preferences.TimingOffsets = initial.ReadOffsets();
     Save(new LyricsPreferencesStore(isolated), preferences, "explicit path");
     break;
    case "reopen-reset":
     var explicitStore = new LyricsPreferencesStore(isolated);
     var reopened = Load(explicitStore, "fresh process");
     CheckDisplay(reopened, 42, Fill, Stroke, "fresh process");
     var timing = ReadTiming(reopened, 1.7, true, "fresh process");
     timing.Reset(TrackA);
     reopened.TimingOffsets = timing.ReadOffsets();
     Save(explicitStore, reopened, "reset");
     break;
    case "verify-reset":
     var reset = Load(new LyricsPreferencesStore(isolated), "fresh process after reset");
     CheckDisplay(reset, 42, Fill, Stroke, "fresh process after reset");
     ReadTiming(reset, 1.5, false, "fresh process after reset");
     string corrupt = Path.Combine(project, "work", "explicit", "corrupt.json");
     byte[] original = { 0x7B, 0x22, 0x56, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22, 0x3A };
     File.WriteAllBytes(corrupt, original);
     var corruptStore = new LyricsPreferencesStore(corrupt);
     var failure = corruptStore.Load();
     Check(failure.Status == LyricsPreferencesLoadStatus.CorruptConfiguration && failure.UsedDefaults &&
      Near(failure.Preferences.Display.FontSize, 30) && failure.Preferences.TimingOffsets.Count == 0 &&
      original.SequenceEqual(File.ReadAllBytes(corrupt)), "corrupt JSON: load reports fallback and preserves original bytes");
     var attemptedSave = corruptStore.Save(reset);
     Check(!attemptedSave.Succeeded && original.SequenceEqual(File.ReadAllBytes(corrupt)), "corrupt JSON: save after failed load leaves original bytes unchanged");
     break;
    case "shared-write":
     var firstStore = SharedStore(project, "build-probe-one");
     var initialShared = firstStore.Load();
     Check(initialShared.Status == LyricsPreferencesLoadStatus.DefaultsUsed, "shared path initially uses defaults");
     var sharedPreferences = initialShared.Preferences;
     var sharedTiming = ReadTiming(sharedPreferences, 1.5, false, "shared path initial");
     sharedTiming.Adjust(TrackA, 0.2);
     sharedPreferences.TimingOffsets = sharedTiming.ReadOffsets();
     sharedPreferences.Display.FontSize = 43;
     sharedPreferences.Display.FontFamilyName = Font;
     sharedPreferences.Display.FontWeightName = Weight;
     sharedPreferences.Display.ForegroundHex = Fill;
     sharedPreferences.Display.StrokeHex = "#BC0D1E2F";
     Save(firstStore, sharedPreferences, "save preferences in the shared path");
     Check(File.Exists(firstStore.FilePath), "shared settings file exists");
     break;
    case "cross-build":
     var secondStore = SharedStore(project, "build-probe-two");
     var shared = Load(secondStore, "second build");
     CheckDisplay(shared, 43, Fill, "#BC0D1E2F", "second build reads shared values");
     ReadTiming(shared, 1.7, true, "second build");
     break;
    default: throw new ArgumentException("Unknown probe phase.");
   }
   return 0;
  } catch (Exception error) {
   Console.Error.WriteLine("FAIL " + error);
   return 1;
  }
 }
}
'@
[IO.File]::WriteAllText($probeSource, $source)

function Invoke-PreferencesProbe([string]$Probe, [string]$Phase) {
 & $Probe $project $Phase
 if ($LASTEXITCODE -ne 0) { throw "Preference probe failed in phase '$Phase' (exit code $LASTEXITCODE). Artifacts: $run" }
}

Write-Output "BUILD $($assembly.FullName) SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $assembly.FullName).Hash)"
Write-Output "ARTIFACTS $run"
& $compiler /nologo /target:exe /platform:x64 /utf8output ('/out:' + $probeOne) ('/reference:' + $assemblyOne) $probeSource
if ($LASTEXITCODE -ne 0) { throw "Preference probe compilation failed. Artifacts: $run" }
Copy-Item -LiteralPath $probeOne -Destination $probeTwo
Invoke-PreferencesProbe $probeOne 'save'
Invoke-PreferencesProbe $probeOne 'reopen-reset'
Invoke-PreferencesProbe $probeTwo 'verify-reset'
Invoke-PreferencesProbe $probeOne 'shared-write'
Invoke-PreferencesProbe $probeTwo 'cross-build'
Write-Output 'NOT CHECKED: application startup wiring, live appearance or palette interaction.'
