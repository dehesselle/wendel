using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wendel;

/// <summary>
/// Appends timestamped entries to %LOCALAPPDATA%\wendel\wendel.log. Thread-safe; a
/// failure to write is ignored.
/// </summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Settable for tests.</summary>
    public static string FilePath { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wendel", "wendel.log");

    public static string OldFilePath => Path.ChangeExtension(FilePath, ".old.log");

    public static void Info(string message, string? details = null) => Write("INFO", message, details);

    public static void Error(string message, string? details = null) => Write("ERROR", message, details);

    /// <summary>Moves a log larger than 1 MB to wendel.old.log, replacing the previous one.</summary>
    public static void Rotate()
    {
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Delete(OldFilePath);
                    file.MoveTo(OldFilePath);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Keep appending to the large file.
            }
        }
    }

    /// <summary>
    /// Writes one entry: a line with time, level and message, then each line of
    /// <paramref name="details"/> indented by two spaces.
    /// </summary>
    internal static void Write(string level, string message, string? details)
    {
        var text = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.PadRight(5)).Append(' ').Append(message).Append("\r\n");
        if (!string.IsNullOrEmpty(details))
        {
            foreach (var line in details!.TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                text.Append("  ").Append(line).Append("\r\n");
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, text.ToString(), Utf8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Logging must never become an error of its own.
            }
        }
    }
}
