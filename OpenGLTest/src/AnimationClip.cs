using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One keyframe animation authored in Blockbench (a clip in the project's <c>animations</c>
/// array), already converted into engine conventions by <see cref="BbmodelLoader"/>: rotation
/// keys are engine Euler degrees, position keys are blocks in the parent bone's space, scale keys
/// are multipliers. GL-free and immutable after construction, so one clip is shared by every
/// entity that plays it.
/// </summary>
public sealed class AnimationClip
{
    /// <summary>What happens past the clip's end: wrap (<see cref="Loop"/>) or clamp.</summary>
    public enum LoopMode { Once, Loop, Hold }

    /// <summary>How a segment between two keyframes is evaluated (Blockbench's names).</summary>
    public enum Interpolation { Linear, CatmullRom, Step }

    /// <summary>One key on one channel: time in seconds, the value, and its interpolation.</summary>
    public readonly record struct Keyframe(float Time, Vector3 Value, Interpolation Mode);

    /// <summary>
    /// The keyed channels of one bone, each sorted by <see cref="Keyframe.Time"/>; an empty
    /// array means the channel is unkeyed and samples its identity value.
    /// </summary>
    public sealed class BoneTrack
    {
        public Keyframe[] Rotation = [], Position = [], Scale = [];
    }

    /// <summary>A named timeline marker (e.g. <c>release</c>, <c>impact</c>) at a time in seconds.</summary>
    public readonly record struct Event(float Time, string Name);

    /// <summary>A bone's local animated transform: translation (blocks), rotation and scale.</summary>
    public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
    {
        public static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    }

    private readonly Dictionary<string, BoneTrack> _tracks;
    private readonly Event[] _events;

    /// <summary>
    /// Builds a clip. <paramref name="events"/> are sorted by time here; a
    /// <paramref name="length"/> of zero or less is replaced by the last keyframe or event time
    /// (0 when there is none).
    /// </summary>
    public AnimationClip(string name, float length, LoopMode loop,
        IReadOnlyDictionary<string, BoneTrack> tracks, IReadOnlyList<Event> events)
    {
        Name = name;
        Loop = loop;
        _tracks = new Dictionary<string, BoneTrack>(StringComparer.Ordinal);
        foreach (var (bone, track) in tracks)
            _tracks[bone] = track;

        _events = new Event[events.Count];
        for (var i = 0; i < events.Count; i++)
            _events[i] = events[i];
        // Insertion sort is stable, so two events at the same time keep their authored order.
        for (var i = 1; i < _events.Length; i++)
        {
            var e = _events[i];
            var j = i - 1;
            while (j >= 0 && _events[j].Time > e.Time)
            {
                _events[j + 1] = _events[j];
                j--;
            }
            _events[j + 1] = e;
        }

        if (length <= 0f)
        {
            length = 0f;
            foreach (var track in _tracks.Values)
            {
                length = MathF.Max(length, LastTime(track.Rotation));
                length = MathF.Max(length, LastTime(track.Position));
                length = MathF.Max(length, LastTime(track.Scale));
            }
            foreach (var e in _events)
                length = MathF.Max(length, e.Time);
        }
        Length = length;
    }

    public string Name { get; }

    /// <summary>Clip length in seconds.</summary>
    public float Length { get; }

    public LoopMode Loop { get; }

    /// <summary>Per-bone tracks keyed by group name (ordinal).</summary>
    public IReadOnlyDictionary<string, BoneTrack> Tracks => _tracks;

    /// <summary>Timeline events sorted by time.</summary>
    public IReadOnlyList<Event> Events => _events;

    /// <summary>True when the clip keys any channel of <paramref name="bone"/>.</summary>
    public bool Keys(string bone) => _tracks.ContainsKey(bone);

    /// <summary>
    /// Maps a playback time into the clip: a looping clip wraps modulo its length (0 for a
    /// zero-length clip), a once or hold clip clamps to 0..Length.
    /// </summary>
    public float WrapTime(float time)
    {
        if (Loop == LoopMode.Loop)
        {
            if (Length <= 0f) return 0f;
            var t = time % Length;
            return t < 0f ? t + Length : t;
        }
        return Math.Clamp(time, 0f, MathF.Max(Length, 0f));
    }

    /// <summary>
    /// The local transform of <paramref name="bone"/> at <paramref name="time"/>: identity when
    /// the clip does not key it, otherwise each channel evaluated at the wrapped time (an unkeyed
    /// channel of a keyed bone keeps its identity value).
    /// </summary>
    public BoneTransform Sample(string bone, float time)
    {
        if (!_tracks.TryGetValue(bone, out var track))
            return BoneTransform.Identity;
        var t = WrapTime(time);
        var rotation = track.Rotation.Length == 0
            ? Quaternion.Identity
            : RotationFromDegrees(Evaluate(track.Rotation, t, Vector3.Zero));
        return new BoneTransform(
            Evaluate(track.Position, t, Vector3.Zero),
            rotation,
            Evaluate(track.Scale, t, Vector3.One));
    }

    /// <summary>
    /// Engine Euler degrees to a quaternion in Blockbench's bone Euler order 'ZYX': X is applied
    /// first, then Y, then Z (System.Numerics row vectors, so the leftmost factor runs first).
    /// </summary>
    public static Quaternion RotationFromDegrees(Vector3 degrees)
    {
        const float toRad = MathF.PI / 180f;
        return Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateRotationX(degrees.X * toRad)
            * Matrix4x4.CreateRotationY(degrees.Y * toRad)
            * Matrix4x4.CreateRotationZ(degrees.Z * toRad));
    }

    // Blockbench interpolates the raw Euler / vector components, so this does too; only the
    // animator's layer blends slerp.
    private static Vector3 Evaluate(Keyframe[] keys, float t, Vector3 fallback)
    {
        if (keys.Length == 0) return fallback;
        if (t <= keys[0].Time) return keys[0].Value;
        var last = keys.Length - 1;
        if (t >= keys[last].Time) return keys[last].Value;

        var i = 0;
        while (i < last - 1 && keys[i + 1].Time <= t)
            i++;
        var k1 = keys[i];
        var k2 = keys[i + 1];
        var span = k2.Time - k1.Time;
        var u = span > 0f ? (t - k1.Time) / span : 0f;

        // Blockbench's segment rule: catmull-rom if either end asks for it, step if the earlier
        // key does, linear otherwise.
        if (k1.Mode == Interpolation.CatmullRom || k2.Mode == Interpolation.CatmullRom)
        {
            var p0 = keys[i > 0 ? i - 1 : i].Value;
            var p3 = keys[i + 2 <= last ? i + 2 : i + 1].Value;
            return CatmullRom(p0, k1.Value, k2.Value, p3, u);
        }
        if (k1.Mode == Interpolation.Step)
            return k1.Value;
        return Vector3.Lerp(k1.Value, k2.Value, u);
    }

    // Uniform Catmull-Rom between p1 and p2; passes through both ends.
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        var u2 = u * u;
        var u3 = u2 * u;
        return 0.5f * (2f * p1
                       + (p2 - p0) * u
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
    }

    private static float LastTime(Keyframe[] keys) => keys.Length == 0 ? 0f : keys[^1].Time;
}
