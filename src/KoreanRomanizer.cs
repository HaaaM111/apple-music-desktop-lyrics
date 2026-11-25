using System;
using System.Globalization;
using System.Text;

namespace AMLyrics {
 public static class KoreanRomanizer {
  static readonly string[] Initials = {
   "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"
  };

  static readonly string[] Medials = {
   "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i"
  };

  static readonly string[] Finals = {
   "", "k", "kk", "ks", "n", "nj", "nh", "t", "l", "lk", "lm", "lb", "ls", "lt", "lp", "lh", "m", "p", "ps", "t", "t", "ng", "t", "t", "k", "t", "p", "t"
  };

  public static bool ContainsHangul(string text) {
   if (string.IsNullOrEmpty(text)) return false;
   for (int i = 0; i < text.Length; i++) {
    char c = text[i];
    if (c >= '\uAC00' && c <= '\uD7A3') return true;
   }
   return false;
  }

  public static bool LooksLikeRomanizedLatin(string text) {
   if (string.IsNullOrEmpty(text)) return false;
   int asciiLetters = 0;
   for (int i = 0; i < text.Length; i++) {
    char c = text[i];
    if ((c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\u31F0' && c <= '\u31FF') ||
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\uAC00' && c <= '\uD7AF')) {
     return false;
    }
    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) {
     asciiLetters++;
    }
   }
   return asciiLetters >= 4;
  }

  public static string TryRomanizeIfKorean(string text) {
   if (!ContainsHangul(text)) return "";
   return Romanize(text);
  }

  public static string Romanize(string text) {
   if (string.IsNullOrEmpty(text)) return "";

   var sb = new StringBuilder(text.Length * 2);
   int i = 0;
   while (i < text.Length) {
    char c = text[i];
    if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) {
     i++;
     continue;
    }

    if (IsHangulSyllable(c)) {
     int runStart = i;
     while (i < text.Length) {
      char ch = text[i];
      if (IsHangulSyllable(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) {
       i++;
      } else {
       break;
      }
     }
     AppendHangulRun(sb, text, runStart, i);
    } else {
     if (c == ' ') {
      if (sb.Length > 0 && sb[sb.Length - 1] != ' ') {
       sb.Append(' ');
      }
     } else {
      sb.Append(c);
     }
     i++;
    }
   }

   return sb.ToString().Trim();
  }

  static bool IsHangulSyllable(char c) {
   return c >= '\uAC00' && c <= '\uD7A3';
  }

  static void AppendHangulRun(StringBuilder sb, string text, int start, int end) {
   int count = 0;
   for (int i = start; i < end; i++) {
    if (IsHangulSyllable(text[i])) count++;
   }
   if (count == 0) return;

   var initials = new int[count];
   var finals = new int[count];
   var onsets = new string[count];
   var medials = new string[count];
   var codas = new string[count];

   int idx = 0;
   for (int i = start; i < end; i++) {
    char c = text[i];
    if (!IsHangulSyllable(c)) continue;
    int s = c - 0xAC00;
    int l = s / 588;
    int v = (s % 588) / 28;
    int t = s % 28;
    initials[idx] = l;
    finals[idx] = t;
    onsets[idx] = Initials[l];
    medials[idx] = Medials[v];
    codas[idx] = Finals[t];
    idx++;
   }

   for (int i = 0; i < count - 1; i++) {
    int t = finals[i];
    int nextL = initials[i + 1];

    if (nextL == 11) {
     ApplyLiaison(t, codas, onsets, i);
    } else if ((t == 8 && nextL == 5) || (t == 4 && nextL == 5) || (t == 8 && nextL == 2)) {
     codas[i] = "l";
     onsets[i + 1] = "l";
    } else if (nextL == 2 || nextL == 6) {
     if (t == 1 || t == 2 || t == 24 || t == 3) {
      codas[i] = "ng";
     } else if (t == 7 || t == 19 || t == 20 || t == 22 || t == 23 || t == 25 || t == 27) {
      codas[i] = "n";
     } else if (t == 17 || t == 18 || t == 26) {
      codas[i] = "m";
     }
    } else if ((t == 27 || t == 6 || t == 15) && (nextL == 0 || nextL == 3 || nextL == 12)) {
     codas[i] = t == 6 ? "n" : (t == 15 ? "l" : "");
     onsets[i + 1] = nextL == 0 ? "k" : (nextL == 3 ? "t" : "ch");
    } else if (nextL == 18 && (t == 1 || t == 7 || t == 17 || t == 22)) {
     codas[i] = "";
     onsets[i + 1] = t == 1 ? "k" : (t == 7 ? "t" : (t == 17 ? "p" : "ch"));
    }
   }

   for (int i = 0; i < count; i++) {
    sb.Append(onsets[i]);
    sb.Append(medials[i]);
    sb.Append(codas[i]);
   }
  }

  static void ApplyLiaison(int t, string[] codas, string[] onsets, int i) {
   switch (t) {
    case 1:  codas[i] = "";  onsets[i + 1] = "g";  break;
    case 2:  codas[i] = "";  onsets[i + 1] = "kk"; break;
    case 3:  codas[i] = "k"; onsets[i + 1] = "s";  break;
    case 4:  codas[i] = "";  onsets[i + 1] = "n";  break;
    case 5:  codas[i] = "n"; onsets[i + 1] = "j";  break;
    case 6:  codas[i] = "n"; onsets[i + 1] = "";   break;
    case 7:  codas[i] = "";  onsets[i + 1] = "d";  break;
    case 8:  codas[i] = "";  onsets[i + 1] = "r";  break;
    case 9:  codas[i] = "l"; onsets[i + 1] = "g";  break;
    case 10: codas[i] = "l"; onsets[i + 1] = "m";  break;
    case 11: codas[i] = "l"; onsets[i + 1] = "b";  break;
    case 12: codas[i] = "l"; onsets[i + 1] = "s";  break;
    case 13: codas[i] = "l"; onsets[i + 1] = "t";  break;
    case 14: codas[i] = "l"; onsets[i + 1] = "p";  break;
    case 15: codas[i] = "l"; onsets[i + 1] = "";   break;
    case 16: codas[i] = "";  onsets[i + 1] = "m";  break;
    case 17: codas[i] = "";  onsets[i + 1] = "b";  break;
    case 18: codas[i] = "p"; onsets[i + 1] = "s";  break;
    case 19: codas[i] = "";  onsets[i + 1] = "s";  break;
    case 20: codas[i] = "";  onsets[i + 1] = "ss"; break;
    case 21: break;
    case 22: codas[i] = "";  onsets[i + 1] = "j";  break;
    case 23: codas[i] = "";  onsets[i + 1] = "ch"; break;
    case 24: codas[i] = "";  onsets[i + 1] = "k";  break;
    case 25: codas[i] = "";  onsets[i + 1] = "t";  break;
    case 26: codas[i] = "";  onsets[i + 1] = "p";  break;
    case 27: codas[i] = "";  onsets[i + 1] = "";   break;
   }
  }
 }
}
