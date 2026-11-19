using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AMLyrics {
 internal static class LyricsOverlayHover {
  internal static bool IsPointerInside(Window window, FrameworkElement toolbarSurface = null) {
   if (window == null || !window.IsVisible || window.WindowState == WindowState.Minimized) return false;

   var source = PresentationSource.FromVisual(window) as HwndSource;
   if (source == null || source.Handle == IntPtr.Zero) return false;

   ScreenPoint pointer;
   ScreenRect bounds;
   if (!GetCursorPos(out pointer) || !GetWindowRect(source.Handle, out bounds)) return false;
   if (Contains(bounds, pointer)) return true;

   if (toolbarSurface == null || !toolbarSurface.IsVisible) return false;
   var toolbarSource = PresentationSource.FromVisual(toolbarSurface) as HwndSource;
   if (toolbarSource == null || toolbarSource.Handle == IntPtr.Zero || toolbarSource.Handle == source.Handle) return false;

   return GetWindowRect(toolbarSource.Handle, out bounds) && Contains(bounds, pointer);
  }

  static bool Contains(ScreenRect bounds, ScreenPoint pointer) {
   return pointer.X >= bounds.Left && pointer.X < bounds.Right &&
          pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct ScreenPoint {
   internal int X, Y;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct ScreenRect {
   internal int Left, Top, Right, Bottom;
  }

  [DllImport("user32.dll", ExactSpelling = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool GetCursorPos(out ScreenPoint point);

  [DllImport("user32.dll", ExactSpelling = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool GetWindowRect(IntPtr window, out ScreenRect bounds);
 }
}
