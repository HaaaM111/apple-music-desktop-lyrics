using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
namespace AMLyrics {
 public static class Program {
  [STAThread]
  public static int Main(string[] args) {
   bool smoke = false, live = false, autoLaunch = false, hasDesktop = false;
   string folderArg = null, settingsPath = null, ipcScope = null;
   Guid desktop = Guid.Empty;
   for (int i = 0; i < args.Length; i++) {
    string arg = args[i];
    if (arg == "--ui-smoke") {
     smoke = true;
     if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) folderArg = args[++i];
    } else if (arg == "--live-check") {
     live = true;
     if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) folderArg = args[++i];
    } else if (arg == "--auto-launch") {
     autoLaunch = true;
    } else if (arg == "--desktop") {
     if (i + 1 >= args.Length || !Guid.TryParse(args[++i], out desktop)) return 2;
     hasDesktop = true;
    } else if (arg == "--settings") {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return 2;
     settingsPath = Path.GetFullPath(args[++i]);
    } else if (arg == "--ipc-scope") {
     if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return 2;
     ipcScope = args[++i];
    } else {
     return 2;
    }
   }
   bool verification = smoke || live;
   string directory = AppDomain.CurrentDomain.BaseDirectory;
   string singleInstanceMutex = HostIpc.BuildMutexName("AMDesktopLyrics.SingleInstance", ipcScope);
   string showEventName = HostIpc.BuildMutexName("AMDesktopLyrics.Show", ipcScope);
   string pipeName = HostIpc.BuildPipeName(ipcScope);
   bool first;
   using (var mutex = new Mutex(true, verification ? null : singleInstanceMutex, out first)) {
    if (!first && !verification) {
     if (autoLaunch) return 0;
     HostStateSnapshot existingState;
     string ipcError;
     if (!HostIpcClient.TrySend(pipeName, HostCommandKind.SetEnabled, true, 1500, out existingState, out ipcError)) {
      try { using (var existing = EventWaitHandle.OpenExisting(showEventName)) existing.Set(); }
      catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("桌面歌词正在启动，请稍后重试。", "桌面歌词"); }
     }
     return 0;
    }
    using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, verification ? null : showEventName))
    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset)) {
     var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
     IMediaSource media = smoke ? (IMediaSource)new TestMediaSource() : new WindowsMediaSource(Path.Combine(directory, "MediaSession.ps1"), false);
     var window = new LyricsWindow(media, new LocalLyricsProvider(), verification, autoLaunch, settingsPath);
     int result = 0;
     Task listener = null;
     HostIpcServer ipcServer = null;
     if (!verification) {
      listener = Task.Run(() => {
       while (WaitHandle.WaitAny(new WaitHandle[] { show, stop }) == 0) {
        if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(() => window.SetEnabled(true)));
       }
      });
      ipcServer = new HostIpcServer(pipeName, cmd => app.Dispatcher.Invoke(() => {
       switch (cmd.Kind) {
        case HostCommandKind.SetEnabled: window.SetEnabled(cmd.FlagValue); break;
        case HostCommandKind.SetLocked: window.SetLockedFromHost(cmd.FlagValue); break;
        case HostCommandKind.OpenSettings: window.OpenSettingsFromHost(); break;
        case HostCommandKind.GetState: break;
        case HostCommandKind.ClientExited: window.ShutdownForClientExit(); break;
        case HostCommandKind.Shutdown: window.ShutdownPreservingEnabled(); break;
       }
       return window.ReadHostState();
      }));
      ipcServer.Start();
     } else {
      string folder = !string.IsNullOrWhiteSpace(folderArg) ? Path.GetFullPath(folderArg) : directory;
      window.ShowActivated = false; window.ShowInTaskbar = false;
      window.Loaded += async (s,e) => {
       await Task.Yield();
       try {
        Directory.CreateDirectory(folder);
        using (var log = new StreamWriter(Path.Combine(folder, live ? "live-check.txt" : "ui-smoke.txt"), false, System.Text.Encoding.UTF8)) {
         Action<bool,string> check = (ok, name) => { log.WriteLine((ok ? "PASS " : "FAIL ") + name); log.Flush(); if (!ok) result = 1; };
         if (live) await window.LiveCheckAsync(folder, desktop, check);
         else {
          await window.SmokeAsync(folder, check);
          if (desktop != Guid.Empty) {
           bool onCurrent;
           check(DesktopPlacement.Locate(window, out onCurrent) == desktop && !window.ShowActivated, "Smoke test stays on requested desktop without requesting activation");
          }
         }
        }
       } catch (Exception ex) { File.WriteAllText(Path.Combine(folder,live ? "live-check-error.txt" : "ui-smoke-error.txt"), ex.ToString()); result = 1; }
       finally { window.Close(); }
      };
     }
     try {
      if (verification) {
       if (hasDesktop) {
        window.ShowActivated = false;
        window.WindowState = WindowState.Minimized;
        window.Show();
        DesktopPlacement.MoveOwnWindow(window, desktop);
        window.WindowState = WindowState.Normal;
       } else {
        window.Show();
       }
      } else {
       window.InitializeStartupState();
       if (window.ShouldStartVisible) {
        if (hasDesktop) {
         window.ShowActivated = false;
         window.WindowState = WindowState.Minimized;
         window.Show();
         DesktopPlacement.MoveOwnWindow(window, desktop);
         window.WindowState = WindowState.Normal;
        } else {
         window.Show();
        }
       } else {
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
       }
      }
      app.Run();
     } catch (Exception ex) {
      result = 1;
      if (verification && !string.IsNullOrWhiteSpace(folderArg)) {
       string failureFolder = Path.GetFullPath(folderArg);
       Directory.CreateDirectory(failureFolder);
       File.WriteAllText(Path.Combine(failureFolder, "startup-error.txt"), ex.ToString());
      }
      window.Close();
     }
     finally {
      if (ipcServer != null) ipcServer.Dispose();
      stop.Set();
      if (listener != null) listener.Wait(2000);
      media.Dispose();
     }
     return result;
    }
   }
  }
}
}
