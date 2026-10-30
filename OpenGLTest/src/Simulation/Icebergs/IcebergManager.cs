using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Icebergs;

/// <summary>
/// Owns the live icebergs (<see cref="Iceberg"/>): forced and natural spawns, the drift and the
/// pushes (<see cref="IcebergMotion"/>), the collision of a moving berg with solid or unloaded
/// cells, other bergs and the bodies of the targets, the life cycle (despawn far from every target,
/// sink at the end of its life), and on a client the display-only mirrors of the server's bergs.
/// It is also the <see cref="IBodySolids"/> the player body collides with, and
/// <see cref="CarryAndPushOut"/> moves the local player with the berg it rides. Icebergs are not
/// mobs: they have their own caps (<see cref="IcebergConfig.NaturalCap"/>,
/// <see cref="IcebergConfig.MaxAlive"/>) and never count toward the mob caps. Main thread only.
/// </summary>
public sealed class IcebergManager : IBodySolids
{
    private readonly List<Iceberg> _bergs = [];
    private readonly IcebergShape[] _shapes;
    private readonly Random _random;
    private readonly HashSet<Iceberg> _previousMirrors = [];
    // The berg the rider carry moves the body with; it never blocks its own rider's carry.
    private Iceberg? _ignored;
    private float _spawnTimer;
    private int _spawnCursor;

    /// <summary>Builds the manager over the iceberg entity <paramref name="type"/>, validating its variants.</summary>
    public IcebergManager(EntityType type, int? randomSeed = null)
    {
        IcebergShape.ValidateContent(type);
        _shapes = IcebergShape.ForType(type);
        _random = randomSeed is { } seed ? new Random(seed) : new Random();
    }

    /// <summary>The live icebergs (on a client, the mirrors of the server's).</summary>
    public IReadOnlyList<Iceberg> Icebergs => _bergs;

    /// <summary>How many variants (size classes) the iceberg entity has.</summary>
    public int VariantCount => _shapes.Length;

    /// <summary>
    /// The climate the natural spawns read; null is worldgen's
    /// (<see cref="IcebergSpawner.WorldgenClimate"/>). A test seam.
    /// </summary>
    internal IcebergClimate? Climate { get; set; }

    /// <summary>The collision shape of <paramref name="variant"/>.</summary>
    public IcebergShape ShapeOf(int variant) => _shapes[variant];

    /// <summary>
    /// A forced spawn (a scenario, a save restore): refused (null) at
    /// <see cref="IcebergConfig.MaxAlive"/>, or where the placed berg would overlap another berg or
    /// a solid or unloaded cell.
    /// </summary>
    public Iceberg? Spawn(ChunkManager chunks, int variant, float scale, int turns, int seed, Vector3 center, float life)
    {
        if (_bergs.Count >= IcebergConfig.MaxAlive || variant < 0 || variant >= _shapes.Length)
            return null;
        var shape = _shapes[variant];
        var (min, max) = shape.PlacedBounds(turns, scale, center);
        if (OverlapsOtherBerg(null, min, max) || !CellsClear(chunks, min, max))
            return null;
        var berg = new Iceberg(shape, variant, scale, turns, seed, center, life);
        _bergs.Add(berg);
        return berg;
    }

    /// <summary>
    /// One frame of the authoritative simulation (singleplayer and the server, never a client):
    /// every berg near a target and over loaded chunks drifts, is pushed by the targets leaning on
    /// it and collides; then the life cycle runs. With no targets nothing moves, spawns or despawns.
    /// </summary>
    public void Update(float dt, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, double lightingTime)
    {
        if (dt <= 0f || targets.Count == 0)
            return;

        foreach (var berg in _bergs)
        {
            if (berg.IsSinking || !AnyTargetWithin(berg, targets, IcebergConfig.SimulateRadiusBlocks))
                continue;
            if (!ChunksLoaded(chunks, berg.Bounds.Min, berg.Bounds.Max))
                continue;

            var push = SumPushes(berg, targets);
            var velocity = IcebergMotion.Step(new Vector2(berg.Velocity.X, berg.Velocity.Z),
                IcebergMotion.Drift(berg.Seed, lightingTime), IcebergMotion.PushAcceleration(push, berg.Mass), dt);
            berg.Velocity = new Vector3(velocity.X, 0f, velocity.Y);
            MoveAxis(berg, chunks, targets, 0, berg.Velocity.X * dt);
            MoveAxis(berg, chunks, targets, 2, berg.Velocity.Z * dt);
            berg.AdvanceEmerge(dt);
        }

        UpdateLifeCycle(dt, chunks, targets);
    }

    // --- Collision ---

    /// <summary>
    /// Whether every cell under the box from <paramref name="min"/> to <paramref name="max"/> is
    /// loaded and not solid: where a berg may stand.
    /// </summary>
    public static bool CellsClear(ChunkManager chunks, Vector3 min, Vector3 max)
    {
        // A face lying exactly on a cell boundary does not reach into the next cell
        const float inset = 1e-4f;
        VoxelCoords.CellRange(min, max - new Vector3(inset), out var minX, out var minY, out var minZ,
            out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
            if (!CellClear(chunks, x, y, z))
                return false;
        return true;
    }

    private static bool CellClear(ChunkManager chunks, int x, int y, int z) =>
        chunks.TryGetCell(x, y, z, out var id, out _) && !BlockType.IsSolid(id);

    /// <summary>Whether every chunk the box from <paramref name="min"/> to <paramref name="max"/> reaches into is loaded.</summary>
    public static bool ChunksLoaded(ChunkManager chunks, Vector3 min, Vector3 max)
    {
        var lo = VoxelCoords.ChunkPosOf(min);
        var hi = VoxelCoords.ChunkPosOf(max);
        for (var cx = (int)lo.X; cx <= (int)hi.X; cx++)
        for (var cy = (int)lo.Y; cy <= (int)hi.Y; cy++)
        for (var cz = (int)lo.Z; cz <= (int)hi.Z; cz++)
            if (chunks.ChunkAt(cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size) == null)
                return false;
        return true;
    }

    // Moves the berg delta along axis (0 = X, 2 = Z), or stops that velocity component when the
    // leading face meets a solid or unloaded cell, another berg or a standing body.
    private void MoveAxis(Iceberg berg, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, int axis, float delta)
    {
        if (MathF.Abs(delta) < 1e-6f)
            return;
        delta = Math.Clamp(delta, -0.99f, 0.99f);
        var offset = axis == 0 ? new Vector3(delta, 0f, 0f) : new Vector3(0f, 0f, delta);
        var (min, max) = berg.Bounds;
        var newMin = min + offset;
        var newMax = max + offset;

        if (EntersBlockedLayer(chunks, axis, min, max, newMin, newMax)
            || OverlapsOtherBerg(berg, newMin, newMax)
            || BlocksABody(berg, berg.Center + offset, targets))
        {
            berg.Velocity = axis == 0 ? berg.Velocity with { X = 0f } : berg.Velocity with { Z = 0f };
            return;
        }

        berg.SetCenter(berg.Center + offset);
    }

    // Whether the cell layer the leading face moves into holds a solid or unloaded cell anywhere
    // across the berg's bounds.
    private static bool EntersBlockedLayer(ChunkManager chunks, int axis, Vector3 min, Vector3 max,
        Vector3 newMin, Vector3 newMax)
    {
        const float inset = 1e-4f;
        var forward = (axis == 0 ? newMax.X - max.X : newMax.Z - max.Z) > 0f;
        int oldLayer, newLayer;
        if (forward)
        {
            oldLayer = MathUtils.FloorToInt((axis == 0 ? max.X : max.Z) - inset);
            newLayer = MathUtils.FloorToInt((axis == 0 ? newMax.X : newMax.Z) - inset);
        }
        else
        {
            oldLayer = MathUtils.FloorToInt(axis == 0 ? min.X : min.Z);
            newLayer = MathUtils.FloorToInt(axis == 0 ? newMin.X : newMin.Z);
        }
        if (newLayer == oldLayer)
            return false;

        var minY = MathUtils.FloorToInt(newMin.Y);
        var maxY = MathUtils.FloorToInt(newMax.Y - inset);
        var minOther = MathUtils.FloorToInt(axis == 0 ? newMin.Z : newMin.X);
        var maxOther = MathUtils.FloorToInt((axis == 0 ? newMax.Z : newMax.X) - inset);
        for (var y = minY; y <= maxY; y++)
        for (var other = minOther; other <= maxOther; other++)
        {
            var clear = axis == 0
                ? CellClear(chunks, newLayer, y, other)
                : CellClear(chunks, other, y, newLayer);
            if (!clear)
                return true;
        }
        return false;
    }

    private bool OverlapsOtherBerg(Iceberg? self, Vector3 min, Vector3 max)
    {
        foreach (var other in _bergs)
        {
            if (other == self || !other.CollidesWithBodies)
                continue;
            if (IcebergContact.Overlaps(min, max, other.Bounds.Min, other.Bounds.Max))
                return true;
        }
        return false;
    }

    // Whether the berg moved to center would push into a target's body that is not riding it.
    private static bool BlocksABody(Iceberg berg, Vector3 center, IReadOnlyList<IMobTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.IsFlying || target.IsDead)
                continue;
            var bodyMin = IcebergContact.BodyMin(target.Eye);
            var bodyMax = IcebergContact.BodyMax(target.Eye);
            if (Rides(berg, bodyMin, bodyMax))
                continue;
            foreach (var box in berg.Shape.Boxes)
            {
                var (min, max) = IcebergShape.Place(box, berg.Turns, berg.Scale, center);
                if (IcebergContact.Overlaps(bodyMin, bodyMax, min, max))
                    return true;
            }
        }
        return false;
    }

    private static bool Rides(Iceberg berg, Vector3 bodyMin, Vector3 bodyMax)
    {
        foreach (var (min, max) in berg.Boxes)
            if (IcebergContact.IsRiding(bodyMin, bodyMax, min, max))
                return true;
        return false;
    }

    // The summed side-contact push directions of every target leaning on the berg: each target
    // that is not flying, dead or riding it pushes along the normal of the first box it touches.
    private static Vector2 SumPushes(Iceberg berg, IReadOnlyList<IMobTarget> targets)
    {
        var sum = Vector2.Zero;
        foreach (var target in targets)
        {
            if (target.IsFlying || target.IsDead)
                continue;
            var bodyMin = IcebergContact.BodyMin(target.Eye);
            var bodyMax = IcebergContact.BodyMax(target.Eye);
            if (Rides(berg, bodyMin, bodyMax))
                continue;
            foreach (var (min, max) in berg.Boxes)
            {
                if (!IcebergContact.TrySideContact(bodyMin, bodyMax, min, max, out var direction))
                    continue;
                sum += direction;
                break;
            }
        }
        return sum;
    }

    private static bool AnyTargetWithin(Iceberg berg, IReadOnlyList<IMobTarget> targets, float radius)
    {
        foreach (var target in targets)
            if (IcebergSpawner.HorizontalDistance(target.Eye, berg.Bounds.Min, berg.Bounds.Max) <= radius)
                return true;
        return false;
    }

    /// <inheritdoc/>
    public bool Overlaps(Vector3 min, Vector3 max)
    {
        foreach (var berg in _bergs)
        {
            if (berg == _ignored || !berg.CollidesWithBodies)
                continue;
            if (!IcebergContact.Overlaps(min, max, berg.Bounds.Min, berg.Bounds.Max))
                continue;
            foreach (var (boxMin, boxMax) in berg.Boxes)
                if (IcebergContact.Overlaps(min, max, boxMin, boxMax))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) overlaps a
    /// collidable berg's boxes, so a block may not be placed there. The air above a low ledge of a
    /// berg is free.
    /// </summary>
    public bool OverlapsCell(int x, int y, int z) =>
        Overlaps(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));

    // --- Rider carry (client side: singleplayer and a client) ---

    /// <summary>
    /// Moves the local player with the berg it rides: each berg's horizontal move since the last
    /// carry is applied to a body standing on its boxes where they were before that move, through
    /// the body collision (terrain and the other bergs still stop it, its own berg does not). Then
    /// a body left inside a berg box (a mirror snapping into it) is pushed out. Never while flying.
    /// </summary>
    public void CarryAndPushOut(Camera camera, ChunkManager chunks)
    {
        if (!camera.IsFlying)
        {
            Carry(camera, chunks);
            PushOut(camera, chunks);
        }

        foreach (var berg in _bergs)
            berg.LastCarriedCenter = berg.Center;
    }

    private void Carry(Camera camera, ChunkManager chunks)
    {
        foreach (var berg in _bergs)
        {
            if (!berg.CollidesWithBodies)
                continue;
            var displacement = (berg.Center - berg.LastCarriedCenter) with { Y = 0f };
            if (displacement == Vector3.Zero)
                continue;

            var bodyMin = IcebergContact.BodyMin(camera.Position);
            var bodyMax = IcebergContact.BodyMax(camera.Position);
            var before = berg.Center - displacement;
            var riding = false;
            foreach (var box in berg.Shape.Boxes)
            {
                var (min, max) = IcebergShape.Place(box, berg.Turns, berg.Scale, before);
                if (!IcebergContact.IsRiding(bodyMin, bodyMax, min, max))
                    continue;
                riding = true;
                break;
            }
            if (!riding)
                continue;

            _ignored = berg;
            var position = BodyCollision.MoveOnAxis(camera.Position, displacement.X, 0, chunks, out _, this);
            position = BodyCollision.MoveOnAxis(position, displacement.Z, 2, chunks, out _, this);
            camera.SetPosition(position);
            _ignored = null;
        }
    }

    private void PushOut(Camera camera, ChunkManager chunks)
    {
        foreach (var berg in _bergs)
        {
            if (!berg.CollidesWithBodies)
                continue;
            foreach (var (min, max) in berg.Boxes)
            {
                if (IcebergContact.Overlaps(IcebergContact.BodyMin(camera.Position),
                        IcebergContact.BodyMax(camera.Position), min, max))
                    BodyCollision.PushOutOfBox(camera, min, max, chunks, this);
            }
        }
    }

    // --- Client mirrors ---

    /// <summary>
    /// A client's whole berg list becomes exactly <paramref name="mirrors"/> (the server's
    /// snapshot). A mirror new to the list starts its carry from where it is.
    /// </summary>
    public void ReplaceMirrors(IEnumerable<Iceberg> mirrors)
    {
        _previousMirrors.Clear();
        foreach (var berg in _bergs)
            _previousMirrors.Add(berg);
        _bergs.Clear();
        foreach (var mirror in mirrors)
        {
            if (!_previousMirrors.Contains(mirror))
                mirror.LastCarriedCenter = mirror.Center;
            _bergs.Add(mirror);
        }
        _previousMirrors.Clear();
    }

    /// <summary>
    /// Advances a client's mirrors between snapshots: each dead-reckons along its velocity up to the
    /// lead cap, and a mirror that has sunk away is dropped.
    /// </summary>
    public void AnimateMirrors(float dt)
    {
        foreach (var berg in _bergs)
            berg.AdvanceMirror(dt);
        _bergs.RemoveAll(b => b.IsRemovable);
    }

    // --- Life cycle ---

    private void UpdateLifeCycle(float dt, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        for (var i = _bergs.Count - 1; i >= 0; i--)
        {
            var berg = _bergs[i];
            if (!AnyTargetWithin(berg, targets, IcebergConfig.DespawnRadiusBlocks))
            {
                _bergs.RemoveAt(i);
                continue;
            }

            if (!berg.IsSinking)
            {
                berg.LifeLeft -= dt;
                if (berg.LifeLeft <= 0f && !AnyTargetOnOrNear(berg, targets))
                    berg.BeginSinking();
            }

            berg.AdvanceSink(dt);
            if (berg.IsRemovable)
                _bergs.RemoveAt(i);
        }

        _spawnTimer += dt;
        if (_spawnTimer < IcebergConfig.SpawnIntervalSeconds)
            return;
        _spawnTimer = 0f;
        if (_bergs.Count >= IcebergConfig.NaturalCap)
            return;
        TrySpawnNear(chunks, targets[_spawnCursor++ % targets.Count].Eye, targets);
    }

    private static bool AnyTargetOnOrNear(Iceberg berg, IReadOnlyList<IMobTarget> targets)
    {
        foreach (var target in targets)
        {
            if (IcebergSpawner.HorizontalDistance(target.Eye, berg.Bounds.Min, berg.Bounds.Max)
                < IcebergConfig.SpawnClearRadiusBlocks)
                return true;
            if (Rides(berg, IcebergContact.BodyMin(target.Eye), IcebergContact.BodyMax(target.Eye)))
                return true;
        }
        return false;
    }

    private void TrySpawnNear(ChunkManager chunks, Vector3 around, IReadOnlyList<IMobTarget> targets)
    {
        var variant = _random.Next(_shapes.Length);
        var scale = IcebergConfig.ScaleMin + _random.NextSingle() * (IcebergConfig.ScaleMax - IcebergConfig.ScaleMin);
        var turns = _random.Next(4);
        var seed = _random.Next();
        var life = IcebergConfig.LifeMinSeconds
                   + _random.NextSingle() * (IcebergConfig.LifeMaxSeconds - IcebergConfig.LifeMinSeconds);
        if (IcebergSpawner.TryFindSite(chunks, around, _shapes[variant], turns, scale, targets, _random,
                out var center, Climate))
            Spawn(chunks, variant, scale, turns, seed, center, life);
    }
}
