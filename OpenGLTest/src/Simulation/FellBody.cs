using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Core;

namespace VoxelEngine.Simulation;

/// <summary>
/// GL-free rigid body for a felled tree: the tree's blocks are point masses (their centers) held in
/// one rigid shape that falls under gravity, tumbles, hits the terrain and settles. Optionally it
/// starts pinned to the stump by a ball joint at the cut (<c>hinged</c>), which is what makes a
/// standing tree tip over its base like a felled trunk; the pin lets go once the tree has leaned
/// past <see cref="SimulationConfig.TreeHingeReleaseRadians"/>, and from then on the base kicks free
/// like a real fall. Without the pin (a limb, a branch set) the body just drops and tumbles. The
/// terrain is a solid-cell query; a block collides through the lowest point of its cell, with
/// impulse contacts (restitution and friction) and a positional correction. Fixed substeps, main
/// thread, no GL.
/// </summary>
internal sealed class FellBody
{
    private const float Step = 1f / 120f;
    private const int MaxStepsPerUpdate = 12;
    private const int SolverIterations = 8;
    // The skin that keeps a resting body from flickering in and out of contact
    private const float ContactSkin = 0.02f;
    private const float PenetrationSlop = 0.005f;
    private const int MaxClimbCells = 6;
    private static readonly Vector3 Up = Vector3.UnitY;

    private readonly Func<int, int, int, bool> _isSolid;
    private readonly Vector3[] _offsets;
    private readonly Vector3[] _contactOffsets;
    private readonly int[] _contactOwner;
    private readonly float _invMass;
    private readonly Matrix4x4 _invInertiaBody;
    private readonly Vector3 _hingeLocal;
    private readonly Vector3 _hingeWorld;
    private readonly Vector3 _crownLocal;
    // Contact scratch, refilled every substep
    private readonly List<(Vector3 Lever, float Depth, int Index)> _contacts = [];
    private readonly List<int> _impacts = [];
    private float _accumulator;
    private float _restTimer;

    /// <summary>Where the body's center of mass was when it was built.</summary>
    public Vector3 StartCenter { get; }

    /// <summary>The center of mass now.</summary>
    public Vector3 Center { get; private set; }

    /// <summary>The body's rotation now (from its shape at build time).</summary>
    public Quaternion Rotation { get; private set; } = Quaternion.Identity;

    /// <summary>True while the pin to the stump still holds.</summary>
    public bool Hinged { get; private set; }

    /// <summary>True once the body has stopped moving (or its time ran out) and is frozen.</summary>
    public bool AtRest { get; private set; }

    /// <summary>Seconds simulated so far.</summary>
    public float Elapsed { get; private set; }

    /// <summary>The fastest downward speed a block hit the ground with during the last
    /// <see cref="Advance"/>; 0 if nothing hit.</summary>
    public float LastImpactSpeed { get; private set; }

    private Vector3 _velocity;
    private Vector3 _angular;

    /// <summary>The blocks (by index) that hit the ground faster than
    /// <see cref="SimulationConfig.TreeFellDetachSpeed"/> during the last <see cref="Advance"/>.</summary>
    public IReadOnlyList<int> HardImpacts => _impacts;

    /// <param name="centers">World-space block centers.</param>
    /// <param name="masses">One mass per center (leaves are light).</param>
    /// <param name="contactPoints">Per block, the world-space points that touch the ground (see <see cref="FellContacts"/>).</param>
    /// <param name="pivot">The cut: the world point the pin holds.</param>
    /// <param name="tipAxis">The axis the tree tips around when pinned, or tumbles around when free.</param>
    /// <param name="hinged">Whether the body starts pinned to the stump at <paramref name="pivot"/>.</param>
    /// <param name="isSolid">Whether a world cell stops the body.</param>
    /// <param name="inherit">A body this one is split from: it carries on from that body's pose and
    /// motion (and its pin) with only these blocks, so nothing jumps when a piece breaks off.</param>
    public FellBody(IReadOnlyList<Vector3> centers, IReadOnlyList<float> masses, IReadOnlyList<Vector3[]> contactPoints, Vector3 pivot, Vector3 tipAxis,
        bool hinged, Func<int, int, int, bool> isSolid, FellBody? inherit = null)
    {
        _isSolid = isSolid;
        var total = 0f;
        var sum = Vector3.Zero;
        for (var i = 0; i < centers.Count; i++)
        {
            total += masses[i];
            sum += centers[i] * masses[i];
        }
        StartCenter = Center = sum / Math.Max(total, 1e-4f);
        _invMass = 1f / Math.Max(total, 1e-4f);

        // Inertia of the point masses, plus the cube each block really is (m/6 per axis)
        _offsets = new Vector3[centers.Count];
        float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        for (var i = 0; i < centers.Count; i++)
        {
            var r = _offsets[i] = centers[i] - Center;
            var m = masses[i];
            xx += m * (r.Y * r.Y + r.Z * r.Z + 1f / 6f);
            yy += m * (r.X * r.X + r.Z * r.Z + 1f / 6f);
            zz += m * (r.X * r.X + r.Y * r.Y + 1f / 6f);
            xy -= m * r.X * r.Y;
            xz -= m * r.X * r.Z;
            yz -= m * r.Y * r.Z;
        }
        var reach = 0f;
        foreach (var offset in _offsets)
            reach = MathF.Max(reach, offset.Length());
        Radius = reach + 1f;
        var inertia = new Matrix4x4(xx, xy, xz, 0, xy, yy, yz, 0, xz, yz, zz, 0, 0, 0, 0, 1);
        _invInertiaBody = Matrix4x4.Invert(inertia, out var inverse) ? inverse : Matrix4x4.Identity;

        var contactCount = 0;
        foreach (var points in contactPoints) contactCount += points.Length;
        _contactOffsets = new Vector3[contactCount];
        _contactOwner = new int[contactCount];
        var at = 0;
        for (var i = 0; i < contactPoints.Count; i++)
        {
            foreach (var point in contactPoints[i])
            {
                _contactOffsets[at] = point - Center;
                _contactOwner[at++] = i;
            }
        }

        _hingeWorld = pivot;
        _hingeLocal = pivot - Center;
        _crownLocal = Center - pivot;
        Hinged = hinged;

        // A small shove toward the fall direction: a pinned tree would otherwise balance on its
        // base, and a loose branch tips a little as it drops
        if (inherit == null)
        {
            _angular = tipAxis * SimulationConfig.TreeFellKickRadiansPerSecond;
            return;
        }

        Rotation = inherit.Rotation;
        Center = inherit.Center + Vector3.Transform(StartCenter - inherit.StartCenter, inherit.Rotation);
        _angular = inherit._angular;
        _velocity = inherit._velocity + Vector3.Cross(inherit._angular, Center - inherit.Center);
        Hinged = inherit.Hinged;
        Elapsed = inherit.Elapsed;
    }

    /// <summary>The farthest any block center is from <see cref="Center"/>, plus a block: a sphere
    /// around the center that holds the whole body in any pose.</summary>
    public float Radius { get; }

    /// <summary>How fast the body's material moves at the world point <paramref name="point"/> now.</summary>
    public Vector3 VelocityAt(Vector3 point) => _velocity + Vector3.Cross(_angular, point - Center);

    /// <summary>World position of block <paramref name="index"/>'s center now.</summary>
    public Vector3 PointNow(int index) => Center + Vector3.Transform(_offsets[index], Rotation);

    /// <summary>Model matrix for a mesh whose vertices are world positions at build time minus
    /// <paramref name="pivot"/> (row-vector convention: rotate, then translate).</summary>
    public Matrix4x4 ModelFor(Vector3 pivot) =>
        Matrix4x4.CreateFromQuaternion(Rotation)
        * Matrix4x4.CreateTranslation(Center + Vector3.Transform(pivot - StartCenter, Rotation));

    /// <summary>Stops the body where it lies.</summary>
    public void Freeze()
    {
        AtRest = true;
        _velocity = Vector3.Zero;
        _angular = Vector3.Zero;
    }

    /// <summary>Advances the simulation by <paramref name="deltaTime"/> in fixed substeps.</summary>
    public void Advance(float deltaTime)
    {
        LastImpactSpeed = 0f;
        _impacts.Clear();
        if (AtRest) return;

        _accumulator += MathF.Min(deltaTime, Step * MaxStepsPerUpdate);
        while (_accumulator >= Step && !AtRest)
        {
            _accumulator -= Step;
            Substep(Step);
        }
    }

    private void Substep(float dt)
    {
        Elapsed += dt;
        _velocity.Y -= BodyMotion.Gravity * dt;

        FindContacts();
        for (var i = 0; i < SolverIterations; i++)
        {
            if (Hinged) SolveHinge();
            SolveContacts();
        }

        Center += _velocity * dt;
        Rotation = Quaternion.Normalize(Rotation + Quaternion.Multiply(new Quaternion(_angular, 0f) * Rotation, 0.5f * dt));
        _velocity *= 1f - SimulationConfig.TreeFellLinearDamping * dt;
        _angular *= 1f - SimulationConfig.TreeFellAngularDamping * dt;

        if (Hinged)
        {
            Center += _hingeWorld - (Center + Vector3.Transform(_hingeLocal, Rotation));
            ReleaseHingeWhenTipped();
        }
        Depenetrate();
        SettleWhenStill(dt);
    }

    // The pin holds until the tree is nearly flat, or it has been stopped short (a crown that
    // reached the ground first): then the stump lets go and the body settles
    private void ReleaseHingeWhenTipped()
    {
        if (_contacts.Count > 0 && IsStill()) Hinged = false;

        var crown = Vector3.Transform(_crownLocal, Rotation);
        var length = crown.Length();
        if (length < 1e-4f) return;
        if (MathF.Acos(Math.Clamp(crown.Y / length, -1f, 1f)) > SimulationConfig.TreeHingeReleaseRadians)
            Hinged = false;
    }

    private bool IsStill()
    {
        var limit = SimulationConfig.TreeFellRestSpeed * SimulationConfig.TreeFellRestSpeed;
        return _velocity.LengthSquared() < limit && _angular.LengthSquared() < limit;
    }

    private void SettleWhenStill(float dt)
    {
        var still = IsStill() && _contacts.Count > 0 && !Hinged;
        _restTimer = still ? _restTimer + dt : 0f;
        if (_restTimer < SimulationConfig.TreeFellRestHoldSeconds && Elapsed < SimulationConfig.TreeFellMaxSimSeconds)
            return;

        Freeze();
    }

    /// <summary>Every contact point that is in (or within the skin of) a solid cell.</summary>
    private void FindContacts()
    {
        _contacts.Clear();
        for (var i = 0; i < _contactOffsets.Length; i++)
        {
            var lever = Vector3.Transform(_contactOffsets[i], Rotation);
            var depth = DepthIntoGround(Center + lever);
            if (depth > -ContactSkin)
                _contacts.Add((lever, depth, _contactOwner[i]));
        }
    }

    /// <summary>How far above the surface of the solid column holding <paramref name="point"/> the
    /// point sits (negative: above, down to the skin; positive: sunk in); -1 when there is no ground.</summary>
    private float DepthIntoGround(Vector3 point)
    {
        var below = point - new Vector3(0f, ContactSkin, 0f);
        int x = (int)MathF.Floor(below.X), z = (int)MathF.Floor(below.Z);
        var y = (int)MathF.Floor(below.Y);
        if (!_isSolid(x, y, z)) return -1f;
        for (var climb = 0; climb < MaxClimbCells && _isSolid(x, y + 1, z); climb++)
            y++;
        return y + 1 - point.Y;
    }

    private void SolveHinge()
    {
        var r = Vector3.Transform(_hingeLocal, Rotation);
        var pointVelocity = _velocity + Vector3.Cross(_angular, r);
        // The 3x3 effective mass of the pin, built column by column from unit impulses
        var kx = Vector3.UnitX * _invMass + Vector3.Cross(ApplyInvInertia(Vector3.Cross(r, Vector3.UnitX)), r);
        var ky = Vector3.UnitY * _invMass + Vector3.Cross(ApplyInvInertia(Vector3.Cross(r, Vector3.UnitY)), r);
        var kz = Vector3.UnitZ * _invMass + Vector3.Cross(ApplyInvInertia(Vector3.Cross(r, Vector3.UnitZ)), r);
        var k = new Matrix4x4(kx.X, kx.Y, kx.Z, 0, ky.X, ky.Y, ky.Z, 0, kz.X, kz.Y, kz.Z, 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(k, out var inverse)) return;

        var impulse = Vector3.TransformNormal(-pointVelocity, inverse);
        ApplyImpulse(impulse, r);
    }

    private void SolveContacts()
    {
        foreach (var (lever, _, index) in _contacts)
        {
            var pointVelocity = _velocity + Vector3.Cross(_angular, lever);
            var approach = pointVelocity.Y;
            if (approach >= 0f) continue;

            LastImpactSpeed = MathF.Max(LastImpactSpeed, -approach);
            if (-approach >= SimulationConfig.TreeFellDetachSpeed && !_impacts.Contains(index))
                _impacts.Add(index);
            var bounce = -approach > SimulationConfig.TreeFellBounceSpeed ? SimulationConfig.TreeFellBounce : 0f;
            var normalMass = EffectiveMass(lever, Up);
            var normalImpulse = -(1f + bounce) * approach / normalMass;
            ApplyImpulse(Up * normalImpulse, lever);

            var slide = _velocity + Vector3.Cross(_angular, lever);
            slide.Y = 0f;
            var speed = slide.Length();
            if (speed < 1e-4f) continue;
            var along = slide / speed;
            var frictionImpulse = MathF.Min(speed / EffectiveMass(lever, along),
                SimulationConfig.TreeFellFriction * normalImpulse);
            ApplyImpulse(-along * frictionImpulse, lever);
        }
    }

    // Pushes the whole body out of the ground by its deepest sunk block
    private void Depenetrate()
    {
        FindContacts();
        var deepest = 0f;
        foreach (var (_, depth, _) in _contacts)
            deepest = MathF.Max(deepest, depth);
        if (deepest > PenetrationSlop)
            Center += new Vector3(0f, MathF.Min(deepest - PenetrationSlop, 0.5f) * 0.9f, 0f);
    }

    private float EffectiveMass(Vector3 lever, Vector3 direction) =>
        _invMass + Vector3.Dot(direction, Vector3.Cross(ApplyInvInertia(Vector3.Cross(lever, direction)), lever));

    private void ApplyImpulse(Vector3 impulse, Vector3 lever)
    {
        _velocity += impulse * _invMass;
        _angular += ApplyInvInertia(Vector3.Cross(lever, impulse));
    }

    // World-space inverse inertia applied to a vector: R * invI_body * R^T
    private Vector3 ApplyInvInertia(Vector3 v)
    {
        var body = Vector3.Transform(v, Quaternion.Conjugate(Rotation));
        return Vector3.Transform(Vector3.TransformNormal(body, _invInertiaBody), Rotation);
    }
}
