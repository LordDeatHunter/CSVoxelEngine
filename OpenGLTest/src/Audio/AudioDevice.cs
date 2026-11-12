using System;
using Silk.NET.OpenAL;

namespace VoxelEngine.Audio;

/// <summary>
/// The process's one OpenAL Soft device and context, shared by every <see cref="AudioOutput"/>:
/// opened by the first <see cref="Acquire"/> and closed by the last <see cref="Release"/>. The
/// natives come from the Silk.NET.OpenAL.Soft.Native package (Windows, Linux and macOS), so the
/// game plays on every OS it runs on; a machine with no device makes <see cref="Acquire"/> throw. The context asks
/// OpenAL Soft for HRTF or forbids it (<see cref="SetHrtf"/>): binaural rendering that places a positional sound
/// (<see cref="SpatialVoices"/>) in front, behind, above or below over headphones, where plain panning only says
/// left or right.
/// </summary>
internal static unsafe class AudioDevice
{
    // ALC_SOFT_HRTF: the context attribute (ALC_TRUE asks for HRTF, ALC_FALSE forbids it) and the status query
    private const int AlcHrtfSoft = 0x1992;
    private const int AlcHrtfStatusSoft = 0x1993;
    private const string HrtfExtension = "ALC_SOFT_HRTF";

    private static readonly object Gate = new();
    private static bool _hrtf = true;
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

        var attributes = stackalloc int[] { AlcHrtfSoft, _hrtf ? 1 : 0, 0 };
        _context = _alc.CreateContext(_device, attributes);
        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            Close();
            throw new InvalidOperationException("the audio device refused a context");
        }
        LogHrtf();
    }

    /// <summary>
    /// Asks for HRTF (3D audio for headphones) or forbids it. Before the device opens this is what it opens with; an
    /// open device is reset in place (<c>alcResetDeviceSOFT</c>), with every stream and voice playing on.
    /// </summary>
    public static void SetHrtf(bool enabled)
    {
        lock (Gate)
        {
            if (_hrtf == enabled) return;
            _hrtf = enabled;
            if (_device == null || _alc == null || !_alc.IsExtensionPresent(_device, HrtfExtension)) return;
            var reset = (delegate* unmanaged[Cdecl]<Device*, int*, byte>)_alc.GetProcAddress(_device, "alcResetDeviceSOFT");
            if (reset == null) return;
            var attributes = stackalloc int[] { AlcHrtfSoft, enabled ? 1 : 0, 0 };
            if (reset(_device, attributes) == 0)
                Log.Warn("Audio: the device refused to switch 3D audio (HRTF)");
            LogHrtf();
        }
    }

    // What the device made of the request: OpenAL Soft may deny HRTF (an output format it cannot render on)
    private static void LogHrtf()
    {
        if (!_alc!.IsExtensionPresent(_device, HrtfExtension))
        {
            Log.WriteLine("Audio: 3D audio (HRTF) is not supported by this OpenAL; sounds are panned only");
            return;
        }
        int status;
        _alc.GetContextProperty(_device, (GetContextInteger)AlcHrtfStatusSoft, 1, &status);
        var text = status switch
        {
            0 => "off",
            1 or 3 => "on",
            2 => "denied by the OpenAL configuration",
            4 => "on (headphones detected)",
            5 => "unavailable for this output format",
            _ => $"in an unknown state ({status})",
        };
        Log.WriteLine($"Audio: 3D audio (HRTF) {text}");
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
