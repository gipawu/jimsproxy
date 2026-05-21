using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ThreadingState = System.Threading.ThreadState;

namespace Framework.Logging;

public enum LogType
{
    Server,
    Network,
    Debug,
    Verbose,  // JimsProxy: per-packet tracing below Debug
    Error,
    Warn,
    Storage,
    SpanMiss,
    SpanStats
}

public enum LogNetDir // Network direction
{
    C2P, // C>P S
    P2S, // C P>S
    S2P, // C P<S
    P2C, // C<P S
}

public static class Log
{
    static Dictionary<LogType, (ConsoleColor Color, string Type)> LogToColorType = new()
    {
        { LogType.Debug,     (ConsoleColor.DarkBlue,  " Debug   ") },
        { LogType.Verbose,   (ConsoleColor.DarkGray,  " Verbose ") },
        { LogType.Server,    (ConsoleColor.Blue,      " Server  ") },
        { LogType.Network,   (ConsoleColor.Green,     " Network ") },
        { LogType.Error,     (ConsoleColor.Red,       " Error   ") },
        { LogType.Warn,      (ConsoleColor.Yellow,    " Warning ") },
        { LogType.Storage,   (ConsoleColor.Cyan,      " Storage ") },
        { LogType.SpanMiss,  (ConsoleColor.Magenta,   " SpanMiss") },
        { LogType.SpanStats, (ConsoleColor.DarkGreen, "SpanStats") },
    };

    static BlockingCollection<(LogType Type, string Message)> logQueue = new();
    static readonly Lock _debugOutputLock = new();
    private static Thread? _logOutputThread = null;
    public static bool IsLogging => _logOutputThread != null && !logQueue.IsCompleted;

    public static bool DebugLogEnabled { get; set; }
    public static bool VerboseLogEnabled { get; set; }
    public static bool SpanStatsEnabled { get; set; }

    // ── JimsProxy: structured JSONL logging ────────────────────────────
    //
    // Writes one JSON object per line to Logs/jimsproxy-YYYYMMDD-HHMMSS.jsonl
    // next to the running exe (cwd). Completely independent of the
    // colored console output above — both can be active simultaneously.

    public static bool StructuredLogEnabled { get; set; } = true;

    private static StreamWriter? _jsonlWriter;
    private static readonly object _jsonlLock = new();
    private static string? _jsonlPath;
    // JimsProxy: structured-log writes run on a dedicated background thread so a
    // slow disk flush can never block a packet-handling thread (per-event
    // synchronous flushing here was the cause of in-game rubberbanding under
    // load). Event() only serializes + enqueues; the writer thread owns all
    // file I/O. Bounded so a wedged disk drops lines instead of growing memory
    // unbounded — and TryAdd means the hot path never blocks even when full.
    private static readonly BlockingCollection<string> _jsonlQueue = new(boundedCapacity: 1 << 18);
    private static Thread? _jsonlWriterThread;
    private static long _jsonlDroppedCount;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        // JimsProxy: explicit DefaultJsonTypeInfoResolver re-enables reflection-based
        // serialization for trimmed/PublishSingleFile builds. Without this,
        // JsonSerializer.Serialize throws InvalidOperationException
        // "Reflection-based serialization has been disabled for this application"
        // when called with anonymous types. We use anonymous types extensively in
        // Log.Event payloads, so source-gen is not practical here. The runtime cost
        // is identical to the default reflection path on net8+.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Absolute path to the JSONL file for this session, or null if structured logging is disabled.
    /// </summary>
    public static string? StructuredLogPath => _jsonlPath;

    private static void EnsureJsonlOpen()
    {
        if (_jsonlWriter != null)
            return;

        try
        {
            var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(logsDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _jsonlPath = Path.Combine(logsDir, $"jimsproxy-{stamp}.jsonl");
            _jsonlWriter = new StreamWriter(new FileStream(_jsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                // AutoFlush off: the background writer thread (JsonlWriterLoop) flushes
                // explicitly. Per-event flushing was synchronous disk I/O on the
                // packet-handling thread — the cause of in-game rubberbanding under load.
                AutoFlush = false
            };
        }
        catch (Exception ex)
        {
            // If we can't open the log file, disable structured logging for the session
            // and print to stderr so the launcher can still see it via console
            StructuredLogEnabled = false;
            Console.Error.WriteLine($"[JimsProxy] Could not open structured log file: {ex.Message}");
        }
    }

    /// <summary>
    /// Eagerly open the JSONL file so Log.StructuredLogPath is valid before
    /// callers evaluate a payload that captures it (e.g. session.start).
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static void StartStructuredLog()
    {
        if (!StructuredLogEnabled || _jsonlWriterThread != null)
            return;
        lock (_jsonlLock)
        {
            if (_jsonlWriterThread != null)
                return;
            EnsureJsonlOpen();
            if (_jsonlWriter == null)
                return; // open failed; EnsureJsonlOpen already disabled structured logging
            _jsonlWriterThread = new Thread(JsonlWriterLoop)
            {
                IsBackground = true,
                Name = "jsonl-writer",
            };
            _jsonlWriterThread.Start();
        }
    }

    /// <summary>
    /// Emit a structured diagnostic event. Safe to call from any thread.
    /// Writes one JSON line with timestamp_ms, eventType, and optional payload.
    /// </summary>
    /// <param name="eventType">Dotted-lowercase identifier, e.g. "session.start", "packet.untranslated"</param>
    /// <param name="payload">Anonymous object or dictionary serialized as the event body. Null for no payload.</param>
    public static void Event(string eventType, object? payload = null)
    {
        if (!StructuredLogEnabled)
            return;

        string line;
        try
        {
            var envelope = new
            {
                timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                eventType,
                payload
            };
            line = JsonSerializer.Serialize(envelope, _jsonOpts);
        }
        catch (Exception ex)
        {
            // Serialization failure: emit a minimal error event so we don't lose visibility
            line = JsonSerializer.Serialize(new
            {
                timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                eventType = "log.serialize_error",
                payload = new { original_event = eventType, error = ex.Message }
            });
        }

        // Hand off to the background writer thread; never touch the disk here.
        // TryAdd is non-blocking — if the queue is full (disk wedged) drop the
        // line rather than stall a packet-handling thread. Once logging has been
        // shut down (CompleteAdding) TryAdd throws InvalidOperationException;
        // swallow it so a late Event() during teardown never propagates out.
        try
        {
            if (!_jsonlQueue.TryAdd(line))
                Interlocked.Increment(ref _jsonlDroppedCount);
        }
        catch (InvalidOperationException)
        {
            // structured logging already closed — drop silently
        }
    }

    /// <summary>
    /// Background pump: drains the structured-log queue and writes to disk.
    /// Flushes when the queue momentarily empties (so data lands promptly) and
    /// at least every 1000 lines (so a sustained burst can't defer a flush
    /// indefinitely). All structured-log file I/O happens here, off the
    /// packet-handling threads.
    /// </summary>
    private static void JsonlWriterLoop()
    {
        var sinceFlush = Stopwatch.StartNew();
        bool writeFailed = false;
        foreach (var line in _jsonlQueue.GetConsumingEnumerable())
        {
            if (_jsonlWriter == null || writeFailed)
                continue;
            try
            {
                _jsonlWriter.WriteLine(line);
                // Flush when the queue momentarily drains, or at least every
                // 200ms during a sustained burst — bounds how much is lost if
                // the process is hard-killed before the shutdown hooks run.
                if (_jsonlQueue.Count == 0 || sinceFlush.ElapsedMilliseconds >= 200)
                {
                    _jsonlWriter.Flush();
                    sinceFlush.Restart();
                }
            }
            catch (Exception ex)
            {
                // Stop writing after the first failure — a wedged/full disk fails
                // every subsequent line, and we don't want one Console.Error line
                // per queued event. Keep draining the queue so producers' TryAdd
                // never blocks; the lines are simply discarded.
                writeFailed = true;
                StructuredLogEnabled = false;
                Console.Error.WriteLine($"[JimsProxy] Structured log write failed, disabling: {ex.Message}");
            }
        }
        if (!writeFailed)
        {
            try { _jsonlWriter?.Flush(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Flush and close the structured log file. Called on shutdown.
    /// </summary>
    public static void FlushAndCloseStructuredLog()
    {
        // Stop accepting new events and let the writer thread drain what's queued.
        try { _jsonlQueue.CompleteAdding(); } catch { /* already completed */ }
        bool drained = _jsonlWriterThread == null || _jsonlWriterThread.Join(5000);

        lock (_jsonlLock)
        {
            // Only dispose once the writer thread has actually finished. If a
            // wedged disk kept it past the Join timeout, disposing here would
            // fault it mid-write; leave the handle for the OS to reclaim on exit.
            if (_jsonlWriter != null && drained)
            {
                long dropped = Interlocked.Read(ref _jsonlDroppedCount);
                if (dropped > 0)
                {
                    try
                    {
                        _jsonlWriter.WriteLine(JsonSerializer.Serialize(new
                        {
                            timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            eventType = "log.dropped",
                            payload = new { dropped_lines = dropped },
                        }, _jsonOpts));
                    }
                    catch { /* ignore */ }
                }
                try { _jsonlWriter.Flush(); _jsonlWriter.Dispose(); } catch { /* ignore */ }
                _jsonlWriter = null;
            }
        }
    }

    // ── Upstream logging API (unchanged behavior) ──────────────────────

    /// <summary>
    /// Start the logging Thread and take logs out of the <see cref="BlockingCollection{T}"/>
    /// </summary>
    public static void Start()
    {
        if (_logOutputThread == null)
        {
            _logOutputThread = new Thread(() =>
            {
                foreach (var msg in logQueue.GetConsumingEnumerable())
                {
                    PrintInternalDirectly(msg.Type, msg.Message);
                }
            });

            _logOutputThread.IsBackground = true;
            _logOutputThread.Start();
        }
    }

    private static void PrintInternalDirectly(LogType type, string text)
    {
        if (type == LogType.Debug && !DebugLogEnabled)
            return;
        if (type == LogType.Verbose && !VerboseLogEnabled)
            return;
        if (type == LogType.SpanStats && !SpanStatsEnabled)
            return;
#if DEBUG
        Console.Write($"{DateTime.Now:HH:mm:ss.ff} | "); // This function is directly called in DEBUG, so our timesstamps can also be a more precise
#else
        Console.Write($"{DateTime.Now:HH:mm:ss} | ");
#endif
        Console.ForegroundColor = LogToColorType[type].Color;
        Console.Write($"{LogToColorType[type].Type}");
        Console.ResetColor();

        Console.WriteLine($"| {text}");
    }

    public static void Print(LogType type, object text, [CallerMemberName] string method = "", [CallerFilePath] string path = "")
    {
        string formattedText = $"{FormatCaller(method, path)} | {text}";
#if DEBUG
        // Fastpath when using breakpoints we want to see the log results immediately
        if (Debugger.IsAttached)
        {
            lock (_debugOutputLock)
            {
                PrintInternalDirectly(type, formattedText);
            }
            return;
        }
#endif
        logQueue.Add((type, formattedText));
    }

    public static void PrintNet(LogType type, LogNetDir netDirection, object text, [CallerMemberName] string method = "", [CallerFilePath] string path = "")
    {
        string directionText = netDirection switch
        {
            LogNetDir.C2P => "C>P S",
            LogNetDir.P2S => "C P>S",
            LogNetDir.S2P => "C P<S",
            LogNetDir.P2C => "C<P S",
            _ => "?   ?",
        };
        Print(type, $"{directionText} | {text}", method, path);
    }

    public static void outException(Exception err, [CallerMemberName] string method = "", [CallerFilePath] string path = "")
    {
        Print(LogType.Error, err.ToString(), method, path);
    }

    private static string FormatCaller(string method, string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.PadRight(15, ' ');
    }
}
