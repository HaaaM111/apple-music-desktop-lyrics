using System;
using System.Collections.Generic;
using System.Windows;

namespace AMLyrics {
 public enum LyricsLongLineMode { Fit, Marquee, Ellipsis }

 public sealed class LyricsDisplaySettings {
  public const double MinimumFontSize = LyricsLayoutMetrics.MinimumFontSize;
  public const double MaximumFontSize = LyricsLayoutMetrics.MaximumFontSize;
  public double FontSize = 30;
  public string ForegroundHex = "#FFF7FA";
  public string FontFamilyName = "Segoe UI";
  public string FontWeightName = "常规";
  public string StrokeHex = "#0F0F12";
  public double BackgroundOpacity;
  public bool StrokeEnabled = true;
  public bool ShadowEnabled = true;
  public bool AnimateLongLines = true;

  public LyricsDisplaySettings Clone() {
   return new LyricsDisplaySettings {
    FontSize = FontSize, ForegroundHex = ForegroundHex, FontFamilyName = FontFamilyName,
    FontWeightName = FontWeightName, StrokeHex = StrokeHex,
    BackgroundOpacity = BackgroundOpacity, StrokeEnabled = StrokeEnabled,
    ShadowEnabled = ShadowEnabled, AnimateLongLines = AnimateLongLines
   };
  }

  public void Normalize() {
   FontSize = Clamp(FontSize, MinimumFontSize, MaximumFontSize);
   BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1);
   if (string.IsNullOrWhiteSpace(ForegroundHex)) ForegroundHex = "#FFF7FA";
   if (string.IsNullOrWhiteSpace(FontFamilyName)) FontFamilyName = "Segoe UI";
   if (string.IsNullOrWhiteSpace(FontWeightName)) FontWeightName = "常规";
   if (string.IsNullOrWhiteSpace(StrokeHex)) StrokeHex = "#0F0F12";
  }

  static double Clamp(double value, double minimum, double maximum) {
   return Math.Max(minimum, Math.Min(maximum, value));
  }
 }

 public sealed class LyricsWindowPreferences {
  public const double MinimumWidth = LyricsLayoutMetrics.MinimumWidth;
  public const double MaximumWidth = LyricsLayoutMetrics.MaximumWidth;
  public const double DefaultWidth = LyricsLayoutMetrics.DefaultWidth;
  public bool Enabled;
  public bool Locked;
  public double Width = DefaultWidth;
  public double Left = double.NaN;
  public double Top = double.NaN;
  public string ScreenDeviceName = "";
  public LyricsDisplaySettings Display = new LyricsDisplaySettings();
  public Dictionary<string,double> TimingOffsets = new Dictionary<string,double>();

  public LyricsWindowPreferences Clone() {
   return new LyricsWindowPreferences {
    Enabled = Enabled, Locked = Locked, Width = Width, Left = Left, Top = Top,
    ScreenDeviceName = ScreenDeviceName ?? "",
    Display = Display == null ? new LyricsDisplaySettings() : Display.Clone(),
    TimingOffsets = TimingOffsets == null ? new Dictionary<string,double>() : new Dictionary<string,double>(TimingOffsets)
   };
  }

  public void Normalize() {
   Width = Math.Max(MinimumWidth, Math.Min(MaximumWidth, Width));
   if (Display == null) Display = new LyricsDisplaySettings();
   Display.Normalize();
   TimingOffsets = LyricTiming.NormalizeOffsets(TimingOffsets);
  }
 }

 public static class LyricsWindowGeometry {
  public static Rect KeepVisible(Rect desired, Rect workArea, double margin) {
   double width = Math.Min(desired.Width, workArea.Width);
   double height = Math.Min(desired.Height, workArea.Height);
   double left = Clamp(desired.Left, workArea.Left + margin, workArea.Right - width - margin);
   double top = Clamp(desired.Top, workArea.Top + margin, workArea.Bottom - height - margin);
   return new Rect(left, top, width, height);
  }

  static double Clamp(double value, double minimum, double maximum) {
   if (maximum < minimum) return minimum;
   return Math.Max(minimum, Math.Min(maximum, value));
  }
 }
}
