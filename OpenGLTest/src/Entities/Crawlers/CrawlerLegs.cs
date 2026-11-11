using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// A spider's eight legs, walked procedurally: every foot stays planted on the world where it landed while the
/// body moves over it, and steps to a new hold once the body has carried its rest point too far away. A hold is
/// found by probing the blocks around the body (out from the hip for a wall, down for the ground, back under the
/// body for the side of a ledge), so the legs reach for whatever surface is there: floors, walls, ceilings and
/// the corners between them. The two alternating groups of four take turns, as a spider's do. Each frame the
/// planted feet are turned into bone rotations by <see cref="LegIk"/>. Cosmetic: nothing in the simulation reads
/// it, so a server never runs it. GL-free; main thread only.
/// </summary>
public sealed class CrawlerLegs
{
    // A foot farther than this many leg lengths from its rest point was left behind by a teleport: it is re-placed
    private const float SnapReaches = 2.5f;
    // A planted foot this share of the leg's length from its hip is about to overstretch and steps regardless
    private const float StretchShare = 0.96f;
    // Under this speed a step lands on the rest point, with no lead along the motion
    private const float LeadMinSpeed = 0.2f;
    // A foot within this share of the step distance of the hold it would step to stays where it is
    private const float HeldShare = 0.5f;
    private const int NotStepping = -1;

    private readonly CrawlerRig _rig;
    private readonly Vector3[] _foot = new Vector3[CrawlerRig.LegCount];
    private readonly Vector3[] _from = new Vector3[CrawlerRig.LegCount];
    private readonly Vector3[] _to = new Vector3[CrawlerRig.LegCount];
    private readonly float[] _step = new float[CrawlerRig.LegCount];
    private readonly Quaternion[] _upper = new Quaternion[CrawlerRig.LegCount];
    private readonly Quaternion[] _lower = new Quaternion[CrawlerRig.LegCount];

    public CrawlerLegs(CrawlerRig rig)
    {
        _rig = rig;
        Array.Fill(_step, NotStepping);
    }

    /// <summary>Whether the legs have been posed at least once (<see cref="Update"/>); until then the model's
    /// clips pose them.</summary>
    public bool IsPosed { get; private set; }

    /// <summary>Where the foot of leg <paramref name="leg"/> is in the world, planted or mid-step.</summary>
    public Vector3 Foot(int leg) => _foot[leg];

    /// <summary>Whether leg <paramref name="leg"/> is in the air between two holds.</summary>
    public bool IsStepping(int leg) => _step[leg] >= 0f;

    /// <summary>
    /// Advances the feet and solves the legs for a body whose model space maps to the world through
    /// <paramref name="root"/> (a rotation and a translation) and moves at <paramref name="velocity"/>. With no
    /// <paramref name="chunks"/> (a model viewer) the feet walk the model's own ground plane, which slides back
    /// under a body that stays where it is, so the gait shows on the spot.
    /// </summary>
    public void Update(float deltaTime, in Matrix4x4 root, Vector3 velocity, ChunkManager? chunks)
    {
        var up = Vector3.Normalize(new Vector3(root.M21, root.M22, root.M23));
        var center = root.Translation;
        var legs = _rig.Legs;
        if (chunks == null && IsPosed) Treadmill(-velocity * deltaTime);
        for (var i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            var hip = Vector3.Transform(leg.Hip, root);
            var rest = Vector3.Transform(leg.Rest, root);
            var hipHeight = leg.Hip.Y;
            var snap = SnapReaches * leg.Reach;
            if (!IsPosed || Vector3.DistanceSquared(_foot[i], rest) > snap * snap)
            {
                _foot[i] = FindHold(hip, rest, up, center, hipHeight, leg.Reach, chunks);
                _step[i] = NotStepping;
                continue;
            }

            if (_step[i] >= 0f)
            {
                AdvanceStep(i, leg.Reach, up, deltaTime);
                continue;
            }

            var off = Vector3.Distance(_foot[i], rest);
            var stretched = Vector3.Distance(_foot[i], hip) > leg.Reach * StretchShare;
            var urgent = off > leg.Reach * CrawlerConfig.LegUrgentShare;
            if (!urgent && !stretched && off <= leg.Reach * CrawlerConfig.LegStepShare) continue;
            if (!urgent && OtherGroupStepping(i)) continue;

            // The step lands ahead of the rest point along the motion, where the body will have carried it
            var target = rest + velocity * CrawlerConfig.LegStepSeconds;
            var speed = velocity.Length();
            if (speed > LeadMinSpeed) target += velocity / speed * (leg.Reach * CrawlerConfig.LegLeadShare);
            var hold = FindHold(hip + (target - rest), target, up, center + (target - rest), hipHeight, leg.Reach, chunks);
            // The rest point may lie inside a wall or past a ledge, where the hold it leads to is the one the
            // foot already has: then there is nowhere better to step
            var held = leg.Reach * CrawlerConfig.LegStepShare * HeldShare;
            if (Vector3.DistanceSquared(hold, _foot[i]) <= held * held) continue;
            _from[i] = _foot[i];
            _to[i] = hold;
            _step[i] = 0f;
        }

        IsPosed = true;
        Solve(root);
    }

    /// <summary>
    /// The normal of the ground the feet stand on: the plane through the eight footholds, pointing to the side
    /// <paramref name="up"/> is on; <paramref name="up"/> itself until the legs are posed or when the feet lie
    /// in a line.
    /// </summary>
    public Vector3 FeetNormal(Vector3 up)
    {
        if (!IsPosed) return up;
        // Newell's sum around the ring of feet: the right side front to back, then the left back to front
        var normal = Vector3.Zero;
        for (var k = 0; k < CrawlerRig.LegCount; k++)
        {
            var a = _foot[RingIndex(k)];
            var b = _foot[RingIndex((k + 1) % CrawlerRig.LegCount)];
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (normal.LengthSquared() < 1e-8f) return up;
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, up) < 0f ? -normal : normal;
    }

    /// <summary>The solved rotation of leg bone <paramref name="bone"/> (an upper segment's in model space, a
    /// lower one's relative to its upper); false for any other bone, or before the legs are posed.</summary>
    public bool TryGetRotation(string bone, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        if (!IsPosed || !_rig.TryGetLegBone(bone, out var leg, out var lower)) return false;
        rotation = lower ? _lower[leg] : _upper[leg];
        return true;
    }

    // Carries every hold along with ground that moves under the body
    private void Treadmill(Vector3 drift)
    {
        for (var i = 0; i < CrawlerRig.LegCount; i++)
        {
            _foot[i] += drift;
            _from[i] += drift;
            _to[i] += drift;
        }
    }

    private void AdvanceStep(int i, float reach, Vector3 up, float deltaTime)
    {
        _step[i] += deltaTime / CrawlerConfig.LegStepSeconds;
        if (_step[i] >= 1f)
        {
            _foot[i] = _to[i];
            _step[i] = NotStepping;
            return;
        }

        var t = MathUtils.SmoothStep(_step[i]);
        var lift = MathF.Sin(_step[i] * MathF.PI) * reach * CrawlerConfig.LegStepLiftShare;
        _foot[i] = Vector3.Lerp(_from[i], _to[i], t) + up * lift;
    }

    private void Solve(in Matrix4x4 root)
    {
        if (!Matrix4x4.Invert(root, out var toModel)) return;
        var legs = _rig.Legs;
        for (var i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            LegIk.Solve(leg.Hip, Vector3.Transform(_foot[i], toModel), leg.Side, leg.UpperLength, leg.LowerLength,
                Vector3.UnitY, out _upper[i], out _lower[i], out _);
        }
    }

    // The two alternating groups: front-right, second-left, third-right, rear-left against the other four
    private static int GroupOf(int leg) =>
        (leg % CrawlerRig.LegsPerSide + (leg < CrawlerRig.LegsPerSide ? 0 : 1)) % 2;

    private bool OtherGroupStepping(int leg)
    {
        var group = GroupOf(leg);
        for (var j = 0; j < CrawlerRig.LegCount; j++)
            if (_step[j] >= 0f && GroupOf(j) != group) return true;
        return false;
    }

    // The right legs front to back, then the left legs back to front: once around the body
    private static int RingIndex(int k) =>
        k < CrawlerRig.LegsPerSide ? k : CrawlerRig.LegCount - 1 - (k - CrawlerRig.LegsPerSide);

    // Where a foot resting at 'rest' takes hold: a wall between the hip and the rest point, else the ground under
    // the rest point, else the side of the ledge the body stands on, else nothing (the foot hangs a little low)
    private static Vector3 FindHold(Vector3 hip, Vector3 rest, Vector3 up, Vector3 center, float hipHeight,
        float reach, ChunkManager? chunks)
    {
        if (chunks == null) return rest;

        var above = rest + up * hipHeight;
        if (TryHit(hip, above, chunks, out var hold)) return hold;
        var below = rest - up * (reach * CrawlerConfig.LegProbeDepthShare);
        if (TryHit(above, below, chunks, out hold)) return hold;
        var under = center - up * (reach * CrawlerConfig.LegProbeDepthShare);
        if (TryHit(below, under, chunks, out hold)) return hold;
        return rest - up * (reach * CrawlerConfig.LegDangleShare);
    }

    // The first block surface on the segment, by the combat line of sight's walk (plants and fluids are stepped
    // through, shaped blocks are hit at their boxes)
    private static bool TryHit(Vector3 from, Vector3 to, ChunkManager chunks, out Vector3 point)
    {
        point = to;
        var delta = to - from;
        var distance = delta.Length();
        if (distance < 1e-4f) return false;
        var direction = delta / distance;
        var cells = Math.Max(1, (int)MathF.Ceiling(distance * 3f) + 2);
        if (!BlockRaycaster.Raycast(from, direction, distance, cells, stopAtRange: true, chunks, out _, out _, out _,
                out var hitT, includeFluids: false, sightOnly: true) || hitT > distance)
            return false;
        point = from + direction * hitT;
        return true;
    }
}
