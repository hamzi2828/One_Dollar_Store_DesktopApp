using System;
using System.IO;
using System.Text;

namespace DollarCoAttendance
{
    // Append-only log for what the counter cannot report itself: which call
    // failed and why, a punch the server refused, an unexpected error.
    //
    //   %LOCALAPPDATA%\DollarCoAttendance\attendance.log   (rotated at 2 MB -> .log.1)
    //
    // Never throws: it runs inside the punch path and inside the crash handlers.
    public static class Log
    {
        private const long RotateAtBytes = 2L * 1024 * 1024;

        private static readonly object gate = new object();
        private static string filePath;

        public static string FilePath
        {
            get
            {
                if (filePath == null)
                {
                    try
                    {
                        string dir = AppInfo.DataFolder;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        filePath = Path.Combine(dir, "attendance.log");
                    }
                    catch
                    {
                        filePath = string.Empty;
                    }
                }
                return filePath;
            }
        }

        public static void Info(string message) { Write("INFO ", message, null); }

        public static void Error(string message) { Write("ERROR", message, null); }

        public static void Error(string message, Exception ex) { Write("ERROR", message, ex); }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path)) return;

                var line = new StringBuilder();
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                line.Append(" [").Append(level).Append("] ");
                line.Append(message ?? string.Empty);
                if (ex != null) line.Append(Environment.NewLine).Append(ex);
                line.Append(Environment.NewLine);

                lock (gate)
                {
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // See the class comment.
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < RotateAtBytes) return;
                string previous = path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            catch
            {
                // The file is open elsewhere: it grows a little past the cap this time.
            }
        }
    }
}
