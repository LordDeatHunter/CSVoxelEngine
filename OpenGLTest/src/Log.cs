using System;
using System.Collections.Concurrent;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Async console sink: every diagnostic line is enqueued and written by one background thread
/// in order, so a slow terminal (conhost, IDE run window, full pipe) or a multi-kilobyte worker
/// exception dump never blocks the frame that logged it. Before this, a startup log burst could
/// stall whichever profiled span happened to call Console.WriteLine for 100 ms+.
/// Writers never block; use <see cref="Shutdown"/> on exit to drain.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> _queue = new();
    private static readonly Thread _writer = new(WriteLoop)
    {
        IsBackground = true,
        Name = "LogWriter"
    };

    static Log() => _writer.Start();

    /// <summary>Enqueues one line; never blocks. Falls back to a direct write after shutdown.</summary>
    public static void WriteLine(string line)
    {
        try
        {
            _queue.Add(line);
        }
        catch (InvalidOperationException)
        {
            // Shutdown already ran (background workers can outlive it): same as the old path.
            Console.WriteLine(line);
        }
    }

    /// <summary>Drains every queued line in order. Never hangs: a wedged sink is bypassed by
    /// the main thread after a grace period instead of blocking shutdown forever.</summary>
    public static void Shutdown()
    {
        _queue.CompleteAdding();
        if (_writer.Join(TimeSpan.FromSeconds(2)))
            return;
        while (_queue.TryTake(out var line))
            Console.WriteLine(line);
    }

    private static void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
                Console.WriteLine(line);
        }
        catch (ObjectDisposedException)
        {
            // Stdout went away (process teardown); Shutdown drains what is left.
        }
    }
}
