using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace VoxelEngine.Core;

/// <summary>
/// Async console sink: every diagnostic line is enqueued and written by one background thread
/// in order, so a slow terminal (conhost, IDE run window, full pipe) or a multi-kilobyte worker
/// exception dump never blocks the frame that logged it. Before this, a startup log burst could
/// stall whichever profiled span happened to call Console.WriteLine for 100 ms+.
/// Writers never block; use <see cref="Shutdown"/> on exit to drain.
/// </summary>
public static class Log
{
    // Optional color for a line; null keeps the terminal's default. The color is applied by
    // the writer thread, so setting Console.ForegroundColor never races another writer.
    private readonly record struct Entry(string Line, ConsoleColor? Color);

    private static readonly BlockingCollection<Entry> _queue = new();
    private static readonly Thread _writer = new(WriteLoop)
    {
        IsBackground = true,
        Name = "LogWriter"
    };

    static Log() => _writer.Start();

    /// <summary>Enqueues one line; never blocks. Falls back to a direct write after shutdown.</summary>
    public static void WriteLine(string line) => Enqueue(new Entry(line, null));

    /// <summary>Enqueues one line drawn in <paramref name="color"/> where the terminal supports it.</summary>
    public static void WriteLine(string line, ConsoleColor color) => Enqueue(new Entry(line, color));

    /// <summary>Enqueues a warning line, drawn yellow. Non-fatal content problems use this so a
    /// bad field degrades to a default instead of aborting startup.</summary>
    public static void Warn(string line) => Enqueue(new Entry(line, ConsoleColor.Yellow));

    private static void Enqueue(Entry entry)
    {
        try
        {
            _queue.Add(entry);
        }
        catch (InvalidOperationException)
        {
            // Shutdown already ran (background workers can outlive it): same as the old path.
            WriteDirect(entry);
        }
    }

    /// <summary>Drains every queued line in order. Never hangs: a wedged sink is bypassed by
    /// the main thread after a grace period instead of blocking shutdown forever.</summary>
    public static void Shutdown()
    {
        _queue.CompleteAdding();
        if (_writer.Join(TimeSpan.FromSeconds(2)))
            return;
        while (_queue.TryTake(out var entry))
            WriteDirect(entry);
    }

    private static void WriteLoop()
    {
        try
        {
            foreach (var entry in _queue.GetConsumingEnumerable())
                WriteDirect(entry);
        }
        catch (ObjectDisposedException)
        {
            // Stdout went away (process teardown); Shutdown drains what is left.
        }
    }

    // A redirected or detached console can refuse color changes; in that case the line is
    // still written, just without the tint. The write itself is never retried, so a broken
    // pipe cannot double up a line.
    private static void WriteDirect(Entry entry)
    {
        if (entry.Color is not { } color)
        {
            Console.WriteLine(entry.Line);
            return;
        }

        ConsoleColor previous;
        try
        {
            previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            Console.WriteLine(entry.Line);
            return;
        }

        try
        {
            Console.WriteLine(entry.Line);
        }
        finally
        {
            try
            {
                Console.ForegroundColor = previous;
            }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException)
            {
                // The console went away mid-line; nothing left to restore.
            }
        }
    }
}
