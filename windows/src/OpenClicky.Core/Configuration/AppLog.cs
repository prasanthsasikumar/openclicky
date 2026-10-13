// Ports AppLog.swift: a plain-text log at %LOCALAPPDATA%\OpenClicky\Logs\app.log (the Mac's
// ~/Library/Logs/OpenClicky/app.log). The app has no console, so when the buddy points at the
// wrong thing, this is where the `point_at` line can be read afterwards. Truncated at launch past
// 2 MB, one timestamped line per call, safe from any thread; each line also goes to Debug output.

using System.Diagnostics;
using System.Text;

namespace OpenClicky.Core.Configuration;

public static class AppLog
{
    /// <summary>Truncated at launch (first write) when it has grown past this.</summary>
    public const long MaxBytesBeforeTruncation = 2_000_000;

    private static readonly object WriteLock = new();
    private static string? fileOverride;
    private static StreamWriter? writer;
    private static bool openAttempted;

    /// <summary>
    /// Where the log goes. A test run writes its own log in the temp folder, not the installed app's.
    /// </summary>
    public static string FilePath
    {
        get
        {
            lock (WriteLock)
            {
                return fileOverride ?? DefaultFilePath();
            }
        }
    }

    private static string DefaultFilePath()
    {
        if (IsTestRun()) return Path.Combine(Path.GetTempPath(), "openclicky-tests.log");
        return OpenClickyPaths.LogFile;
    }

    private static bool IsTestRun() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        {
            var name = assembly.GetName().Name ?? "";
            return name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>Sends the log to another file (tests); the next line opens it.</summary>
    public static void UseFile(string? path)
    {
        lock (WriteLock)
        {
            writer?.Dispose();
            writer = null;
            openAttempted = false;
            fileOverride = path;
        }
    }

    /// <summary>Appends one timestamped line ("yyyy-MM-dd HH:mm:ss.fff line").</summary>
    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
        Debug.WriteLine(stamped);
        lock (WriteLock)
        {
            var openWriter = OpenWriterIfNeeded();
            if (openWriter is null) return;
            try
            {
                // Other processes append too (a second launch, the Kokoro and Parakeet helpers): write at
                // the end as it is now, not where this process last left off, or their lines get overwritten.
                openWriter.BaseStream.Seek(0, SeekOrigin.End);
                openWriter.Write(stamped);
                openWriter.Write('\n');
                openWriter.Flush();
            }
            catch (IOException)
            {
                // A log line is never worth failing the caller over.
            }
        }
    }

    /// <summary>The Swift name, so ported code can keep `AppLog.append(...)` as `AppLog.Append(...)`.</summary>
    public static void Append(string line) => Write(line);

    private static StreamWriter? OpenWriterIfNeeded()
    {
        if (writer is not null || openAttempted) return writer;
        openAttempted = true;
        try
        {
            var path = fileOverride ?? DefaultFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var truncate = File.Exists(path) && new FileInfo(path).Length > MaxBytesBeforeTruncation;
            var stream = new FileStream(path, truncate ? FileMode.Create : FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writer = null;
        }
        return writer;
    }
}
