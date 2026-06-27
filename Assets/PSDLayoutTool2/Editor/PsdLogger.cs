namespace PsdLayoutTool2
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using UnityEditor;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Writes PSDLayoutTool2 diagnostic logs to a project-local file.
    /// </summary>
    internal static class PsdLogger
    {
        private const int MaxLogFilesToKeep = 50;
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
        private static Stopwatch sessionStopwatch;
        private static string currentLogPath;
        private static bool hasError;
        private static bool writeFailureReported;

        /// <summary>
        /// Gets the directory that stores diagnostic logs.
        /// </summary>
        public static string LogDirectory
        {
            get
            {
                return Path.Combine(GetProjectRootPath(), "Library", "PSDLayoutTool2", "Logs");
            }
        }

        /// <summary>
        /// Starts a new import logging session.
        /// </summary>
        /// <param name="assetPath">PSD asset path.</param>
        /// <param name="mode">Import mode.</param>
        /// <param name="skipConflictPrompt">Whether the conflict prompt is skipped.</param>
        public static void BeginImportSession(string assetPath, string mode, bool skipConflictPrompt)
        {
            if (sessionStopwatch != null)
            {
                EndImportSession("Interrupted by a new session");
            }

            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();

            string safeAssetName = MakeSafeFileName(string.IsNullOrEmpty(assetPath) ? "UnknownPSD" : assetPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            currentLogPath = Path.Combine(LogDirectory, timestamp + "_" + safeAssetName + ".log");
            sessionStopwatch = Stopwatch.StartNew();
            hasError = false;
            writeFailureReported = false;

            WriteRawLine("============================================================");
            WriteRawLine("PSDLayoutTool2 Import Log");
            WriteRawLine("Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            WriteRawLine("Unity: " + Application.unityVersion);
            WriteRawLine("Project: " + GetProjectRootPath());
            WriteRawLine("Asset: " + assetPath);
            WriteRawLine("Mode: " + mode);
            WriteRawLine("Skip conflict prompt: " + skipConflictPrompt);
            WriteRawLine("============================================================");
        }

        /// <summary>
        /// Ends the active import logging session.
        /// </summary>
        /// <param name="result">Short result text.</param>
        public static void EndImportSession(string result)
        {
            if (sessionStopwatch == null)
            {
                return;
            }

            string status = hasError ? "FAILED" : "FINISHED";
            Info("Session " + status + ": " + result);
            WriteRawLine("Ended: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            WriteRawLine("Elapsed: " + sessionStopwatch.Elapsed);
            WriteRawLine("Log file: " + currentLogPath);
            WriteRawLine("============================================================");
            Debug.Log("[PSDLayoutTool2] Import log " + status + ": " + currentLogPath);

            sessionStopwatch = null;
            currentLogPath = null;
        }

        /// <summary>
        /// Writes an informational log entry.
        /// </summary>
        /// <param name="message">Message to write.</param>
        public static void Info(string message)
        {
            Write("INFO", message);
        }

        /// <summary>
        /// Writes a step log entry.
        /// </summary>
        /// <param name="message">Message to write.</param>
        public static void Step(string message)
        {
            Write("STEP", message);
        }

        /// <summary>
        /// Writes a warning log entry.
        /// </summary>
        /// <param name="message">Message to write.</param>
        public static void Warning(string message)
        {
            Write("WARN", message);
            Debug.LogWarning("[PSDLayoutTool2] " + message);
        }

        /// <summary>
        /// Writes an exception log entry.
        /// </summary>
        /// <param name="message">Context message.</param>
        /// <param name="exception">Exception to record.</param>
        public static void Exception(string message, Exception exception)
        {
            hasError = true;
            Write("ERROR", message);
            Write("ERROR", exception != null ? exception.ToString() : "Unknown exception");
            Debug.LogError("[PSDLayoutTool2] " + message + "\n" + exception);
        }

        /// <summary>
        /// Reveals the log directory in the OS file manager.
        /// </summary>
        public static void RevealLogFolder()
        {
            Directory.CreateDirectory(LogDirectory);
            EditorUtility.RevealInFinder(LogDirectory);
        }

        /// <summary>
        /// Reveals the latest log file in the OS file manager.
        /// </summary>
        public static void RevealLatestLog()
        {
            string latestLog = GetLatestLogPath();
            if (string.IsNullOrEmpty(latestLog))
            {
                EditorUtility.DisplayDialog(
                    "PSDLayoutTool2",
                    "No PSDLayoutTool2 log file has been created yet.",
                    "OK");
                return;
            }

            EditorUtility.RevealInFinder(latestLog);
        }

        /// <summary>
        /// Menu entry for revealing the log directory.
        /// </summary>
        [MenuItem("Tools/PSD Layout Tool 2/Open Log Folder")]
        private static void OpenLogFolder()
        {
            RevealLogFolder();
        }

        /// <summary>
        /// Menu entry for revealing the latest log file.
        /// </summary>
        [MenuItem("Tools/PSD Layout Tool 2/Open Latest Log")]
        private static void OpenLatestLog()
        {
            RevealLatestLog();
        }

        private static void Write(string level, string message)
        {
            string elapsed = sessionStopwatch != null ? sessionStopwatch.Elapsed.ToString(@"hh\:mm\:ss\.fff") : "--:--:--.---";
            string prefix = DateTime.Now.ToString("HH:mm:ss.fff") + " +" + elapsed + " [" + level + "] ";
            string[] lines = SplitLines(message);
            foreach (string line in lines)
            {
                WriteRawLine(prefix + line);
            }
        }

        private static void WriteRawLine(string line)
        {
            if (string.IsNullOrEmpty(currentLogPath))
            {
                return;
            }

            try
            {
                File.AppendAllText(currentLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                if (!writeFailureReported)
                {
                    writeFailureReported = true;
                    Debug.LogWarning("[PSDLayoutTool2] Failed to write diagnostic log: " + exception.Message);
                }
            }
        }

        private static string[] SplitLines(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return new[] { string.Empty };
            }

            return message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string GetProjectRootPath()
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (dataPath.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
            {
                return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
            }

            DirectoryInfo parent = Directory.GetParent(dataPath);
            return parent != null ? parent.FullName : dataPath;
        }

        private static string MakeSafeFileName(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                bool invalid = InvalidFileNameChars.Contains(character) ||
                    character == '/' ||
                    character == '\\' ||
                    character == ':' ||
                    character == '*';
                builder.Append(invalid ? '_' : character);
            }

            string safe = builder.ToString().Trim('_', '.', ' ');
            if (string.IsNullOrEmpty(safe))
            {
                safe = "UnknownPSD";
            }

            return safe.Length > 80 ? safe.Substring(safe.Length - 80) : safe;
        }

        private static string GetLatestLogPath()
        {
            if (!Directory.Exists(LogDirectory))
            {
                return string.Empty;
            }

            return Directory.GetFiles(LogDirectory, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static void CleanupOldLogs()
        {
            string[] logs = Directory.GetFiles(LogDirectory, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            for (int i = MaxLogFilesToKeep; i < logs.Length; i++)
            {
                try
                {
                    File.Delete(logs[i]);
                }
                catch
                {
                    // Logging cleanup should never block an import.
                }
            }
        }
    }
}
