using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AMLyrics {
 public sealed class LyricsHitTestBridge : IDisposable {
  const int WmNcHitTest = 0x0084;
  const int HtTransparent = -1;
  const int GwlExStyle = -20;
  const int WsExTransparent = 0x00000020;
  readonly Window window;
  HwndSource source;
  bool clickThrough;
  bool disposed;

  public LyricsHitTestBridge(Window window) {
   if (window == null) throw new ArgumentNullException("window");
   this.window = window;
   window.SourceInitialized += OnSourceInitialized;
   if (PresentationSource.FromVisual(window) != null) Attach();
  }
  public bool IsClickThrough { get { return clickThrough; } }

  public void SetClickThrough(bool value) {
   if (disposed) throw new ObjectDisposedException("LyricsHitTestBridge");
   clickThrough = value;
   if (source != null) ApplyNativeStyle();
  }

  void OnSourceInitialized(object sender, EventArgs e) { Attach(); }
  void Attach() {
   if (source != null) return;
   source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
   if (source != null) source.AddHook(WindowProc);
   ApplyNativeStyle();
  }

  IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
   if (clickThrough && message == WmNcHitTest) { handled = true; return new IntPtr(HtTransparent); }
   return IntPtr.Zero;
  }

  void ApplyNativeStyle() {
   if (source == null) return;
   int style = GetWindowLong(source.Handle, GwlExStyle);
   if (clickThrough) style |= WsExTransparent; else style &= ~WsExTransparent;
   SetWindowLong(source.Handle, GwlExStyle, style);
  }

  public void Dispose() {
   if (disposed) return;
   disposed = true;
   if (source != null) {
    source.RemoveHook(WindowProc);
    int style = GetWindowLong(source.Handle, GwlExStyle) & ~WsExTransparent;
    SetWindowLong(source.Handle, GwlExStyle, style);
   }
   window.SourceInitialized -= OnSourceInitialized;
  }

  [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] static extern int GetWindowLong(IntPtr window, int index);
  [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] static extern int SetWindowLong(IntPtr window, int index, int value);
 }
}
