using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using AMLyrics;

class LifecycleTests {
 static int Main(string[] args) {
  if (args.Length > 0 && args[0] == "--sleep-child") {
   return RunSleepChild(args);
  }
  if (args.Length >= 3 && args[0] == "--ipc") {
   return LifecycleIpcTool.Send(args[1], string.Join(" ", args.Skip(2).ToArray()));
  }
  return LifecycleTestSuite.Run(args);
 }

 static int RunSleepChild(string[] args) {
  int timeoutMs = 60000;
  if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])) {
   using (var waitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, args[1])) {
    waitEvent.WaitOne(timeoutMs);
   }
   return 0;
  }
  Thread.Sleep(timeoutMs);
  return 0;
 }
}

static class LifecycleIpcTool {
 public static int Send(string scope, string commandText) {
  HostCommand command;
  if (!HostIpc.TryParseCommand(commandText, out command) &&
      !HostIpc.TryParseCommand("v1 " + commandText, out command)) {
   Console.Error.WriteLine("ERR Invalid command: " + commandText);
   return 2;
  }
  string pipeName = HostIpc.BuildPipeName(scope);
  HostStateSnapshot state;
  string error;
  if (!HostIpcClient.TrySend(pipeName, command.Kind, command.FlagValue, 1500, out state, out error)) {
   Console.Error.WriteLine("ERR " + (string.IsNullOrEmpty(error) ? "IPC failed" : error));
   return 1;
  }
  Console.WriteLine(string.Format(
   "OK Enabled={0} Locked={1} Visible={2} Polling={3}",
   state.Enabled, state.Locked, state.Visible, state.Polling));
  return 0;
 }
}

static class LifecycleTestSuite {
 static int total;
 static int failures;

 static void Check(bool ok, string name) {
  total++;
  Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
  if (!ok) failures++;
 }

 static bool WaitUntil(Func<bool> predicate, int timeoutMs) {
  var watch = Stopwatch.StartNew();
  while (watch.ElapsedMilliseconds < timeoutMs) {
   if (predicate()) return true;
   Thread.Sleep(40);
  }
  return predicate();
 }

 public static int Run(string[] args) {
  string tempRoot = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
   ? Path.GetFullPath(args[0])
   : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lifecycle-unit-" + Guid.NewGuid().ToString("N"));
  string dummyExePath = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
   ? Path.GetFullPath(args[1])
   : null;
  bool createdTempRoot = !Directory.Exists(tempRoot);
  Directory.CreateDirectory(tempRoot);

  try {
   TestHostIpcCodecAndScoping();
   TestHostIpcPipeRoundtrip();
   TestProcessJobGuard();
   TestWindowsMediaSourceLifecycle(tempRoot);
   TestAppleMusicMonitor(dummyExePath);
  } catch (Exception ex) {
   Console.WriteLine("FAIL Unhandled exception: " + ex);
   failures++;
  } finally {
   if (createdTempRoot) {
    try { Directory.Delete(tempRoot, true); } catch { }
   }
  }

  Console.WriteLine(total + " checks; " + failures + " failed.");
  return failures == 0 ? 0 : 1;
 }

 static void TestHostIpcCodecAndScoping() {
  string pipeDefault = HostIpc.BuildPipeName(null);
  string pipeEmpty = HostIpc.BuildPipeName("   ");
  string pipeA1 = HostIpc.BuildPipeName("scope-a");
  string pipeA2 = HostIpc.BuildPipeName("scope-a");
  string pipeB = HostIpc.BuildPipeName("scope-b");
  Check(!string.IsNullOrWhiteSpace(pipeDefault) && !string.IsNullOrWhiteSpace(pipeEmpty),
   "HostIpc: default and blank scopes produce non-empty pipe names");
  Check(pipeA1 == pipeA2 && pipeA1 != pipeB && pipeA1 != pipeDefault,
   "HostIpc: BuildPipeName is deterministic and isolates scopes");

  string mutexA1 = HostIpc.BuildMutexName("AMDesktopLyrics", "scope-a");
  string mutexA2 = HostIpc.BuildMutexName("AMDesktopLyrics", "scope-a");
  string mutexB = HostIpc.BuildMutexName("AMDesktopLyrics", "scope-b");
  string mutexOther = HostIpc.BuildMutexName("AMDesktopLyricsWatch", "scope-a");
  Check(mutexA1 == mutexA2 && mutexA1 != mutexB && mutexA1 != mutexOther,
   "HostIpc: BuildMutexName isolates both base name and scope");

  HostCommandKind[] kinds = new[] {
   HostCommandKind.SetEnabled,
   HostCommandKind.SetLocked,
   HostCommandKind.OpenSettings,
   HostCommandKind.GetState,
   HostCommandKind.ClientExited,
   HostCommandKind.Shutdown
  };

  for (int i = 0; i < kinds.Length; i++) {
   HostCommandKind kind = kinds[i];
   bool[] values = (kind == HostCommandKind.SetEnabled || kind == HostCommandKind.SetLocked)
    ? new[] { true, false }
    : new[] { false };
   for (int j = 0; j < values.Length; j++) {
    string formatted = HostIpc.FormatCommand(kind, values[j]);
    HostCommand parsed;
    bool ok = HostIpc.TryParseCommand(formatted, out parsed);
    bool match = ok && parsed != null && parsed.Kind == kind &&
     (!(kind == HostCommandKind.SetEnabled || kind == HostCommandKind.SetLocked) || parsed.FlagValue == values[j]);
    Check(match, "HostIpc: command roundtrip for " + kind + " (" + values[j] + ")");
   }
  }

  HostCommand paddedParsed;
  Check(HostIpc.TryParseCommand("  \uFEFFv1   GET_STATE  ", out paddedParsed) &&
   paddedParsed != null && paddedParsed.Kind == HostCommandKind.GetState,
   "HostIpc: TryParseCommand tolerates BOM, whitespace, and extra spacing");

  string[] invalidCommands = new[] {
   null, "", "   ", "GET_STATE", "v2 GET_STATE", "v1 get_state",
   "v1 set_enabled 1", "v1 UNKNOWN_COMMAND", "v1 SET_ENABLED",
   "v1 SET_ENABLED maybe", "v1 SET_LOCKED 2", "v1 GET_STATE extra"
  };
  bool rejectedAll = true;
  for (int i = 0; i < invalidCommands.Length; i++) {
   HostCommand bad;
   if (HostIpc.TryParseCommand(invalidCommands[i], out bad)) {
    rejectedAll = false;
    break;
   }
  }
  Check(rejectedAll, "HostIpc: TryParseCommand rejects null, empty, wrong-case/version, unknown, and malformed inputs");

  var stateA = new HostStateSnapshot { Enabled = true, Locked = false, Visible = true, Polling = true, ClientAvailable = true };
  string okLine = HostIpc.FormatOk(stateA);
  HostStateSnapshot parsedStateA;
  Check(HostIpc.TryParseOk(okLine, out parsedStateA) && parsedStateA != null &&
   parsedStateA.Enabled && !parsedStateA.Locked && parsedStateA.Visible && parsedStateA.Polling && parsedStateA.ClientAvailable,
   "HostIpc: FormatOk/TryParseOk preserves all state flags (1/0/1/1/1)");

  var stateB = new HostStateSnapshot { Enabled = false, Locked = true, Visible = false, Polling = false, ClientAvailable = false };
  HostStateSnapshot parsedStateB;
  Check(HostIpc.TryParseOk(HostIpc.FormatOk(stateB), out parsedStateB) && parsedStateB != null &&
   !parsedStateB.Enabled && parsedStateB.Locked && !parsedStateB.Visible && !parsedStateB.Polling && !parsedStateB.ClientAvailable,
   "HostIpc: FormatOk/TryParseOk preserves all state flags (0/1/0/0/0)");

  string errLine = HostIpc.FormatError("bad\r\ninput");
  HostStateSnapshot ignoredState;
  Check(!string.IsNullOrWhiteSpace(errLine) &&
   errLine.StartsWith("v1 ERR ", StringComparison.Ordinal) &&
   errLine.IndexOf('\n') < 0 &&
   !HostIpc.TryParseOk(errLine, out ignoredState) &&
   !HostIpc.TryParseOk("GARBAGE", out ignoredState) &&
   !HostIpc.TryParseOk("v1 OK enabled=1", out ignoredState) &&
   !HostIpc.TryParseOk(null, out ignoredState),
   "HostIpc: FormatError produces single-line ERR and TryParseOk rejects error/incomplete lines");
 }

 static void TestHostIpcPipeRoundtrip() {
  string scope = "ipc-test-" + Guid.NewGuid().ToString("N");
  string pipeName = HostIpc.BuildPipeName(scope);
  bool enabled = false;
  bool locked = false;
  bool visible = false;
  bool polling = false;
  bool clientExitedSeen = false;
  object stateGate = new object();

  using (var server = new HostIpcServer(pipeName, cmd => {
   lock (stateGate) {
    if (cmd.Kind == HostCommandKind.SetEnabled) {
     enabled = cmd.FlagValue;
     visible = cmd.FlagValue;
     polling = cmd.FlagValue;
    } else if (cmd.Kind == HostCommandKind.SetLocked) {
     locked = cmd.FlagValue;
    } else if (cmd.Kind == HostCommandKind.ClientExited) {
     clientExitedSeen = true;
     visible = false;
     polling = false;
    }
    return new HostStateSnapshot {
     Enabled = enabled,
     Locked = locked,
     Visible = visible,
     Polling = polling,
     ClientAvailable = true
    };
   }
  })) {
   server.Start();
   HostStateSnapshot snap;
   string err;

   bool sentEnable = HostIpcClient.TrySend(pipeName, HostCommandKind.SetEnabled, true, 2000, out snap, out err);
   Check(sentEnable && snap != null && snap.Enabled && snap.Visible && snap.Polling,
    "HostIpcServer/Client: SetEnabled(true) roundtrip over named pipe");

   bool sentGet = HostIpcClient.TrySend(pipeName, HostCommandKind.GetState, false, 2000, out snap, out err);
   Check(sentGet && snap != null && snap.Enabled && snap.Visible && snap.Polling,
    "HostIpcServer/Client: GetState reflects updated state");

   bool sentExited = HostIpcClient.TrySend(pipeName, HostCommandKind.ClientExited, false, 2000, out snap, out err);
   Check(sentExited && clientExitedSeen && snap != null && snap.Enabled && !snap.Visible && !snap.Polling,
    "HostIpcServer/Client: ClientExited reaches handler while preserving Enabled flag");

   string rawResponse = SendRawPipeLine(pipeName, "v1 UNKNOWN_COMMAND_XYZ");
   HostStateSnapshot dummy;
   Check(!string.IsNullOrWhiteSpace(rawResponse) &&
    rawResponse.StartsWith("v1 ERR ", StringComparison.Ordinal) &&
    !HostIpc.TryParseOk(rawResponse, out dummy),
    "HostIpcServer: unknown raw command returns v1 ERR response without crashing server");

   bool stillAlive = HostIpcClient.TrySend(pipeName, HostCommandKind.SetEnabled, false, 2000, out snap, out err);
   Check(stillAlive && snap != null && !snap.Enabled && !snap.Visible && !snap.Polling,
    "HostIpcServer/Client: server continues serving valid requests after unknown command");
  }

  HostStateSnapshot afterDispose;
  string closedError;
  bool sentAfterDispose = HostIpcClient.TrySend(pipeName, HostCommandKind.GetState, false, 250, out afterDispose, out closedError);
  Check(!sentAfterDispose, "HostIpcClient: TrySend returns false cleanly after server Dispose()");
 }

 static string SendRawPipeLine(string pipeName, string line) {
  using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) {
   pipe.Connect(2000);
   var utf8NoBom = new UTF8Encoding(false);
   using (var writer = new StreamWriter(pipe, utf8NoBom, 1024, true))
   using (var reader = new StreamReader(pipe, utf8NoBom, false, 1024, true)) {
    writer.AutoFlush = true;
    writer.WriteLine(line);
    return reader.ReadLine();
   }
  }
 }

 static void TestProcessJobGuard() {
  string selfExe = Assembly.GetEntryAssembly().Location;
  Process child = null;
  try {
   child = Process.Start(new ProcessStartInfo {
    FileName = selfExe,
    Arguments = "--sleep-child",
    UseShellExecute = false,
    CreateNoWindow = true
   });
   Check(child != null && !child.HasExited, "ProcessJobGuard: launched benign child process");

   using (var guard = new ProcessJobGuard()) {
    Check(!guard.TryAssign(null), "ProcessJobGuard: TryAssign(null) returns false safely");
    bool assigned = guard.TryAssign(child);
    Check(assigned && !child.HasExited, "ProcessJobGuard: TryAssign attaches active child without terminating early");
    guard.Dispose();
    guard.Dispose();
    bool exited = child.WaitForExit(3000);
    Check(exited && child.HasExited, "ProcessJobGuard: Dispose() immediately terminates assigned child process");
   }
  } finally {
   if (child != null) {
    try { if (!child.HasExited) child.Kill(); } catch { }
    child.Dispose();
   }
  }
 }

 static void TestWindowsMediaSourceLifecycle(string tempRoot) {
  string scriptPath = Path.Combine(tempRoot, "fake-media-" + Guid.NewGuid().ToString("N") + ".ps1");
  string pidFile = scriptPath + ".pid";
  string scriptContent = string.Join(Environment.NewLine, new[] {
   "param([int]$ParentProcessId = 0)",
   "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)",
   "$pidFile = $PSCommandPath + '.pid'",
   "[IO.File]::WriteAllText($pidFile, $PID.ToString())",
   "$sample = @{",
   " Status = 'Connected'",
   " SourceAppId = 'AppleMusic.LifecycleTest'",
   " Title = 'Lifecycle Track'",
   " Artist = 'QA Artist'",
   " Album = 'QA Album'",
   " IsPlaying = $true",
   " PlaybackRate = 1.0",
   " PositionSeconds = 10.0",
   " DurationSeconds = 180.0",
   " TimelineReliable = $true",
   " CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')",
   "} | ConvertTo-Json -Compress",
   "[Console]::WriteLine($sample)",
   "$owner = $null",
   "if ($ParentProcessId -gt 0) { $owner = [Diagnostics.Process]::GetProcessById($ParentProcessId) }",
   "while ($true) {",
   " if ($null -ne $owner -and $owner.HasExited) { break }",
   " Start-Sleep -Milliseconds 150",
   "}"
  });
  File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(false));

  Process trackedWorker = null;
  try {
   using (var source = new WindowsMediaSource(scriptPath, false)) {
    IMediaSourceLifecycle lifecycle = source;
    Check(!lifecycle.IsPolling, "WindowsMediaSource(script, false): IsPolling initially false");

    for (int i = 0; i < 5; i++) {
     MediaSample idleSample = source.Read();
     if (idleSample == null || idleSample.Track != null) {
      Check(false, "WindowsMediaSource(script, false): Read() does not return track before StartPolling");
      return;
     }
     Thread.Sleep(40);
    }
    Check(!lifecycle.IsPolling && !File.Exists(pidFile),
     "WindowsMediaSource(script, false): multiple Read() calls do not launch PowerShell worker");

    source.StartPolling();
    Check(lifecycle.IsPolling, "WindowsMediaSource: StartPolling() sets IsPolling=true");

    bool started = WaitUntil(() => {
     if (!File.Exists(pidFile)) return false;
     MediaSample s = source.Read();
     return s != null && s.Status == "Connected" && s.Track != null && s.Track.Title == "Lifecycle Track";
    }, 8000);
    Check(started, "WindowsMediaSource: StartPolling() launches worker and receives sample");

    int workerPid = int.Parse(File.ReadAllText(pidFile).Trim());
    trackedWorker = Process.GetProcessById(workerPid);
    Check(!trackedWorker.HasExited, "WindowsMediaSource: PowerShell worker process is running while polling");

    source.StopPolling();
    Check(!lifecycle.IsPolling, "WindowsMediaSource: StopPolling() sets IsPolling=false");
    bool workerExited = trackedWorker.WaitForExit(3000);
    Check(workerExited && trackedWorker.HasExited, "WindowsMediaSource: StopPolling() terminates PowerShell worker");

    source.ResetSample();
    MediaSample reset = source.Read();
    Check(reset != null && reset.Track == null && reset.Status != "Connected",
     "WindowsMediaSource: ResetSample() clears previous track sample");
   }
  } finally {
   if (trackedWorker != null) {
    try { if (!trackedWorker.HasExited) trackedWorker.Kill(); } catch { }
    trackedWorker.Dispose();
   }
   try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
   try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
  }
 }

 static void TestAppleMusicMonitor(string dummyExePath) {
  bool useDummy = !string.IsNullOrEmpty(dummyExePath) && File.Exists(dummyExePath);
  string targetExePath = useDummy ? dummyExePath : Assembly.GetEntryAssembly().Location;
  string targetExeName = Path.GetFileName(targetExePath);
  int selfPid = Process.GetCurrentProcess().Id;
  string exitEventName = @"Local\AMMonitorProbeExit_" + Guid.NewGuid().ToString("N");

  Process p1 = null;
  Process p2 = null;
  try {
   int[] initialPids = AppleMusicMonitor.FindProcessIds(targetExeName).Where(id => id != selfPid).ToArray();
   Check(initialPids != null && initialPids.Length == 0,
    "AppleMusicMonitor: FindProcessIds returns empty when target is not running");

   using (var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, exitEventName))
   using (var stopOnWait = new ManualResetEvent(false))
   using (var stopNever = new ManualResetEvent(false)) {
    var startInfo = new ProcessStartInfo {
     FileName = targetExePath,
     Arguments = useDummy ? exitEventName : ("--sleep-child " + exitEventName),
     UseShellExecute = false,
     CreateNoWindow = true
    };
    p1 = Process.Start(startInfo);
    p2 = Process.Start(startInfo);

    int[] foundPids = new int[0];
    bool bothFound = WaitUntil(() => {
     foundPids = AppleMusicMonitor.FindProcessIds(targetExeName).Where(id => id != selfPid).ToArray();
     return foundPids != null && foundPids.Length == 2 &&
      foundPids.Contains(p1.Id) && foundPids.Contains(p2.Id);
    }, 4000);
    Check(bothFound, "AppleMusicMonitor: FindProcessIds locates multiple target instances");

    using (var timer = new Timer(_ => stopOnWait.Set(), null, 200, Timeout.Infinite)) {
     bool exitedBeforeStop = AppleMusicMonitor.WaitForAllExit(foundPids, stopOnWait, 50);
     Check(!exitedBeforeStop, "AppleMusicMonitor: WaitForAllExit returns false when stopHandle is signaled");
    }

    using (var timer = new Timer(_ => exitEvent.Set(), null, 150, Timeout.Infinite)) {
     bool exitedAll = AppleMusicMonitor.WaitForAllExit(foundPids, stopNever, 50);
     Check(exitedAll, "AppleMusicMonitor: WaitForAllExit returns true when all target processes exit");
    }

    int[] remaining = AppleMusicMonitor.FindProcessIds(targetExeName).Where(id => id != selfPid).ToArray();
    Check(remaining != null && remaining.Length == 0,
     "AppleMusicMonitor: FindProcessIds returns empty after target exit");
   }
  } finally {
   foreach (Process p in new[] { p1, p2 }) {
    if (p != null) {
     try { if (!p.HasExited) p.Kill(); } catch { }
     p.Dispose();
    }
   }
  }
 }
}
