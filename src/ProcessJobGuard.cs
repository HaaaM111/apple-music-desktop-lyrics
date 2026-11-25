using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AMLyrics {
 [StructLayout(LayoutKind.Sequential)]
 internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
  public long PerProcessUserTimeLimit;
  public long PerJobUserTimeLimit;
  public uint LimitFlags;
  public UIntPtr MinimumWorkingSetSize;
  public UIntPtr MaximumWorkingSetSize;
  public uint ActiveProcessLimit;
  public UIntPtr Affinity;
  public uint PriorityClass;
  public uint SchedulingClass;
 }

 [StructLayout(LayoutKind.Sequential)]
 internal struct IO_COUNTERS {
  public ulong ReadOperationCount;
  public ulong WriteOperationCount;
  public ulong OtherOperationCount;
  public ulong ReadTransferCount;
  public ulong WriteTransferCount;
  public ulong OtherTransferCount;
 }

 [StructLayout(LayoutKind.Sequential)]
 internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
  public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
  public IO_COUNTERS IoInfo;
  public UIntPtr ProcessMemoryLimit;
  public UIntPtr JobMemoryLimit;
  public UIntPtr PeakProcessMemoryUsed;
  public UIntPtr PeakJobMemoryUsed;
 }

 public sealed class ProcessJobGuard : IDisposable {
  const int JobObjectExtendedLimitInformation = 9;
  const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

  readonly object gate = new object();
  IntPtr jobHandle;
  bool disposed;

  public ProcessJobGuard() {
   IntPtr handle = IntPtr.Zero;
   try {
    handle = CreateJobObject(IntPtr.Zero, null);
    if (handle == IntPtr.Zero) return;

    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    uint length = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, length)) {
     CloseHandle(handle);
     return;
    }

    jobHandle = handle;
   } catch {
    if (handle != IntPtr.Zero) {
     try { CloseHandle(handle); } catch {}
    }
    jobHandle = IntPtr.Zero;
   }
  }

  ~ProcessJobGuard() {
   DisposeHandle();
  }

  public bool TryAssign(Process process) {
   if (process == null) return false;
   lock (gate) {
    if (disposed || jobHandle == IntPtr.Zero) return false;
    try {
     if (process.HasExited) return false;
     IntPtr processHandle = process.Handle;
     if (processHandle == IntPtr.Zero) return false;
     return AssignProcessToJobObject(jobHandle, processHandle);
    } catch {
     return false;
    }
   }
  }

  public void Dispose() {
   DisposeHandle();
   GC.SuppressFinalize(this);
  }

  void DisposeHandle() {
   lock (gate) {
    disposed = true;
    if (jobHandle != IntPtr.Zero) {
     try { CloseHandle(jobHandle); } catch {}
     jobHandle = IntPtr.Zero;
    }
   }
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool SetInformationJobObject(
   IntPtr hJob,
   int JobObjectInfoClass,
   [In] ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
   uint cbJobObjectInfoLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool CloseHandle(IntPtr hObject);
 }
}
