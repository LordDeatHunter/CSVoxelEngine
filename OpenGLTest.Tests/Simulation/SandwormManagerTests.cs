using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// The sandworm manager over a flat desert: one worm at a time, hunting a target from under the sand and
/// biting it when it breaches, resting between bites, never hurting an immune target, still hunting a target
/// across the desert border, keeping the hunt going for a moment after the target is lost, giving up when the
/// head strays from the desert or nobody is near, rolling loot on a kill; the body's hit tests (exposed segments
/// only) and the head-versus-body damage split.
/// </summary>
public sealed class SandwormManagerTests
{
    private const int Top = 64;
    private const float Dt = 1f / 60f;

    public SandwormManagerTests() => TestContent.EnsureLoaded();

    // Built once per biome: the worm never edits a block, and filling chunks cell by cell is the slow part
    private static readonly Lazy<ChunkManager> SharedDesert = new(() => Build(FlatSource(Biome.Desert, -5, 5)));
    private static readonly Lazy<ChunkManager> SharedForest = new(() => Build(FlatSource(Biome.Forest, -5, 5)));
    private static readonly Lazy<ChunkManager> SharedBorder = new(BuildBorder);

    private static ChunkManager Desert(Biome biome = Biome.Desert) =>
        biome == Biome.Desert ? SharedDesert.Value : SharedForest.Value;

    // Desert up to chunk column x = 0 and forest from x = 1, so a target can stand across the border
    private static ChunkManager Border() => SharedBorder.Value;

    private static ChunkManager BuildBorder()
    {
        var desert = FlatSource(Biome.Desert, -5, 0);
        var forest = FlatSource(Biome.Forest, 1, 5);
        return Build(new SplitBiomeSource(desert, forest, 0));
    }

    private static FlatWorldSource FlatSource(Biome biome, int cxMin, int cxMax)
    {
        var desert = biome == Biome.Desert;
        return new FlatWorldSource(cxMin, cxMax, -5, 5, Top, Sand, biome, desert ? 0.8f : 0.1f, desert ? 0.1f : 0.8f);
    }

    private static ushort Sand => ContentRegistry.RequireBlock("sand").Id;

    // A flat sand plateau whose top cell is Top, over 5x5 chunk columns around the origin
    private static ChunkManager Build(IWorldSource source)
    {
        var chunks = new ChunkManager(null!) { WorldSource = source };
        for (var cx = -2; cx <= 2; cx++)
        for (var cz = -2; cz <= 2; cz++)
        for (var cy = Top / Chunk.Size - 1; cy <= Top / Chunk.Size; cy++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, y, z, cy * Chunk.Size + y <= Top ? Sand : BlockType.Air, 0);
            chunks.Chunks[pos] = chunk;
        }
        return chunks;
    }

    private static SandwormManager NewManager(MobCaps? caps = null) =>
        new(EntityRegistry.Require(Sandworm.EntityName), caps);

    private static Vector3 Burrowed(float x, float z) => new(x, Top + 1 - SandwormConfig.BurrowDepthBlocks, z);

    private static Vector3 FeetOn(float x, float z) => new(x, Top + 1, z);

    private static DropManager NewDrops() => new();

    private static void Run(SandwormManager manager, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, float seconds,
        System.Func<bool>? stop = null)
    {
        var drops = NewDrops();
        for (var t = 0f; t < seconds; t += Dt)
        {
            manager.Update(Dt, chunks, drops, targets);
            if (stop != null && stop()) return;
        }
    }

    [Fact]
    public void OnlyOneWormLivesAtATime()
    {
        var manager = NewManager();
        Assert.NotNull(manager.Spawn(Burrowed(0, 0)));
        Assert.True(manager.HasLiving);
        Assert.Null(manager.Spawn(Burrowed(40, 0)));
    }

    [Fact]
    public void AFreshWormIsBurrowedAndNothingOfItCanBeHit()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        manager.Update(Dt, chunks, NewDrops(), []);

        for (var i = 0; i < worm.Chain.Count; i++) Assert.False(worm.IsExposed(i));
        Assert.False(worm.OverlapsBox(worm.Position, 1f));
        Assert.Null(worm.AimPointFrom(new Vector3(0, Top + 2, 5)));
    }

    [Fact]
    public void AWormHuntsADesertTargetBreachesUnderItAndBitesIt()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var target = new StubTarget(StubTarget.EyeAt(FeetOn(30, 0)));
        List<IMobTarget> targets = [target];

        Run(manager, chunks, targets, 30f, () => target.DamageTaken > 0);

        Assert.True(target.DamageTaken > 0);
        Assert.Equal(SandwormConfig.ChompDamage, target.DamageTaken);
        Assert.True(worm.Phase is SandwormPhase.Breach or SandwormPhase.Air);
    }

    [Fact]
    public void AnImmuneTargetIsChasedButNeverHurt()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var target = new StubTarget(StubTarget.EyeAt(FeetOn(30, 0))) { IsFlying = true };

        Run(manager, chunks, [target], 12f);

        Assert.Equal(0, target.DamageTaken);
        Assert.NotEqual(SandwormPhase.Thump, worm.Phase);
        Assert.True(worm.Position.X > 5f);
    }

    [Fact]
    public void ATargetAcrossTheDesertBorderIsStillHunted()
    {
        var chunks = Border();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var target = new StubTarget(StubTarget.EyeAt(FeetOn(40, 0)));
        List<IMobTarget> targets = [target];

        Run(manager, chunks, targets, 30f, () => target.DamageTaken > 0);

        Assert.True(target.DamageTaken > 0);
    }

    [Fact]
    public void AWormThatLosesItsTargetKeepsDiggingTowardWhereItWas()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        List<IMobTarget> targets = [new StubTarget(StubTarget.EyeAt(FeetOn(40, 0)))];

        Run(manager, chunks, targets, 1f);
        var before = worm.Position;
        targets.Clear();
        Run(manager, chunks, targets, 1f);

        Assert.True(Vector3.Distance(before, worm.Position) > 1f);
        Assert.NotEqual(SandwormPhase.Retreat, worm.Phase);
    }

    [Fact]
    public void AWormWhoseHeadStraysFromTheDesertGivesUpAndIsGone()
    {
        var chunks = Desert(Biome.Forest);
        var manager = NewManager();
        manager.Spawn(Burrowed(0, 0))!.Brain.SkipThump();

        Run(manager, chunks, [new StubTarget(StubTarget.EyeAt(FeetOn(30, 0)))], 5f, () => manager.Worms.Count == 0);

        Assert.Empty(manager.Worms);
    }

    [Fact]
    public void ABiteRestsBeforeTheNextOneLands()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var target = new StubTarget(StubTarget.EyeAt(FeetOn(0, 0)));
        List<IMobTarget> targets = [target];

        // Right over the worm: the first frame of the breach bites, and the cooldown holds the next hit off
        Run(manager, chunks, targets, 5f, () => target.DamageTaken > 0);
        var first = target.DamageTaken;
        Assert.True(first > 0);
        Run(manager, chunks, targets, SandwormConfig.ContactCooldownSeconds * 0.5f);
        Assert.Equal(first, target.DamageTaken);
    }

    [Fact]
    public void AWormWithNobodyNearRetreatsAndIsGone()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var far = new StubTarget(StubTarget.EyeAt(FeetOn(SandwormConfig.DespawnRadiusBlocks + 50, 0)));

        Run(manager, chunks, [far], 30f, () => manager.Worms.Count == 0);

        Assert.Empty(manager.Worms);
        Assert.False(manager.HasLiving);
    }

    [Fact]
    public void AKillRollsTheBossLootAndTheWormPlaysItsDeath()
    {
        var chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(Burrowed(0, 0))!;
        worm.Brain.SkipThump();
        var drops = NewDrops();

        worm.TakeDamage(worm.MaxHealth);
        manager.Update(Dt, chunks, drops, []);

        Assert.True(worm.IsDying);
        Assert.True(drops.Drops.Count > 0);
        Assert.False(manager.HasLiving);
    }

    [Fact]
    public void AForcedSpawnIsRefusedAtTheHardCap()
    {
        var caps = new MobCaps();
        var filler = new FakePopulation(MobConfig.MobHardCap);
        caps.Register(filler);
        var manager = NewManager(caps);

        Assert.Null(manager.Spawn(Burrowed(0, 0)));
    }

    [Fact]
    public void TheThumperWakesAWormOnlyOnDesertSandWithNoneAlive()
    {
        var desert = Desert();
        var forest = Desert(Biome.Forest);
        var manager = NewManager();

        Assert.False(manager.CanSummonAt(forest, 0, Top, 0));
        Assert.False(manager.CanSummonAt(desert, 0, Top + 3, 0));
        Assert.True(manager.CanSummonAt(desert, 0, Top, 0));

        var worm = manager.Summon(desert, 0, Top, 0)!;
        Assert.Equal(SandwormPhase.Thump, worm.Phase);
        Assert.True(worm.Position.Y < Top);
        Assert.False(manager.CanSummonAt(desert, 5, Top, 5));
        Assert.Null(manager.Summon(desert, 5, Top, 5));
    }

    [Fact]
    public void AnUnloadedCellCannotBeThumped()
    {
        var manager = NewManager();

        Assert.False(manager.CanSummonAt(Desert(), 500, Top, 500));
    }

    private sealed class FakePopulation(int living) : IMobPopulation
    {
        public int LivingCount { get; } = living;
    }

    // One map split at a chunk column: both halves are supplied, because SandwormGround.IsDesert reads worldgen
    // for any column the source does not supply
    private sealed class SplitBiomeSource(IWorldSource desert, IWorldSource forest, int borderChunkX) : IWorldSource
    {
        public bool Supplies(int chunkX, int chunkZ) =>
            chunkX <= borderChunkX ? desert.Supplies(chunkX, chunkZ) : forest.Supplies(chunkX, chunkZ);

        public ColumnData GetColumn(int chunkX, int chunkZ) =>
            chunkX <= borderChunkX ? desert.GetColumn(chunkX, chunkZ) : forest.GetColumn(chunkX, chunkZ);

        public void Fill(Chunk chunk)
        {
            if (chunk.Position.X <= borderChunkX) desert.Fill(chunk);
            else forest.Fill(chunk);
        }

        public bool IsReady(int chunkX, int chunkZ) =>
            chunkX <= borderChunkX ? desert.IsReady(chunkX, chunkZ) : forest.IsReady(chunkX, chunkZ);

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            chunkX <= borderChunkX ? desert.TryGet(chunkX, chunkZ, out terrain) : forest.TryGet(chunkX, chunkZ, out terrain);
    }

    // A worm standing on the surface for the hit tests: the whole chain laid flat above the sand
    private static Sandworm ExposedWorm(out ChunkManager chunks)
    {
        chunks = Desert();
        var manager = NewManager();
        var worm = manager.Spawn(new Vector3(0, Top + 1 + 1.5f, 0))!;
        manager.Update(Dt, chunks, NewDrops(), []);
        return worm;
    }

    [Fact]
    public void AnExposedSegmentIsHitByABoxAndAimedAtButABurrowedOneIsNot()
    {
        var worm = ExposedWorm(out _);
        Assert.True(worm.IsExposed(0));
        Assert.True(worm.OverlapsBox(worm.Position, 0.2f));
        Assert.NotNull(worm.AimPointFrom(worm.Position + new Vector3(0, 3, 8)));
        Assert.False(worm.OverlapsBox(worm.Position + new Vector3(0, -20, 0), 0.2f));
    }

    [Fact]
    public void ABodyHitDoesLessThanAHeadHit()
    {
        var worm = ExposedWorm(out _);
        var nearHead = worm.Position + new Vector3(0, 0, 3);
        var nearBody = worm.Chain[5] + new Vector3(0, 3, 0);

        var headBlow = worm.ScaleDamage(10, nearHead);
        var bodyBlow = worm.ScaleDamage(10, nearBody);

        Assert.Equal(10, headBlow);
        Assert.True(bodyBlow < headBlow);
        Assert.True(bodyBlow >= 1);
    }
}
