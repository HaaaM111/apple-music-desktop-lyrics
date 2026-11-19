using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AMLyrics {
 public sealed partial class LyricsWindow : Window {
  // Neutral OKLCH roles mapped to sRGB: surface L~0.19, ink L~0.96, muted L~0.75.
  internal static readonly Brush Surface = Brush("#141416");
  internal static readonly Brush Ink = Brush("#F4F4F5");
  internal static readonly Brush Muted = Brush("#B0B0B8");
  internal static readonly Brush Accent = Brush("#F59BAC");
  readonly TextBlock title = new TextBlock();
  readonly LyricsLinePresenter current = new LyricsLinePresenter();
  readonly LyricsLinePresenter next = new LyricsLinePresenter();
  readonly TextBlock status = new TextBlock();
  readonly TextBlock source = new TextBlock();
  readonly IMediaSource media;
  readonly LocalLyricsProvider local;
  readonly LyricSession lyrics;
  readonly LrclibLyricsProvider online;
  readonly Button timingButton = new Button();
  readonly LyricTiming timing = new LyricTiming();
  readonly LyricsDisplaySettings displaySettings = new LyricsDisplaySettings();
  readonly LyricsOverlayChrome overlayChrome;
  readonly LyricsSettingsPanel settingsPanel;
  readonly LyricsHitTestBridge hitTestBridge;
  readonly ILyricsWindowCommands windowCommands;
  readonly LyricsPreferencesStore preferencesStore;
  readonly bool smokeMode;
  bool applyingPreferences;
  string configurationNotice = string.Empty;
  readonly Grid contentRoot;
  readonly Border leftResizeHandle;
  readonly Border rightResizeHandle;
  int attempts;
  double selectedDuration;
  DateTime retryAt = DateTime.MaxValue;
  readonly DispatcherTimer timer;
  readonly Forms.NotifyIcon tray;
  string selectedKey = "";
  string notice = "";
  string noticeKey = "";
  bool importing, closing;
  double lyricSize = LyricsLayoutMetrics.DefaultFontSize;
  int displayedLineIndex = -1;
  IntPtr handle;
  bool hotkeyRegistered;
  bool lockHotkeyRegistered;
  bool dragging;
  bool resizing;
  bool resizingLeft;
  Point resizeStartScreen;
  double resizeStartWidth;
  double resizeStartLeft;
  Task pendingSelection = Task.FromResult(0);
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
  static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
  public LyricsWindow(IMediaSource media, LocalLyricsProvider local, bool smoke) {
   smokeMode = smoke;
   preferencesStore = new LyricsPreferencesStore();
   this.media = media; this.local = local;
   online = media is TestMediaSource ? null : new LrclibLyricsProvider();
   lyrics = new LyricSession(online == null ? new LyricsProviders(local) : new LyricsProviders(local, online));
   displaySettings.Normalize();
   Title = "Apple Music 桌面歌词";
   Width = LyricsLayoutMetrics.DefaultWidth;
   SizeToContent = SizeToContent.Height;
   MinWidth = LyricsLayoutMetrics.MinimumWidth; MaxWidth = LyricsLayoutMetrics.MaximumWidth;
   MinHeight = LyricsLayoutMetrics.MinimumHeight; MaxHeight = LyricsLayoutMetrics.MaximumHeight;
   Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent; Foreground = Ink;
   WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
   WindowStartupLocation = WindowStartupLocation.CenterScreen;
   FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
   FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
   AutomationProperties.SetName(this, "Apple Music 桌面歌词");
   Resources.Add(typeof(Button), XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
    <Setter Property='Foreground' Value='#D4D4DA'/><Setter Property='Background' Value='Transparent'/>
    <Setter Property='Padding' Value='9,4'/><Setter Property='MinHeight' Value='28'/><Setter Property='MinWidth' Value='30'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
     <Border x:Name='B' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' CornerRadius='4' BorderThickness='1' BorderBrush='Transparent' Padding='{TemplateBinding Padding}'>
      <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
     <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#303034'/></Trigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#F59BAC'/></Trigger>
      <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#424248'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter></Style>"));

   overlayChrome = new LyricsOverlayChrome();
   settingsPanel = new LyricsSettingsPanel(RetryLyrics, () => { var ignored = ImportAsync(); }, (delta) => ChangeTiming(delta, false), () => ChangeTiming(0, true));
   hitTestBridge = new LyricsHitTestBridge(this);
   windowCommands = new LyricsWindowCommandRouter(SetEnabled, SetLocked, OpenSettings);
   overlayChrome.SettingsRequested += (s,e) => windowCommands.OpenSettings();
   overlayChrome.ToggleLockRequested += (s,e) => windowCommands.SetLocked(!overlayChrome.IsLocked);
   overlayChrome.CloseRequested += (s,e) => windowCommands.SetEnabled(false);
   overlayChrome.SourceRequested += (s,e) => { OpenSettings(); settingsPanel.FocusSection("歌词"); };
   overlayChrome.TimingRequested += (s,e) => { OpenSettings(); settingsPanel.FocusSection("同步"); };
   overlayChrome.PlaybackRequested += (s,e) => { configurationNotice = "播放控制将在 Apple Music 客户端接入完成后启用"; Refresh(); };
   overlayChrome.SetPlaybackAvailable(false);
   settingsPanel.DismissRequested += (s,e) => CloseSettings();
   settingsPanel.AppearanceChanged += (s,e) => {
    var appearance = settingsPanel.ReadAppearance();
    displaySettings.FontSize = appearance.FontSize;
    displaySettings.ForegroundHex = appearance.ForegroundHex;
    displaySettings.FontFamilyName = appearance.FontFamilyName;
    displaySettings.FontWeightName = appearance.FontWeightName;
    displaySettings.StrokeHex = appearance.StrokeHex;
    displaySettings.BackgroundOpacity = appearance.BackgroundOpacity;
    displaySettings.StrokeEnabled = appearance.StrokeEnabled;
    displaySettings.ShadowEnabled = appearance.ShadowEnabled;
    displaySettings.AnimateLongLines = appearance.AnimateLongLines;
    ApplyDisplaySettings();
    SavePreferences();
   };

   current.ForegroundBrush = Ink; current.LyricsFontWeight = FontWeights.SemiBold;
   next.ForegroundBrush = Muted;
   current.Text = "等待 Apple Music";
   next.Text = "播放歌曲后，在这里显示歌词";
   AutomationProperties.SetName(current, "当前歌词或状态");
   AutomationProperties.SetName(next, "下一句歌词或说明");

   contentRoot = new Grid { Background = Brushes.Transparent };
   var tracks = new Grid { Margin = new Thickness(LyricsLayoutMetrics.HorizontalPadding, 0, LyricsLayoutMetrics.HorizontalPadding, 4), VerticalAlignment = VerticalAlignment.Top };
   tracks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   tracks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   Grid.SetRow(current, 0); Grid.SetRow(next, 1);
   next.Margin = new Thickness(0, LyricsLayoutMetrics.TrackGap, 0, 0);
   tracks.Children.Add(current); tracks.Children.Add(next);
   tracks.IsHitTestVisible = false;
   Grid.SetZIndex(overlayChrome, 10); contentRoot.Children.Add(overlayChrome);
   Grid.SetZIndex(tracks, 20); contentRoot.Children.Add(tracks);
   contentRoot.MouseLeftButtonDown += (s,e) => {
    if (overlayChrome.IsLocked || settingsPanel.Visibility == Visibility.Visible || resizing) return;
    dragging = true;
    overlayChrome.SetHovered(false);
    try { DragMove(); KeepWindowVisible(); } catch (InvalidOperationException) { }
    finally { dragging = false; }
    e.Handled = true;
   };

   leftResizeHandle = MakeResizeHandle(true);
   rightResizeHandle = MakeResizeHandle(false);
   contentRoot.Children.Add(leftResizeHandle);
   contentRoot.Children.Add(rightResizeHandle);
   Grid.SetZIndex(leftResizeHandle, 30); Grid.SetZIndex(rightResizeHandle, 30);
   settingsPanel.Visibility = Visibility.Collapsed;
   settingsPanel.Margin = new Thickness(12);
   var settingsLayer = new Canvas();
   Canvas.SetRight(settingsPanel, 0); Canvas.SetTop(settingsPanel, 0);
   settingsLayer.Children.Add(settingsPanel);
   Grid.SetZIndex(settingsLayer, 50); contentRoot.Children.Add(settingsLayer);
   Content = contentRoot;

   timingButton.Content = "默认";
   Bind(Key.O, ModifierKeys.Control, () => { var ignored = ImportAsync(); });
   Bind(Key.Q, ModifierKeys.Control, Close); Bind(Key.Escape, ModifierKeys.None, CloseSettingsOrHide);
   Bind(Key.H, ModifierKeys.Control, HideWindow); Bind(Key.OemPlus, ModifierKeys.Control, () => ResizeLyrics(2));
   Bind(Key.Add, ModifierKeys.Control, () => ResizeLyrics(2)); Bind(Key.OemMinus, ModifierKeys.Control, () => ResizeLyrics(-2));
   Bind(Key.Subtract, ModifierKeys.Control, () => ResizeLyrics(-2));
   Bind(Key.Left, ModifierKeys.Control, () => ChangeTiming(0.2, false));
   Bind(Key.Right, ModifierKeys.Control, () => ChangeTiming(-0.2, false));
   Bind(Key.D0, ModifierKeys.Control, () => ChangeTiming(0, true));
   if (!smoke) {
    tray = new Forms.NotifyIcon { Text = "Apple Music 桌面歌词 · 双击显示", Icon = System.Drawing.SystemIcons.Application, Visible = true };
    var menu = new Forms.ContextMenuStrip();
    menu.Items.Add("显示歌词", null, (s,e) => Dispatcher.BeginInvoke(new Action(RestoreWindow)));
    menu.Items.Add("退出", null, (s,e) => Dispatcher.BeginInvoke(new Action(Close)));
    tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
   }
   SourceInitialized += (s,e) => {
    handle = new WindowInteropHelper(this).Handle;
    if (!smoke) {
     hotkeyRegistered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, 0x4C);
     lockHotkeyRegistered = RegisterHotKey(handle, 2, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x4C);
     HwndSource.FromHwnd(handle).AddHook(WndProc);
    }
   };
   timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
   timer.Tick += (s,e) => {
    if (!(smokeMode && media is TestMediaSource)) overlayChrome.SetHovered(!dragging && !resizing && LyricsOverlayHover.IsPointerInside(this, overlayChrome.HoverToolbarSurface));
    Refresh();
   };
   IsVisibleChanged += (s,e) => { if (!IsVisible) overlayChrome.SetHovered(false); };
   SizeChanged += (s,e) => { if (e.HeightChanged) KeepWindowVisible(); };
   Loaded += (s,e) => { LoadPreferences(); ApplyDisplaySettings(); Refresh(); KeepWindowVisible(); timer.Start(); };
   Closed += (s,e) => {
    SavePreferences();
    closing = true; timer.Stop(); hitTestBridge.Dispose(); lyrics.Dispose(); if (online != null) online.Dispose(); media.Dispose();
    if (hotkeyRegistered) UnregisterHotKey(handle, 1);
    if (lockHotkeyRegistered) UnregisterHotKey(handle, 2);
    if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
   };
  }
  Border MakeResizeHandle(bool left) {
   var handle = new Border { Width = 8, Background = Brushes.Transparent, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Cursor = Cursors.SizeWE, Focusable = false };
   handle.MouseLeftButtonDown += (s,e) => BeginResize(left, e);
   handle.MouseMove += (s,e) => ContinueResize(e);
   handle.MouseLeftButtonUp += (s,e) => EndResize(e);
   return handle;
  }
  void BeginResize(bool left, MouseButtonEventArgs e) {
   if (overlayChrome.IsLocked || settingsPanel.Visibility == Visibility.Visible) return;
   resizing = true; resizingLeft = left; resizeStartWidth = Width; resizeStartLeft = Left;
   resizeStartScreen = PointToScreen(e.GetPosition(this));
   ((UIElement)e.Source).CaptureMouse(); e.Handled = true;
  }
  void ContinueResize(MouseEventArgs e) {
   if (!resizing) return;
   Point currentScreen = PointToScreen(e.GetPosition(this));
   double delta = currentScreen.X - resizeStartScreen.X;
   double width = LyricsLayoutMetrics.ClampWidth(resizingLeft ? resizeStartWidth - delta : resizeStartWidth + delta);
   if (resizingLeft) Left = resizeStartLeft + (resizeStartWidth - width);
   Width = width; KeepWindowVisible(); e.Handled = true;
  }
  void EndResize(MouseButtonEventArgs e) {
   if (!resizing) return;
   resizing = false; Mouse.Capture(null); KeepWindowVisible(); SavePreferences(); e.Handled = true;
  }
  void SetEnabled(bool enabled) { if (enabled) RestoreWindow(); else HideWindow(); SavePreferences(); }
  void OpenSettings() {
   if (overlayChrome.IsLocked) return;
   settingsPanel.Visibility = Visibility.Visible;
   hitTestBridge.SetClickThrough(false);
   UpdateLayout();
   settingsPanel.FocusFirstControl();
  }
  void CloseSettings() {
   settingsPanel.Visibility = Visibility.Collapsed;
   hitTestBridge.SetClickThrough(overlayChrome.IsLocked);
  }
  void CloseSettingsOrHide() { if (settingsPanel.Visibility == Visibility.Visible) CloseSettings(); else HideWindow(); }
  void SetLocked(bool value) {
   if (value) CloseSettings();
   overlayChrome.SetLocked(value);
   // Keep native hit testing enabled so the visible unlock button remains clickable.
   hitTestBridge.SetClickThrough(false);
   leftResizeHandle.IsHitTestVisible = !value; rightResizeHandle.IsHitTestVisible = !value;
   overlayChrome.SetInteractionEnabled(!value);
   SavePreferences();
  }
  void ApplyDisplaySettings() {
   displaySettings.Normalize(); lyricSize = LyricsLayoutMetrics.ClampFontSize(displaySettings.FontSize);
   current.RequestedFontSize = lyricSize;
   next.RequestedFontSize = LyricsLayoutMetrics.NextLineFontSize(lyricSize);
   current.AnimateLongLines = displaySettings.AnimateLongLines;
   next.AnimateLongLines = displaySettings.AnimateLongLines;
   Brush foreground;
   try { foreground = Brush(displaySettings.ForegroundHex); } catch (Exception) { foreground = Ink; }
   Brush stroke;
   try { stroke = Brush(displaySettings.StrokeHex); } catch (Exception) { stroke = Brush("#0F0F12"); }
   FontFamily family;
   try { family = new FontFamily(displaySettings.FontFamilyName); } catch (Exception) { family = new FontFamily("Segoe UI"); }
   FontWeight weight = FontWeights.Normal;
   if (displaySettings.FontWeightName == "半粗") weight = FontWeights.SemiBold;
   else if (displaySettings.FontWeightName == "粗体") weight = FontWeights.Bold;
   current.ForegroundBrush = foreground; next.ForegroundBrush = new SolidColorBrush(Color.FromArgb(190, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.R : (byte)176, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.G : (byte)176, foreground is SolidColorBrush ? ((SolidColorBrush)foreground).Color.B : (byte)184));
   current.StrokeBrush = stroke; next.StrokeBrush = stroke;
   current.StrokeEnabled = displaySettings.StrokeEnabled; next.StrokeEnabled = displaySettings.StrokeEnabled;
   current.ShadowEnabled = displaySettings.ShadowEnabled; next.ShadowEnabled = displaySettings.ShadowEnabled;
   current.LyricsFontFamily = family; next.LyricsFontFamily = family;
   current.LyricsFontWeight = weight; next.LyricsFontWeight = weight;
   overlayChrome.SetBackdropOpacity(displaySettings.BackgroundOpacity);
  }
  void KeepWindowVisible() {
   if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width) || ActualHeight <= 0) return;
   Rect workArea = SystemParameters.WorkArea;
   Rect visible = LyricsWindowGeometry.KeepVisible(new Rect(Left, Top, Width, ActualHeight), workArea, 0);
   Left = visible.Left; Top = visible.Top;
   current.AlignInkToTop = visible.Top <= workArea.Top + 0.5;
  }
  void LoadPreferences() {
   if (smokeMode && media is TestMediaSource) return;
   var result = preferencesStore.Load();
   configurationNotice = result.Status == LyricsPreferencesLoadStatus.Loaded || result.Status == LyricsPreferencesLoadStatus.DefaultsUsed ? string.Empty : "设置未加载：" + result.Error;
   var preferences = result.Preferences;
   if (smokeMode) preferences.Enabled = true;
   applyingPreferences = true;
   try { ApplyPreferencesFromPersistence(preferences); }
   finally { applyingPreferences = false; }
  }
  void SavePreferences() {
   if (smokeMode || applyingPreferences) return;
   var result = preferencesStore.Save(ReadPreferencesForPersistence());
   configurationNotice = result.Succeeded ? string.Empty : "设置未保存：" + result.Error;
  }
  string CurrentScreenDeviceName() {
   try {
    IntPtr hwnd = new WindowInteropHelper(this).Handle;
    return hwnd == IntPtr.Zero ? Forms.Screen.PrimaryScreen.DeviceName : Forms.Screen.FromHandle(hwnd).DeviceName;
   } catch { return string.Empty; }
  }
  // T2b persistence hook: the future store can read/write this in-memory state without changing host behavior.
  internal LyricsWindowPreferences ReadPreferencesForPersistence() {
   return new LyricsWindowPreferences { Enabled = IsVisible, Locked = overlayChrome.IsLocked, Width = Width, Left = Left, Top = Top, ScreenDeviceName = CurrentScreenDeviceName(), Display = displaySettings.Clone(), TimingOffsets = timing.ReadOffsets() };
  }
  internal void ApplyPreferencesFromPersistence(LyricsWindowPreferences preferences) {
   if (preferences == null) return;
   var copy = preferences.Clone(); copy.Normalize();
   timing.LoadOffsets(copy.TimingOffsets);
   Width = copy.Width; if (!double.IsNaN(copy.Left)) Left = copy.Left; if (!double.IsNaN(copy.Top)) Top = copy.Top;
   displaySettings.FontSize = copy.Display.FontSize; displaySettings.ForegroundHex = copy.Display.ForegroundHex; displaySettings.FontFamilyName = copy.Display.FontFamilyName;
   displaySettings.FontWeightName = copy.Display.FontWeightName; displaySettings.StrokeHex = copy.Display.StrokeHex; displaySettings.BackgroundOpacity = copy.Display.BackgroundOpacity;
   displaySettings.StrokeEnabled = copy.Display.StrokeEnabled; displaySettings.ShadowEnabled = copy.Display.ShadowEnabled; displaySettings.AnimateLongLines = copy.Display.AnimateLongLines;
   ApplyDisplaySettings(); SetLocked(copy.Locked); KeepWindowVisible(); if (copy.Enabled) RestoreWindow(); else HideWindow();
  }
  Button MakeButton(string text, string name, Action action) {
   var b = new Button { Content = text, ToolTip = name, Margin = new Thickness(2,0,0,0) };
   AutomationProperties.SetName(b, name); b.Click += (s,e) => action(); return b;
  }
  void Bind(Key key, ModifierKeys mods, Action action) {
   var command = new RoutedCommand(); CommandBindings.Add(new CommandBinding(command, (s,e) => action()));
   InputBindings.Add(new KeyBinding(command, new KeyGesture(key, mods)));
  }
  IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) {
   if (msg == 0x0312 && wparam.ToInt32() == 1) { RestoreWindow(); handled = true; }
   else if (msg == 0x0312 && wparam.ToInt32() == 2) { SetLocked(!overlayChrome.IsLocked); handled = true; }
   return IntPtr.Zero;
  }
  public void RestoreWindow() { Refresh(); Show(); WindowState = WindowState.Normal; KeepWindowVisible(); if (ShowActivated) Activate(); }
  void HideWindow() { CloseSettings(); Hide(); }
  void ResizeLyrics(double delta) { lyricSize = LyricsLayoutMetrics.ClampFontSize(lyricSize + delta); displaySettings.FontSize = lyricSize; ApplyDisplaySettings(); SavePreferences(); }
 }
}
