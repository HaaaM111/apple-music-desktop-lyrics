using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AMLyrics {
 public static class AppleMusicMonitor {
  const uint TH32CS_SNAPPROCESS = 0x00000002;
  const uint SYNCHRONIZE = 0x00100000;
  const uint WAIT_OBJECT_0 = 0x00000000;
  const uint WAIT_ABANDONED_0 = 0x00000080;
  const uint WAIT_TIMEOUT = 0x00000102;
  const uint WAIT_FAILED = 0xFFFFFFFF;
  const uint INFINITE = 0xFFFFFFFF;
  const int MAX_WAIT_PROCESSES = 60;
  const int ERROR_ACCESS_DENIED = 5;
  static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct PROCESSENTRY32W {
   public uint dwSize;
   public uint cntUsage;
   public uint th32ProcessID;
   public IntPtr th32DefaultHeapID;
   public uint th32ModuleID;
   public uint cntThreads;
   public uint th32ParentProcessID;
   public int pcPriClassBase;
   public uint dwFlags;
   [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
   public string szExeFile;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

  [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

  [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool CloseHandle(IntPtr hObject);

  public static int[] FindProcessIds(string targetExeName) {
   if (string.IsNullOrWhiteSpace(targetExeName)) return new int[0];
   string normalized = Path.GetFileName(targetExeName.Trim());
   if (string.IsNullOrEmpty(normalized)) return new int[0];
   string withExe = normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".exe";
   string withoutExe = normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
    ? normalized.Substring(0, normalized.Length - 4)
    : normalized;

   IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
   if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE) return new int[0];

   var result = new List<int>();
   try {
    var entry = new PROCESSENTRY32W();
    entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
    if (!Process32FirstW(snapshot, ref entry)) return new int[0];
    do {
     if (entry.th32ProcessID > 0 && !string.IsNullOrEmpty(entry.szExeFile)) {
      if (string.Equals(entry.szExeFile, withExe, StringComparison.OrdinalIgnoreCase) ||
       string.Equals(entry.szExeFile, withoutExe, StringComparison.OrdinalIgnoreCase)) {
       result.Add((int)entry.th32ProcessID);
      }
     }
     entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
    } while (Process32NextW(snapshot, ref entry));
   } finally {
    CloseHandle(snapshot);
   }
   return result.ToArray();
  }

  public static bool WaitForAllExit(int[] pids, WaitHandle stopHandle, int fallbackPollMs) {
   int pollMs = Math.Max(100, fallbackPollMs);
   if (stopHandle != null && stopHandle.WaitOne(0)) return false;
   if (pids == null || pids.Length == 0) return true;

   var processHandles = new List<IntPtr>(pids.Length);
   bool hasUnwaitableLiveProcess = false;
   SafeWaitHandle stopSafeHandle = stopHandle != null ? stopHandle.SafeWaitHandle : null;
   bool stopHandleRefAdded = false;

   try {
    if (stopSafeHandle != null) {
     stopSafeHandle.DangerousAddRef(ref stopHandleRefAdded);
    }

    for (int i = 0; i < pids.Length; i++) {
     int pid = pids[i];
     if (pid <= 0) continue;
     IntPtr hProcess = OpenProcess(SYNCHRONIZE, false, (uint)pid);
     if (hProcess != IntPtr.Zero) {
      processHandles.Add(hProcess);
     } else {
      int err = Marshal.GetLastWin32Error();
      if (err == ERROR_ACCESS_DENIED) {
       hasUnwaitableLiveProcess = true;
      }
     }
    }

    while (processHandles.Count > 0) {
     if (stopHandle != null && stopHandle.WaitOne(0)) return false;

     int batchCount = Math.Min(processHandles.Count, MAX_WAIT_PROCESSES);
     int offset = stopSafeHandle != null ? 1 : 0;
     var waitArray = new IntPtr[batchCount + offset];
     if (stopSafeHandle != null) {
      waitArray[0] = stopSafeHandle.DangerousGetHandle();
     }
     for (int i = 0; i < batchCount; i++) {
      waitArray[i + offset] = processHandles[i];
     }

     uint waitResult = WaitForMultipleObjects((uint)waitArray.Length, waitArray, false, INFINITE);
     if (waitResult == WAIT_FAILED || waitResult == WAIT_TIMEOUT) {
      if (WaitFallback(stopHandle, pollMs)) return false;
      return true;
     }

     if (stopSafeHandle != null && waitResult == WAIT_OBJECT_0) {
      return false;
     }

     int signaledIndex = -1;
     if (waitResult >= WAIT_OBJECT_0 && waitResult < WAIT_OBJECT_0 + waitArray.Length) {
      signaledIndex = (int)(waitResult - WAIT_OBJECT_0) - offset;
     } else if (waitResult >= WAIT_ABANDONED_0 && waitResult < WAIT_ABANDONED_0 + waitArray.Length) {
      int abandonedIdx = (int)(waitResult - WAIT_ABANDONED_0);
      if (stopSafeHandle != null && abandonedIdx == 0) return false;
      signaledIndex = abandonedIdx - offset;
     }

     if (signaledIndex >= 0 && signaledIndex < batchCount) {
      IntPtr exitedHandle = processHandles[signaledIndex];
      processHandles.RemoveAt(signaledIndex);
      CloseHandle(exitedHandle);
     } else {
      if (WaitFallback(stopHandle, pollMs)) return false;
      return true;
     }
    }

    if (hasUnwaitableLiveProcess) {
     if (WaitFallback(stopHandle, pollMs)) return false;
    }
    return true;
   } finally {
    for (int i = 0; i < processHandles.Count; i++) {
     if (processHandles[i] != IntPtr.Zero) CloseHandle(processHandles[i]);
    }
    if (stopHandleRefAdded && stopSafeHandle != null) {
     stopSafeHandle.DangerousRelease();
    }
   }
  }

  static bool WaitFallback(WaitHandle stopHandle, int pollMs) {
   if (stopHandle != null) return stopHandle.WaitOne(pollMs);
   Thread.Sleep(pollMs);
   return false;
  }
 }
}
