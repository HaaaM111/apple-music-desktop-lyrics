using System;
using System.Collections.Generic;
using System.Linq;

namespace AMLyrics {
 public sealed class ResolvedLyricTracks {
  public string Primary = "";
  public string Secondary = "";
  public string Tertiary = "";
  public bool ShowTertiary;
  public bool HasTranslation;
  public bool HasRomanization;
 }

 public static class LyricsTrackResolver {
  public static void ExtractParts(LyricLine line, out string primary, out string romanization, out string translation) {
   if (line == null) {
    primary = "";
    romanization = "";
    translation = "";
    return;
   }

   romanization = (line.Romanization ?? "").Trim();
   translation = (line.Translation ?? "").Trim();
   string rawText = line.Text ?? "";

   if (rawText.IndexOf('\n') >= 0) {
    List<string> parts = rawText
     .Split('\n')
     .Select(p => p.Trim())
     .Where(p => !string.IsNullOrEmpty(p))
     .ToList();

    if (parts.Count == 0) {
     primary = "";
    } else {
     primary = parts[0];
     if (translation.Length == 0 && romanization.Length == 0) {
      if (parts.Count == 2) {
       translation = parts[1];
      } else if (parts.Count >= 3) {
       romanization = parts[1];
       translation = parts[2];
      }
     }
    }
   } else {
    primary = rawText.Trim();
   }
  }

  public static ResolvedLyricTracks Resolve(LyricDocument doc, int index, bool showTranslation, bool showRomanization) {
   return ResolveLines(doc == null ? null : doc.Lines, index, showTranslation, showRomanization);
  }

  public static ResolvedLyricTracks ResolveLines(IList<LyricLine> lines, int index, bool showTranslation, bool showRomanization) {
   var result = new ResolvedLyricTracks();
   if (lines == null || lines.Count == 0) {
    return result;
   }

   bool hasTranslation = false;
   bool hasRomanization = false;
   for (int i = 0; i < lines.Count; i++) {
    string p, r, t;
    ExtractParts(lines[i], out p, out r, out t);
    if (t.Length > 0) hasTranslation = true;
    if (r.Length > 0) hasRomanization = true;
    if (hasTranslation && hasRomanization) break;
   }

   result.HasTranslation = hasTranslation;
   result.HasRomanization = hasRomanization;

   string orig = "";
   string rom = "";
   string trans = "";
   if (index < 0) {
    orig = "前奏";
   } else if (index >= 0 && index < lines.Count) {
    ExtractParts(lines[index], out orig, out rom, out trans);
    if (orig.Length == 0) orig = "间奏";
   }

   string nextOrig = "";
   if (index + 1 >= 0 && index + 1 < lines.Count) {
    string nextRom, nextTrans;
    ExtractParts(lines[index + 1], out nextOrig, out nextRom, out nextTrans);
   }

   if (!showTranslation && !showRomanization) {
    result.Primary = orig;
    result.Secondary = nextOrig;
    result.Tertiary = "";
    result.ShowTertiary = false;
   } else if (showTranslation && !showRomanization) {
    result.Primary = orig;
    result.Secondary = trans.Length > 0 ? trans : nextOrig;
    result.Tertiary = "";
    result.ShowTertiary = false;
   } else if (!showTranslation && showRomanization) {
    result.Primary = orig;
    result.Secondary = rom.Length > 0 ? rom : nextOrig;
    result.Tertiary = "";
    result.ShowTertiary = false;
   } else {
    if (rom.Length > 0 && trans.Length > 0) {
     result.Primary = orig;
     result.Secondary = rom;
     result.Tertiary = trans;
     result.ShowTertiary = true;
    } else if (trans.Length > 0) {
     result.Primary = orig;
     result.Secondary = trans;
     result.Tertiary = "";
     result.ShowTertiary = false;
    } else if (rom.Length > 0) {
     result.Primary = orig;
     result.Secondary = rom;
     result.Tertiary = "";
     result.ShowTertiary = false;
    } else {
     result.Primary = orig;
     result.Secondary = nextOrig;
     result.Tertiary = "";
     result.ShowTertiary = false;
    }
   }

   return result;
  }
 }
}
