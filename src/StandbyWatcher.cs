using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AMLyrics {
 public static class StandbyWatcher {
  const int DefaultDebounceMs = 1500;
  const int DefaultPollMs = 1500;

  sealed class WatcherOptions {
   public bool Stop;
   public bool Status;
   public string TargetProcess = "AppleMusic.exe";
   public string HostExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppleMusicLyrics.exe");
   public string SettingsPath;
   public string IpcScope;
   public int DebounceMs = DefaultDebounceMs;
   public int PollMs = DefaultPollMs;
  }

  public static int Main(string[] args) {
   WatcherOptions options;
   if (!TryParseArgs(args, out options)) return 2;

   string watchMutexName = HostIpc.BuildMutexName("AMDesktopLyrics.Watch.SingleInstance", options.IpcScope);
   string stopEventName = HostIpc.BuildMutexName("AMDesktopLyrics.Watch.Stop", options.IpcScope);
   string hostMutexName = HostIpc.BuildMutexName("AMDesktopLyrics.SingleInstance", options.IpcScope);
   string pipeName = HostIpc.BuildPipeName(options.IpcScope);

   if (options.Status) {
    return HandleStatus(watchMutexName);
   }

   if (options.Stop) {
    return HandleStop(watchMutexName, stopEventName);
   }

   bool createdNew;
   using (var singleInstanceMutex = new Mutex(true, watchMutexName, out createdNew)) {
    if (!createdNew) return 0;

    try {
     using (var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName)) {
      stopEvent.Reset();
      RunWatchLoop(options, stopEvent, hostMutexName, pipeName);
     }
     return 0;
    } finally {
     try { singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
    }
   }
  }

  static int HandleStatus(string watchMutexName) {
   Mutex existing;
   if (Mutex.TryOpenExisting(watchMutexName, out existing)) {
    existing.Dispose();
    Console.WriteLine("RUNNING");
    return 0;
   }
   Console.WriteLine("STOPPED");
   return 1;
  }

  static int HandleStop(string watchMutexName, string stopEventName) {
   EventWaitHandle stopEvent;
   if (EventWaitHandle.TryOpenExisting(stopEventName, out stopEvent)) {
    using (stopEvent) {
     stopEvent.Set();
    }
   }

   Mutex watchMutex;
   if (Mutex.TryOpenExisting(watchMutexName, out watchMutex)) {
    using (watchMutex) {
     bool acquired = false;
     try {
      acquired = watchMutex.WaitOne(3000);
     } catch (AbandonedMutexException) {
      acquired = true;
     } finally {
      if (acquired) {
       try { watchMutex.ReleaseMutex(); } catch (ApplicationException) { }
      }
     }
    }
   }
   return 0;
  }

  static void RunWatchLoop(
   WatcherOptions options,
   EventWaitHandle stopEvent,
   string hostMutexName,
   string pipeName) {
   while (!stopEvent.WaitOne(0)) {
    int[] pids = AppleMusicMonitor.FindProcessIds(options.TargetProcess);
    if (pids.Length == 0) {
     if (stopEvent.WaitOne(options.PollMs)) break;
     continue;
    }

    EnsureHostRunning(options, hostMutexName, pipeName);

    if (!AppleMusicMonitor.WaitForAllExit(pids, stopEvent, options.PollMs)) {
     break;
    }

    if (options.DebounceMs > 0 && stopEvent.WaitOne(options.DebounceMs)) {
     break;
    }

    int[] remainingPids = AppleMusicMonitor.FindProcessIds(options.TargetProcess);
    if (remainingPids.Length > 0) {
     continue;
    }

    NotifyHostClientExited(pipeName, stopEvent);
   }
  }

  static void EnsureHostRunning(WatcherOptions options, string hostMutexName, string pipeName) {
   HostStateSnapshot state;
   string error;
   if (HostIpcClient.TrySend(pipeName, HostCommandKind.GetState, false, 400, out state, out error)) {
    return;
   }

   Mutex existingHostMutex;
   if (Mutex.TryOpenExisting(hostMutexName, out existingHostMutex)) {
    existingHostMutex.Dispose();
    return;
   }

   if (string.IsNullOrWhiteSpace(options.HostExe) || !File.Exists(options.HostExe)) {
    return;
   }

   string fullHostExe = Path.GetFullPath(options.HostExe);
   string workingDir = Path.GetDirectoryName(fullHostExe);
   var startInfo = new ProcessStartInfo {
    FileName = fullHostExe,
    Arguments = BuildHostArguments(options),
    WorkingDirectory = string.IsNullOrEmpty(workingDir) ? AppDomain.CurrentDomain.BaseDirectory : workingDir,
    UseShellExecute = false,
    CreateNoWindow = true
   };

   try {
    using (var proc = Process.Start(startInfo)) { }
   } catch (Exception) {
   }
  }

  static void NotifyHostClientExited(string pipeName, EventWaitHandle stopEvent) {
   for (int attempt = 0; attempt < 4; attempt++) {
    HostStateSnapshot state;
    string error;
    if (HostIpcClient.TrySend(pipeName, HostCommandKind.ClientExited, false, 400, out state, out error)) {
     return;
    }
    if (attempt < 3 && stopEvent.WaitOne(250)) {
     return;
    }
   }
  }

  static string BuildHostArguments(WatcherOptions options) {
   var sb = new StringBuilder("--auto-launch");
   if (!string.IsNullOrWhiteSpace(options.SettingsPath)) {
    sb.Append(" --settings ").Append(QuoteArgument(options.SettingsPath));
   }
   if (!string.IsNullOrWhiteSpace(options.IpcScope)) {
    sb.Append(" --ipc-scope ").Append(QuoteArgument(options.IpcScope));
   }
   return sb.ToString();
  }

  static string QuoteArgument(string argument) {
   if (string.IsNullOrEmpty(argument)) return "\"\"";
   if (argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;

   var sb = new StringBuilder();
   sb.Append('"');
   for (int i = 0; i < argument.Length; i++) {
    int backslashes = 0;
    while (i < argument.Length && argument[i] == '\\') {
     backslashes++;
     i++;
    }
    if (i == argument.Length) {
     sb.Append('\\', backslashes * 2);
     break;
    }
    if (argument[i] == '"') {
     sb.Append('\\', backslashes * 2 + 1);
     sb.Append('"');
    } else {
     sb.Append('\\', backslashes);
     sb.Append(argument[i]);
    }
   }
   sb.Append('"');
   return sb.ToString();
  }

  static bool TryParseArgs(string[] args, out WatcherOptions options) {
   options = new WatcherOptions();
   if (args == null) return true;

   for (int i = 0; i < args.Length; i++) {
    string arg = args[i];
    if (string.Equals(arg, "--stop", StringComparison.OrdinalIgnoreCase)) {
     options.Stop = true;
    } else if (string.Equals(arg, "--status", StringComparison.OrdinalIgnoreCase)) {
     options.Status = true;
    } else if (string.Equals(arg, "--target-process", StringComparison.OrdinalIgnoreCase)) {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return false;
     options.TargetProcess = args[++i].Trim();
    } else if (string.Equals(arg, "--host-exe", StringComparison.OrdinalIgnoreCase)) {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return false;
     options.HostExe = args[++i];
    } else if (string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)) {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return false;
     options.SettingsPath = args[++i];
    } else if (string.Equals(arg, "--ipc-scope", StringComparison.OrdinalIgnoreCase)) {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return false;
     options.IpcScope = args[++i];
    } else if (string.Equals(arg, "--debounce-ms", StringComparison.OrdinalIgnoreCase)) {
     int value;
     if (i + 1 >= args.Length || !int.TryParse(args[++i], out value) || value < 0) return false;
     options.DebounceMs = value;
    } else if (string.Equals(arg, "--poll-ms", StringComparison.OrdinalIgnoreCase)) {
     int value;
     if (i + 1 >= args.Length || !int.TryParse(args[++i], out value) || value <= 0) return false;
     options.PollMs = value;
    } else {
     return false;
    }
   }
   return true;
  }
 }
}
