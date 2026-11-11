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
/// until it is crowded or hurt and then charges and gores only those in reach, a crow and a dragonfly strike a
/// player they notice from the air, a flyer left alone lands, rests and takes off again (at once when hurt)
/// and tells its mirror so, the species a spawn becomes follows its biome and the water near it, the animals
/// fill their own budget beside the hostile mobs', and each species has its own network kind.
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

    // A stone floor three chunks on a side around the origin chunk, so a wandering flyer keeps ground under it
    private static ChunkManager WideFloor()
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, 0, cz), generateBlockData: false) { IsBlockDataReady = true };
            chunks.Chunks[new Vector3(cx, 0, cz)] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 0, z, stone, 0);
        }
        return chunks;
    }

    // A flyer of the named species in the air over (x, z), already settled
    private static T Airborne<T>(WildlifeManager manager, string name, float x, float z, float height = 5f) where T : Animal =>
        Assert.IsAssignableFrom<T>(manager.Spawn(name, new Vector3(x, 1f + height, z), spawnIn: false));

    // Steps until the condition holds; false when the time runs out first
    private static bool RunUntil(WildlifeManager manager, ChunkManager chunks, IReadOnlyList<IMobTarget> targets,
        float seconds, System.Func<bool> done)
    {
        var drops = new DropManager();
        for (var t = 0f; t < seconds; t += Dt)
        {
            manager.Update(Dt, chunks, drops, targets);
            if (done()) return true;
        }
        return false;
    }

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
        Assert.True(dire.StrikeDamage > boar.StrikeDamage && dire.MaxHealth > boar.MaxHealth);
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
    public void AnUnprovokedCrowStealsALooseItemInsteadOfAttacking()
    {
        var chunks = WideFloor();
        var manager = NewManager();
        var crow = Airborne<Crow>(manager, Crow.EntityName, 8f, 8f);
        var drops = new DropManager();
        drops.SpawnAt(ContentRegistry.RequireBlock("stone").Id, new Vector3(4.5f, 1.2f, 8.5f));
        var player = PlayerAt(8f + WildlifeConfig.CrowNoticeRangeBlocks * 0.5f, 8f);

        for (var t = 0f; t < 40f && drops.Drops.Count > 0; t += 0.05f)
        {
            drops.Update(0.05f, player.Eye, chunks, _ => false);
            manager.Update(0.05f, chunks, drops, [player]);
        }

        Assert.Empty(drops.Drops);
        Assert.Equal(0, player.DamageTaken);
    }

    [Fact]
    public void ACrowHarassesAPlayerItNoticesAndLeavesADistantOneAlone()
    {
        var chunks = WideFloor();
        var manager = NewManager();
        var crow = Airborne<Crow>(manager, Crow.EntityName, 8f, 8f);

        var distant = PlayerAt(8f + WildlifeConfig.CrowNoticeRangeBlocks + 6f, 8f);
        Assert.False(crow.Heeds([distant]));

        var near = PlayerAt(8f + WildlifeConfig.CrowNoticeRangeBlocks * 0.5f, 8f);
        Assert.False(crow.Heeds([near]), "a crow nobody has hurt must leave players alone");
        crow.TakeDamage(1);
        Assert.True(crow.Heeds([near]));
        Assert.True(RunUntil(manager, chunks, [near], 30f, () => near.DamageTaken > 0), "the crow never struck the player beside it");
        Assert.Equal(0, distant.DamageTaken);

        // The swoop only rakes what it reaches
        Assert.True(Crow.InSwoopReach(new Vector3(0f, 1.5f, 0f), StubTarget.EyeAt(new Vector3(0.5f, 0f, 0f))));
        Assert.False(Crow.InSwoopReach(new Vector3(0f, 1.5f, 0f), StubTarget.EyeAt(new Vector3(WildlifeConfig.CrowStrikeReach + 2f, 0f, 0f))));
    }

    [Fact]
    public void ADragonflyDartsAtAPlayerItNoticesAndBitesOnlyInReach()
    {
        var chunks = WideFloor();
        var manager = NewManager();
        var dragonfly = Airborne<Dragonfly>(manager, Dragonfly.EntityName, 8f, 8f);
        var player = PlayerAt(8f + WildlifeConfig.DragonflyNoticeRangeBlocks * 0.5f, 8f);

        Assert.True(dragonfly.Heeds([player]));
        Assert.True(RunUntil(manager, chunks, [player], 30f, () => player.DamageTaken > 0), "the dragonfly never bit the player");
        Assert.Equal(0, player.DamageTaken % dragonfly.StrikeDamage);

        Assert.False(Dragonfly.InBiteReach(dragonfly.Position, dragonfly.Position + new Vector3(WildlifeConfig.DragonflyBiteReach + 3f, 0f, 0f)));
        var flier = new StubTarget(player.Eye) { IsFlying = true };
        Assert.False(dragonfly.Heeds([flier]));
    }

    [Theory]
    [InlineData(Crow.EntityName)]
    [InlineData(Dragonfly.EntityName)]
    public void AFlyerLeftAloneLandsRestsAndTakesOffAgainAndAtOnceWhenHurt(string name)
    {
        var chunks = WideFloor();
        var manager = NewManager();
        var flyer = Assert.IsAssignableFrom<FlyingAnimal>(Airborne<Animal>(manager, name, 8f, 8f));
        // In play, but far beyond anything the flyer reacts to
        var player = PlayerAt(8f + 60f, 8f);
        Assert.False(flyer.IsPerched);

        var landBy = WildlifeConfig.FlyerAirMaxSeconds + 30f;
        Assert.True(RunUntil(manager, chunks, [player], landBy, () => flyer.IsPerched && flyer.IsGrounded),
            $"the {name} never came down to rest");
        Assert.True(flyer.Feet.Y < 1.2f, $"a resting {name} should stand on the floor, not hang at {flyer.Feet.Y}");

        Assert.True(RunUntil(manager, chunks, [player], WildlifeConfig.FlyerPerchMaxSeconds + 5f, () => !flyer.IsPerched),
            $"the {name} never took off again");

        // Resting again, a hit sends it up at once
        Assert.True(RunUntil(manager, chunks, [player], landBy, () => flyer.IsPerched && flyer.IsGrounded));
        flyer.TakeDamage(1);
        Assert.False(flyer.IsPerched);
    }

    [Theory]
    [InlineData(Crow.EntityName)]
    [InlineData(Dragonfly.EntityName)]
    public void ARestingFlyerTakesOffWhenAPlayerComesNear(string name)
    {
        var chunks = WideFloor();
        var manager = NewManager();
        var flyer = Assert.IsAssignableFrom<FlyingAnimal>(Airborne<Animal>(manager, name, 8f, 8f));
        var away = PlayerAt(8f + 60f, 8f);
        Assert.True(RunUntil(manager, chunks, [away], WildlifeConfig.FlyerAirMaxSeconds + 30f,
            () => flyer.IsPerched && flyer.IsGrounded && !flyer.Animation.IsActionPlaying));

        // Four blocks off: inside what either species notices, outside a crow's peck
        var near = PlayerAt(flyer.Position.X + 4f, flyer.Position.Z);
        (flyer as Crow)?.TakeDamage(1);
        Assert.True(RunUntil(manager, chunks, [near], 1.5f, () => !flyer.IsPerched), $"the resting {name} sat still beside a player");
        Assert.True(RunUntil(manager, chunks, [near], 3f, () => flyer.Feet.Y > 2f), $"the {name} left the ground but did not climb");
    }

    [Theory]
    [InlineData(Crow.EntityName)]
    [InlineData(Dragonfly.EntityName)]
    public void AFlyersMirrorTakesItsPostureFromTheServer(string name)
    {
        var type = EntityRegistry.Require(name);
        var server = Assert.IsAssignableFrom<FlyingAnimal>(WildlifeSpecies.Create(type, new Vector3(0f, 80f, 0f), spawnIn: false));
        var mirror = Assert.IsAssignableFrom<FlyingAnimal>(WildlifeSpecies.Create(type, new Vector3(0f, 80f, 0f), spawnIn: false));

        // Every posture a server can report reads back the same on the mirror
        for (byte posture = 0; posture < 4; posture++)
        {
            mirror.MirrorPosture(posture);
            Assert.Equal(posture, mirror.Posture);
            Assert.Equal((posture & 1) != 0, mirror.IsPerched);
        }
        Assert.Equal(0, server.Posture);
    }

    [Fact]
    public void ASpawnBecomesOnlyASpeciesLivingInItsBiomeEachAsLikelyAsItsWeight()
    {
        foreach (var biome in System.Enum.GetValues<Biome>())
        foreach (var nearWater in new[] { false, true })
        {
            var living = WildlifeSpawnRules.All.Where(r => r.LivesIn(biome, nearWater)).ToArray();
            for (var i = 0; i < 50; i++)
            {
                var picked = WildlifeSpawnRules.Pick(biome, nearWater, i / 50f, _ => true);
                if (living.Length == 0) Assert.Null(picked);
                else Assert.Contains(picked!.Value.Kind, living.Select(r => r.Kind));
            }
        }

        // The ocean is nobody's home, and a species whose content is missing is never picked
        Assert.Null(WildlifeSpawnRules.Pick(Biome.Ocean, true, 0.5f, _ => true));
        for (var i = 0; i < 50; i++)
            Assert.NotEqual(WildlifeKind.Deer, WildlifeSpawnRules.Pick(Biome.Forest, false, i / 50f, kind => kind != WildlifeKind.Deer)!.Value.Kind);
        Assert.Null(WildlifeSpawnRules.Pick(Biome.Forest, false, 0.5f, _ => false));

        // A waterside species spawns beside water and never away from it
        var waterside = WildlifeSpawnRules.All.Where(r => r.NearWater).ToArray();
        Assert.NotEmpty(waterside);
        foreach (var rule in waterside)
        foreach (var biome in rule.Biomes)
        {
            Assert.True(rule.LivesIn(biome, nearWater: true));
            Assert.False(rule.LivesIn(biome, nearWater: false));
            for (var i = 0; i < 100; i++)
                Assert.NotEqual(rule.Kind, WildlifeSpawnRules.Pick(biome, false, i / 100f, _ => true)?.Kind);
            Assert.Contains(Enumerable.Range(0, 400), i => WildlifeSpawnRules.Pick(biome, true, i / 400f, _ => true)?.Kind == rule.Kind);
        }

        // A heavier species takes a larger share of the rolls
        var forest = WildlifeSpawnRules.All.Where(r => r.LivesIn(Biome.Forest, false)).OrderBy(r => r.Weight).ToArray();
        var counts = new Dictionary<WildlifeKind, int>();
        const int rolls = 2000;
        for (var i = 0; i < rolls; i++)
        {
            var kind = WildlifeSpawnRules.Pick(Biome.Forest, false, (i + 0.5f) / rolls, _ => true)!.Value.Kind;
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
