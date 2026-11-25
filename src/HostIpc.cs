using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AMLyrics {
 public enum HostCommandKind {
  SetEnabled,
  SetLocked,
  OpenSettings,
  GetState,
  ClientExited,
  Shutdown
 }

 public sealed class HostCommand {
  public HostCommandKind Kind;
  public bool FlagValue;
 }

 public sealed class HostStateSnapshot {
  public bool Enabled;
  public bool Locked;
  public bool Visible;
  public bool Polling;
  public bool ClientAvailable;
 }

 public static class HostIpc {
  const string ProtocolVersion = "v1";

  public static string BuildPipeName(string scope = null) {
   string sid = GetCurrentUserSid();
   string hash = ComputeShortHash(sid);
   string sanitizedScope = SanitizeScope(scope);
   if (string.IsNullOrEmpty(sanitizedScope)) return "AMLyrics.Host." + hash;
   return "AMLyrics.Host." + hash + "." + sanitizedScope;
  }

  public static string BuildMutexName(string baseName, string scope = null) {
   string effectiveBase = string.IsNullOrWhiteSpace(baseName) ? "AMLyrics" : baseName;
   if (string.IsNullOrWhiteSpace(scope)) return "Local\\" + effectiveBase;
   string sanitizedScope = SanitizeScope(scope);
   return "Local\\" + effectiveBase + "." + sanitizedScope;
  }

  public static string FormatCommand(HostCommandKind kind, bool flagValue = false) {
   switch (kind) {
    case HostCommandKind.SetEnabled:
     return ProtocolVersion + " SET_ENABLED " + (flagValue ? "1" : "0");
    case HostCommandKind.SetLocked:
     return ProtocolVersion + " SET_LOCKED " + (flagValue ? "1" : "0");
    case HostCommandKind.OpenSettings:
     return ProtocolVersion + " OPEN_SETTINGS";
    case HostCommandKind.GetState:
     return ProtocolVersion + " GET_STATE";
    case HostCommandKind.ClientExited:
     return ProtocolVersion + " CLIENT_EXITED";
    case HostCommandKind.Shutdown:
     return ProtocolVersion + " SHUTDOWN";
    default:
     throw new ArgumentOutOfRangeException("kind");
   }
  }

  public static bool TryParseCommand(string line, out HostCommand command) {
   command = null;
   if (string.IsNullOrWhiteSpace(line)) return false;

   string trimmed = line.Trim().TrimStart('\uFEFF');
   string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
   if (parts.Length < 2 || !string.Equals(parts[0], ProtocolVersion, StringComparison.Ordinal)) return false;

   string verb = parts[1];
   if (string.Equals(verb, "SET_ENABLED", StringComparison.Ordinal)) {
    bool flag;
    if (parts.Length != 3 || !TryParseFlag(parts[2], out flag)) return false;
    command = new HostCommand { Kind = HostCommandKind.SetEnabled, FlagValue = flag };
    return true;
   }
   if (string.Equals(verb, "SET_LOCKED", StringComparison.Ordinal)) {
    bool flag;
    if (parts.Length != 3 || !TryParseFlag(parts[2], out flag)) return false;
    command = new HostCommand { Kind = HostCommandKind.SetLocked, FlagValue = flag };
    return true;
   }
   if (string.Equals(verb, "OPEN_SETTINGS", StringComparison.Ordinal)) {
    if (parts.Length != 2) return false;
    command = new HostCommand { Kind = HostCommandKind.OpenSettings };
    return true;
   }
   if (string.Equals(verb, "GET_STATE", StringComparison.Ordinal)) {
    if (parts.Length != 2) return false;
    command = new HostCommand { Kind = HostCommandKind.GetState };
    return true;
   }
   if (string.Equals(verb, "CLIENT_EXITED", StringComparison.Ordinal)) {
    if (parts.Length != 2) return false;
    command = new HostCommand { Kind = HostCommandKind.ClientExited };
    return true;
   }
   if (string.Equals(verb, "SHUTDOWN", StringComparison.Ordinal)) {
    if (parts.Length != 2) return false;
    command = new HostCommand { Kind = HostCommandKind.Shutdown };
    return true;
   }

   return false;
  }

  public static string FormatOk(HostStateSnapshot state) {
   var s = state ?? new HostStateSnapshot();
   return string.Format(
    "{0} OK enabled={1} locked={2} visible={3} polling={4} client={5}",
    ProtocolVersion,
    s.Enabled ? "1" : "0",
    s.Locked ? "1" : "0",
    s.Visible ? "1" : "0",
    s.Polling ? "1" : "0",
    s.ClientAvailable ? "1" : "0");
  }

  public static string FormatError(string message) {
   string clean = SanitizeSingleLine(message);
   if (string.IsNullOrEmpty(clean)) clean = "Unknown error.";
   return ProtocolVersion + " ERR " + clean;
  }

  public static bool TryParseOk(string line, out HostStateSnapshot state) {
   state = null;
   if (string.IsNullOrWhiteSpace(line)) return false;

   string trimmed = line.Trim().TrimStart('\uFEFF');
   string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
   if (parts.Length < 7) return false;
   if (!string.Equals(parts[0], ProtocolVersion, StringComparison.Ordinal)) return false;
   if (!string.Equals(parts[1], "OK", StringComparison.Ordinal)) return false;

   var parsed = new HostStateSnapshot();
   bool hasEnabled = false;
   bool hasLocked = false;
   bool hasVisible = false;
   bool hasPolling = false;
   bool hasClient = false;

   for (int i = 2; i < parts.Length; i++) {
    int eq = parts[i].IndexOf('=');
    if (eq <= 0 || eq == parts[i].Length - 1) return false;
    string key = parts[i].Substring(0, eq);
    string val = parts[i].Substring(eq + 1);
    bool flag;
    if (!TryParseFlag(val, out flag)) return false;

    if (string.Equals(key, "enabled", StringComparison.Ordinal)) {
     parsed.Enabled = flag;
     hasEnabled = true;
    } else if (string.Equals(key, "locked", StringComparison.Ordinal)) {
     parsed.Locked = flag;
     hasLocked = true;
    } else if (string.Equals(key, "visible", StringComparison.Ordinal)) {
     parsed.Visible = flag;
     hasVisible = true;
    } else if (string.Equals(key, "polling", StringComparison.Ordinal)) {
     parsed.Polling = flag;
     hasPolling = true;
    } else if (string.Equals(key, "client", StringComparison.Ordinal)) {
     parsed.ClientAvailable = flag;
     hasClient = true;
    } else {
     return false;
    }
   }

   if (!hasEnabled || !hasLocked || !hasVisible || !hasPolling || !hasClient) return false;
   state = parsed;
   return true;
  }

  static bool TryParseFlag(string token, out bool value) {
   if (string.Equals(token, "1", StringComparison.Ordinal)) {
    value = true;
    return true;
   }
   if (string.Equals(token, "0", StringComparison.Ordinal)) {
    value = false;
    return true;
   }
   value = false;
   return false;
  }

  static string GetCurrentUserSid() {
   try {
    using (var identity = WindowsIdentity.GetCurrent()) {
     if (identity != null && identity.User != null && !string.IsNullOrEmpty(identity.User.Value)) {
      return identity.User.Value;
     }
    }
   } catch {}
   return Environment.UserDomainName + "\\" + Environment.UserName;
  }

  static string ComputeShortHash(string input) {
   using (var sha = SHA256.Create()) {
    byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
    var sb = new StringBuilder(24);
    for (int i = 0; i < 12; i++) {
     sb.Append(bytes[i].ToString("x2"));
    }
    return sb.ToString();
   }
  }

  internal static string SanitizeScope(string scope) {
   if (string.IsNullOrWhiteSpace(scope)) return "";
   var sb = new StringBuilder(scope.Length);
   for (int i = 0; i < scope.Length; i++) {
    char c = scope[i];
    bool allowed = (c >= 'A' && c <= 'Z') ||
                   (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') ||
                   c == '_' ||
                   c == '-';
    sb.Append(allowed ? c : '_');
   }
   return sb.ToString();
  }

  static string SanitizeSingleLine(string message) {
   if (string.IsNullOrWhiteSpace(message)) return "";
   var sb = new StringBuilder(message.Length);
   bool lastSpace = false;
   for (int i = 0; i < message.Length; i++) {
    char c = message[i];
    if (c == '\r' || c == '\n' || c == '\t') c = ' ';
    if (char.IsControl(c)) continue;
    if (c == ' ') {
     if (!lastSpace && sb.Length > 0) {
      sb.Append(' ');
      lastSpace = true;
     }
    } else {
     sb.Append(c);
     lastSpace = false;
    }
   }
   return sb.ToString().Trim();
  }
 }

 public sealed class HostIpcServer : IDisposable {
  const int MaxServerInstances = 4;

  readonly object gate = new object();
  readonly string pipeName;
  readonly Func<HostCommand, HostStateSnapshot> handler;
  readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
  Thread acceptThread;
  bool started;
  bool disposed;

  public HostIpcServer(string pipeName, Func<HostCommand, HostStateSnapshot> handler) {
   if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", "pipeName");
   if (handler == null) throw new ArgumentNullException("handler");
   this.pipeName = pipeName;
   this.handler = handler;
  }

  public void Start() {
   lock (gate) {
    if (disposed || started) return;
    started = true;
    acceptThread = new Thread(AcceptLoop);
    acceptThread.IsBackground = true;
    acceptThread.Name = "AMLyrics.HostIpcServer";
    acceptThread.Start();
   }
  }

  public void Dispose() {
   Thread threadToJoin = null;
   lock (gate) {
    if (disposed) return;
    disposed = true;
    stopEvent.Set();
    threadToJoin = acceptThread;
   }

   bool joined = true;
   if (threadToJoin != null && Thread.CurrentThread != threadToJoin) {
    joined = threadToJoin.Join(1000);
   }
   if (joined) {
    stopEvent.Dispose();
   }
  }

  void AcceptLoop() {
   while (!disposed) {
    NamedPipeServerStream server = null;
    try {
     server = CreatePipeServerStream();
     IAsyncResult ar = server.BeginWaitForConnection(null, null);
     if (!ar.IsCompleted) {
      int signaled = WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, stopEvent });
      if (signaled == 1 || disposed) {
       SafeDisposeStream(server);
       break;
      }
     }

     if (disposed) {
      SafeDisposeStream(server);
      break;
     }

     server.EndWaitForConnection(ar);
     var connected = server;
     server = null;
     ThreadPool.QueueUserWorkItem(HandleClient, connected);
    } catch (ObjectDisposedException) {
     SafeDisposeStream(server);
     if (disposed) break;
    } catch (IOException) {
     SafeDisposeStream(server);
     if (disposed) break;
     stopEvent.WaitOne(20);
    } catch {
     SafeDisposeStream(server);
     if (disposed) break;
     stopEvent.WaitOne(50);
    }
   }
  }

  NamedPipeServerStream CreatePipeServerStream() {
   var security = BuildCurrentUserSecurity();
   return new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    MaxServerInstances,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous,
    4096,
    4096,
    security);
  }

  static PipeSecurity BuildCurrentUserSecurity() {
   var security = new PipeSecurity();
   using (var identity = WindowsIdentity.GetCurrent()) {
    if (identity != null && identity.User != null) {
     security.AddAccessRule(new PipeAccessRule(
      identity.User,
      PipeAccessRights.FullControl,
      AccessControlType.Allow));
    }
   }
   return security;
  }

  void HandleClient(object state) {
   var pipe = (NamedPipeServerStream)state;
   try {
    var utf8 = new UTF8Encoding(false);
    using (pipe)
    using (var reader = new StreamReader(pipe, utf8, false, 1024, true))
    using (var writer = new StreamWriter(pipe, utf8, 1024, true)) {
     string line = reader.ReadLine();
     if (line == null) return;

     string response;
     HostCommand command;
     if (!HostIpc.TryParseCommand(line, out command)) {
      response = HostIpc.FormatError("Invalid command.");
     } else {
      try {
       HostStateSnapshot snapshot = handler(command);
       response = HostIpc.FormatOk(snapshot);
      } catch (Exception ex) {
       response = HostIpc.FormatError(ex.Message);
      }
     }

     writer.Write(response);
     writer.Write('\n');
     writer.Flush();
     try {
      if (pipe.IsConnected) pipe.WaitForPipeDrain();
     } catch {}
    }
   } catch (IOException) {
   } catch (ObjectDisposedException) {
   } catch (InvalidOperationException) {
   } catch {
   }
  }

  static void SafeDisposeStream(NamedPipeServerStream stream) {
   if (stream == null) return;
   try { stream.Dispose(); } catch {}
  }
 }

 public static class HostIpcClient {
  const string ErrorPrefix = "v1 ERR ";

  public static bool TrySend(string pipeName, string commandLine, int timeoutMs, out string response) {
   response = null;
   if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(commandLine)) return false;

   int effectiveTimeout = timeoutMs < 0 ? 0 : timeoutMs;
   var stopwatch = Stopwatch.StartNew();

   try {
    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
     client.Connect(effectiveTimeout);

     var utf8 = new UTF8Encoding(false);
     using (var writer = new StreamWriter(client, utf8, 1024, true))
     using (var reader = new StreamReader(client, utf8, false, 1024, true)) {
      string cleanCommand = commandLine.TrimEnd('\r', '\n');
      writer.Write(cleanCommand);
      writer.Write('\n');
      writer.Flush();

      int remainingMs = effectiveTimeout == 0
       ? 100
       : Math.Max(50, effectiveTimeout - (int)stopwatch.ElapsedMilliseconds);

      Task<string> readTask = reader.ReadLineAsync();
      readTask.ContinueWith(ObserveFaultedTask, TaskContinuationOptions.OnlyOnFaulted);

      if (!readTask.Wait(remainingMs)) return false;
      response = readTask.Result;
      return !string.IsNullOrEmpty(response);
     }
    }
   } catch {
    response = null;
    return false;
   }
  }

  public static bool TrySend(
   string pipeName,
   HostCommandKind kind,
   bool flagValue,
   int timeoutMs,
   out HostStateSnapshot state,
   out string error) {
   state = null;
   error = null;

   try {
    string commandLine = HostIpc.FormatCommand(kind, flagValue);
    string response;
    if (!TrySend(pipeName, commandLine, timeoutMs, out response)) {
     error = "IPC connection failed.";
     return false;
    }

    if (HostIpc.TryParseOk(response, out state)) {
     return true;
    }

    string trimmed = response == null ? "" : response.Trim().TrimStart('\uFEFF');
    if (trimmed.StartsWith(ErrorPrefix, StringComparison.Ordinal)) {
     string msg = trimmed.Substring(ErrorPrefix.Length).Trim();
     error = string.IsNullOrEmpty(msg) ? "Server error." : msg;
    } else {
     error = "Invalid IPC response.";
    }
    return false;
   } catch (Exception ex) {
    state = null;
    error = ex.Message;
    return false;
   }
  }

  static void ObserveFaultedTask(Task task) {
   var ignored = task.Exception;
  }
 }
}
