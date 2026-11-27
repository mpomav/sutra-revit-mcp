using System;
using System.IO;
using System.Text;
using Scio.Revit.Core;

namespace Scio.Cad.Core
{
    internal static class CadLog
    {
        private static readonly object Gate = new object();
        private static string _logPath;

        public static void Init(string cadYear)
        {
            try
            {
                string dir = ScioPaths.LogsDir();
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "cad-" + (cadYear ?? "unknown") + ".log");
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message) => Write("WARN", message, null);
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " [" + level + "] " + message;
                if (ex != null) line += " | " + ex.GetType().Name + ": " + ex.Message;
                lock (Gate)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
