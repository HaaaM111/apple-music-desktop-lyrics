using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AMLyrics {
 public static class StandbyLauncher {
  public static int Main(string[] args) {
   string projectRoot = FindProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
   if (projectRoot == null) return 1;

   string outputsDir = Path.Combine(projectRoot, "outputs");
   if (!Directory.Exists(outputsDir)) return 1;

   string buildDir = Directory.GetDirectories(outputsDir, "build-*")
    .Where(dir =>
     File.Exists(Path.Combine(dir, "AppleMusicLyricsWatch.exe")) &&
     File.Exists(Path.Combine(dir, "AppleMusicLyrics.exe")) &&
     File.Exists(Path.Combine(dir, "MediaSession.ps1")))
    .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "AppleMusicLyricsWatch.exe")))
    .FirstOrDefault();

   if (string.IsNullOrEmpty(buildDir)) return 1;

   string watchExe = Path.Combine(buildDir, "AppleMusicLyricsWatch.exe");
   var startInfo = new ProcessStartInfo {
    FileName = watchExe,
    Arguments = JoinArguments(args),
    WorkingDirectory = buildDir,
    UseShellExecute = false,
    CreateNoWindow = true
   };

   try {
    using (var proc = Process.Start(startInfo)) { }
    return 0;
   } catch {
    return 1;
   }
  }

  static string FindProjectRoot(string baseDirectory) {
   for (var directory = new DirectoryInfo(baseDirectory); directory != null; directory = directory.Parent) {
    string candidate = directory.FullName;
    if (Directory.Exists(Path.Combine(candidate, "outputs")) &&
        Directory.Exists(Path.Combine(candidate, "scripts"))) {
     return candidate;
    }
   }
   return null;
  }

  static string JoinArguments(string[] args) {
   if (args == null || args.Length == 0) return string.Empty;
   var sb = new StringBuilder();
   for (int i = 0; i < args.Length; i++) {
    if (i > 0) sb.Append(' ');
    sb.Append(QuoteArgument(args[i]));
   }
   return sb.ToString();
  }

  static string QuoteArgument(string argument) {
   if (string.IsNullOrEmpty(argument)) return "\"\"";
   if (argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;

   var sb = new StringBuilder();
   sb.Append('"');
   for (int i = 0; i < argument.Length; i++) {
    int backslashes = 0;
    while (i < argument.Length && argument[i] == '\\') {
     backslashes++;
     i++;
    }
    if (i == argument.Length) {
     sb.Append('\\', backslashes * 2);
     break;
    }
    if (argument[i] == '"') {
     sb.Append('\\', backslashes * 2 + 1);
     sb.Append('"');
    } else {
     sb.Append('\\', backslashes);
     sb.Append(argument[i]);
    }
   }
   sb.Append('"');
   return sb.ToString();
  }
 }
}
