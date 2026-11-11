using System;
using Silk.NET.OpenAL;

namespace VoxelEngine.Audio;

/// <summary>
/// The process's one OpenAL Soft device and context, shared by every <see cref="AudioOutput"/>:
/// opened by the first <see cref="Acquire"/> and closed by the last <see cref="Release"/>. The
/// natives come from the Silk.NET.OpenAL.Soft.Native package (Windows, Linux and macOS), so the
/// game plays on every OS it runs on; a machine with no device makes <see cref="Acquire"/> throw.
/// </summary>
internal static unsafe class AudioDevice
{
    private static readonly object Gate = new();
    private static ALContext? _alc;
    private static AL? _al;
    private static Device* _device;
    private static Context* _context;
    private static int _users;

    /// <summary>The AL API with the shared context current, opening the device on first use.</summary>
    public static AL Acquire()
    {
        lock (Gate)
        {
            if (_users == 0) Open();
            _users++;
            return _al!;
        }
    }

    /// <summary>Drops one <see cref="Acquire"/>; the last one closes the device.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_users == 0) return;
            if (--_users == 0) Close();
        }
    }

    private static void Open()
    {
        _alc = ALContext.GetApi(soft: true);
        _al = AL.GetApi(soft: true);
        // An empty name opens the system's default output.
        _device = _alc.OpenDevice("");
        if (_device == null)
        {
            Close();
            throw new InvalidOperationException("no audio output device could be opened");
        }

        _context = _alc.CreateContext(_device, null);
        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            Close();
            throw new InvalidOperationException("the audio device refused a context");
        }
    }

    private static void Close()
    {
        if (_alc != null)
        {
            if (_context != null)
            {
                _alc.MakeContextCurrent(null);
                _alc.DestroyContext(_context);
            }

            if (_device != null) _alc.CloseDevice(_device);
        }

        _context = null;
        _device = null;
        _al?.Dispose();
        _alc?.Dispose();
        _al = null;
        _alc = null;
    }
}
