using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Entities.Wildlife;

/// <summary>
/// The animals of the wild, driven headless: every species' shipped model satisfies its contract, a deer
/// only watches from afar and bolts away from a player who comes close or hurts it, a boar ignores everyone
/// until it is crowded or hurt and then charges and gores only those in reach, the species a spawn becomes
/// follows its biome, the animals fill their own budget beside the hostile mobs', and each species has its
/// own network kind.
/// </summary>
public sealed class WildlifeTests
{
    public WildlifeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    private static IEnumerable<EntityType> AnimalTypes() => WildlifeSpecies.Names.Select(EntityRegistry.Require);

    private static WildlifeManager NewManager(MobCaps? caps = null) => new(AnimalTypes(), caps);

    // An animal of the named species standing on the y = 0 floor at (x, z), already settled
    private static T Standing<T>(WildlifeManager manager, string name, float x, float z) where T : Animal =>
        Assert.IsType<T>(manager.Spawn(name, new Vector3(x, 1f + WildlifeSpecies.HeightOf(name) * 0.5f, z), spawnIn: false));

    private static void Run(WildlifeManager manager, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, float seconds)
    {
        var drops = new DropManager();
        for (var t = 0f; t < seconds; t += Dt)
            manager.Update(Dt, chunks, drops, targets);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) =>
        new Vector2(a.X - b.X, a.Z - b.Z).Length();

    [Fact]
    public void EveryShippedSpeciesSatisfiesItsContractAndHasItsOwnBody()
    {
        foreach (var type in AnimalTypes())
        {
            WildlifeSpecies.ValidateContent(type);
            var animal = Assert.IsAssignableFrom<Animal>(WildlifeSpecies.Create(type, Vector3.Zero, spawnIn: false));
            Assert.Equal(type.Name, WildlifeSpecies.NameOf(animal.Kind));
            Assert.Equal(WildlifeSpecies.HeightOf(type.Name), animal.BodyHeight);
            Assert.Equal(WildlifeSpecies.HalfExtentOf(type.Name), animal.BodyHalfWidth);
            // Every counted action clip it can start exists in its model, so a mirror can always replay it
            for (var count = 0; count < 6; count++)
                Assert.True(animal.Animation.HasClip(animal.AttackClipFor(count)), $"{type.Name} lacks {animal.AttackClipFor(count)}");
        }
        Assert.Null(WildlifeSpecies.Create(EntityRegistry.Require("slime"), Vector3.Zero));
    }

    [Fact]
    public void ADeerIgnoresADistantPlayerWatchesANearOneAndBoltsFromACloseOne()
    {
        var chunks = StoneFloor();
        var manager = NewManager();
        var deer = Standing<Deer>(manager, Deer.EntityName, 8f, 8f);

        // Beyond its alert range it does not react at all
        var far = PlayerAt(8f - WildlifeConfig.DeerAlertRangeBlocks - 4f, 8f);
        Assert.False(deer.Heeds([far]));

        // Inside the alert range but out of the flee range it watches without running
        var near = PlayerAt(8f - (WildlifeConfig.DeerAlertRangeBlocks + WildlifeConfig.DeerFleeRangeBlocks) * 0.5f, 8f);
        Assert.True(deer.Heeds([near]));
        Run(manager, chunks, [near], 0.5f);
        Assert.False(deer.IsFleeing);
        Assert.True(HorizontalDistance(deer.Position, new Vector3(8f, 0f, 8f)) < 0.5f, "a watching deer should hold its ground");

        // A player stepping inside the flee range sends it away from them, in its run
        var close = PlayerAt(8f - WildlifeConfig.DeerFleeRangeBlocks * 0.5f, 8f);
        var before = HorizontalDistance(deer.Position, close.Eye);
        Run(manager, chunks, [close], 0.6f);
        Assert.True(deer.IsFleeing);
        Assert.True(HorizontalDistance(deer.Position, close.Eye) > before + 1f, "a bolting deer should gain distance");
        Assert.True(deer.IsRunning, "a bolting deer should be in its run gait");
        Assert.Equal(0, close.DamageTaken);
    }

    [Fact]
    public void AHurtDeerBoltsEvenFromAPlayerItWasOnlyWatching()
    {
        var chunks = StoneFloor();
        var manager = NewManager();
        var deer = Standing<Deer>(manager, Deer.EntityName, 8f, 8f);
        var near = PlayerAt(8f - (WildlifeConfig.DeerAlertRangeBlocks + WildlifeConfig.DeerFleeRangeBlocks) * 0.5f, 8f);

        deer.TakeDamage(1);
        var before = HorizontalDistance(deer.Position, near.Eye);
        Run(manager, chunks, [near], 0.6f);

        Assert.True(deer.IsFleeing);
        Assert.True(HorizontalDistance(deer.Position, near.Eye) > before + 1f);
    }

    [Fact]
    public void ABoarIgnoresAPasserByButChargesWhoCrowdsIt()
    {
        var chunks = StoneFloor();
        var manager = NewManager();
        var boar = Standing<Boar>(manager, Boar.EntityName, 8f, 8f);

        // Well outside its personal space nobody concerns it, however long they stand there (it wanders meanwhile)
        var passerBy = PlayerAt(8f - WildlifeConfig.BoarCrowdRangeBlocks - 10f, 8f);
        Run(manager, chunks, [passerBy], WildlifeConfig.BoarCrowdSeconds + 1f);
        Assert.False(boar.IsProvoked);
        Assert.Equal(0, passerBy.DamageTaken);

        // Standing inside it is tolerated for a moment, then provokes it
        var crowding = PlayerAt(boar.Position.X - WildlifeConfig.BoarCrowdRangeBlocks + 1f, boar.Position.Z);
        Run(manager, chunks, [crowding], WildlifeConfig.BoarCrowdSeconds * 0.5f);
        Assert.False(boar.IsProvoked);
        Run(manager, chunks, [crowding], WildlifeConfig.BoarCrowdSeconds);
        Assert.True(boar.IsProvoked);

        // Provoked, it closes in and its gore hurts the player who stays in reach
        Run(manager, chunks, [crowding], 4f);
        Assert.True(crowding.DamageTaken > 0, "a provoked boar should have gored the player beside it");
    }

    [Fact]
    public void AHurtBoarIsProvokedAndADireBoarNeedsNoProvoking()
    {
        var chunks = StoneFloor();
        var manager = NewManager();
        var boar = Standing<Boar>(manager, Boar.EntityName, 3f, 3f);
        var dire = Standing<Boar>(manager, Boar.DireEntityName, 12f, 12f);
        var player = PlayerAt(12f, 12f - WildlifeConfig.BoarCrowdRangeBlocks - 2f);

        Assert.False(boar.IsProvoked);
        boar.TakeDamage(1);
        Assert.True(boar.IsProvoked);

        Assert.True(dire.IsDire && !dire.IsProvoked);
        Run(manager, chunks, [player], 0.2f);
        Assert.True(dire.IsProvoked, "a dire boar turns on anyone it notices");
        Assert.True(dire.GoreDamage > boar.GoreDamage && dire.MaxHealth > boar.MaxHealth);
    }

    [Fact]
    public void ABoarsGoreOnlyLandsOnTargetsInReachAndNeverOnAnImmuneOne()
    {
        var center = new Vector3(8f, 1f + WildlifeConfig.BoarHeight * 0.5f, 8f);
        var inReach = StubTarget.EyeAt(new Vector3(8f + WildlifeConfig.BoarGoreReach * 0.5f, 1f, 8f));
        var outOfReach = StubTarget.EyeAt(new Vector3(8f + WildlifeConfig.BoarGoreReach + 2f, 1f, 8f));
        Assert.True(Boar.InGoreReach(center, inReach, WildlifeConfig.BoarGoreReach));
        Assert.False(Boar.InGoreReach(center, outOfReach, WildlifeConfig.BoarGoreReach));

        var chunks = StoneFloor();
        var manager = NewManager();
        var boar = Standing<Boar>(manager, Boar.EntityName, 8f, 8f);
        var flier = new StubTarget(inReach) { IsFlying = true };
        boar.TakeDamage(1);
        Run(manager, chunks, [flier], 4f);
        Assert.Equal(0, flier.DamageTaken);
    }

    [Fact]
    public void ASpawnBecomesOnlyASpeciesLivingInItsBiomeEachAsLikelyAsItsWeight()
    {
        foreach (var biome in System.Enum.GetValues<Biome>())
        {
            var living = WildlifeSpawnRules.All.Where(r => r.LivesIn(biome)).ToArray();
            for (var i = 0; i < 50; i++)
            {
                var picked = WildlifeSpawnRules.Pick(biome, i / 50f, _ => true);
                if (living.Length == 0) Assert.Null(picked);
                else Assert.Contains(picked!.Value.Kind, living.Select(r => r.Kind));
            }
        }

        // The ocean is nobody's home, and a species whose content is missing is never picked
        Assert.Null(WildlifeSpawnRules.Pick(Biome.Ocean, 0.5f, _ => true));
        for (var i = 0; i < 50; i++)
            Assert.NotEqual(WildlifeKind.Deer, WildlifeSpawnRules.Pick(Biome.Forest, i / 50f, kind => kind != WildlifeKind.Deer)!.Value.Kind);
        Assert.Null(WildlifeSpawnRules.Pick(Biome.Forest, 0.5f, _ => false));

        // A heavier species takes a larger share of the rolls
        var forest = WildlifeSpawnRules.All.Where(r => r.LivesIn(Biome.Forest)).OrderBy(r => r.Weight).ToArray();
        var counts = new Dictionary<WildlifeKind, int>();
        const int rolls = 2000;
        for (var i = 0; i < rolls; i++)
        {
            var kind = WildlifeSpawnRules.Pick(Biome.Forest, (i + 0.5f) / rolls, _ => true)!.Value.Kind;
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        for (var i = 1; i < forest.Length; i++)
            Assert.True(counts.GetValueOrDefault(forest[i].Kind) >= counts.GetValueOrDefault(forest[i - 1].Kind));
    }

    [Fact]
    public void AnimalsFillTheirOwnBudgetBesideTheHostileCap()
    {
        var caps = new MobCaps();
        var wildlife = NewManager(caps);
        Assert.True(wildlife.IsWildlife);

        // A wild full of animals leaves the hostile natural cap untouched
        for (var i = 0; i < caps.WildlifeCap; i++)
            Assert.NotNull(wildlife.Spawn(Deer.EntityName, new Vector3(i, 100f, 0f), spawnIn: false));
        Assert.False(caps.AllowsWildlifeSpawn);
        Assert.True(caps.AllowsNaturalSpawn);
        Assert.Equal(1f, caps.WildlifeSpawnHaste, 1e-5f);
        Assert.True(caps.SpawnHaste > 1f);

        // ... and a world full of hostiles leaves the animals theirs; both count toward the one hard cap
        var roomy = new MobCaps();
        var animals = NewManager(roomy);
        var hostile = new SlimeManager(EntityRegistry.Require("slime"), roomy);
        for (var i = 0; i < roomy.NaturalCap; i++)
            Assert.NotNull(hostile.Spawn(new Vector3(i, 100f, 0f)));
        Assert.False(roomy.AllowsNaturalSpawn);
        Assert.True(roomy.AllowsWildlifeSpawn);
        Assert.Equal(roomy.NaturalCap, roomy.Living);
        Assert.NotNull(animals.Spawn(Boar.EntityName, new Vector3(0f, 100f, 5f), spawnIn: false));
        Assert.Equal(roomy.NaturalCap + 1, roomy.Living);
    }

    [Fact]
    public void AnIdleAnimalStaysWhileAHostileMobWouldFadeAndOneOutOfPlayStillGoes()
    {
        var chunks = StoneFloor();
        var manager = NewManager();
        var deer = Standing<Deer>(manager, Deer.EntityName, 8f, 8f);
        // Far enough that it never reacts and a hostile mob would count as idle and far, yet well in play
        var player = PlayerAt(8f + MobConfig.MobIdleDespawnMinDistanceBlocks + 6f, 8f);

        var drops = new DropManager();
        for (var t = 0f; t < MobConfig.MobIdleDespawnSeconds + 5f; t += 0.25f)
            manager.Update(0.25f, chunks, drops, [player]);
        Assert.False(deer.IsDying);

        var gone = PlayerAt(8f + WildlifeConfig.DespawnRadiusBlocks + 10f, 8f);
        manager.Update(Dt, chunks, drops, [gone]);
        Assert.True(deer.IsDying);
    }

    [Fact]
    public void EachSpeciesHasItsOwnNetworkKindAndNothingElseReadsAsOne()
    {
        var kinds = new HashSet<NetEntityKind>();
        foreach (var name in WildlifeSpecies.Names)
        {
            var species = WildlifeSpecies.KindOf(name)!.Value;
            var kind = NetEntity.KindOf(species);
            Assert.True(kinds.Add(kind));
            Assert.True(NetEntity.IsMob(kind));
            Assert.True(NetEntity.TryGetWildlife(kind, out var back));
            Assert.Equal(species, back);
        }

        foreach (var kind in System.Enum.GetValues<NetEntityKind>())
            if (!kinds.Contains(kind)) Assert.False(NetEntity.TryGetWildlife(kind, out _), $"{kind} read as a wild animal");
    }
}
