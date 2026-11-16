using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Cave slimes (<see cref="MobSpawner.TryFindCaveSpawn"/>): slimes spawn on dark, dry cave floors well
/// under the surface around a player in the cave, never where sunlight or man-made light reaches nor in
/// hell (natural light, glowing plants and crystals, never keeps them away),
/// and the size roll treats them as underground.
/// </summary>
public sealed class CaveSlimeTests
{
    public CaveSlimeTests() => TestContent.EnsureLoaded();

    private const int Floor = CaveTestWorld.Floor;

    // A floor deep in hell, its cave inside one chunk like the overworld one's
    private const int HellFloor = ((WorldGenerationConfig.HellTopY - 40) >> Chunk.SizeShift) * Chunk.Size + 4;

    private static ChunkManager Cave(int sun, int floor = Floor, ushort floorId = 0) =>
        CaveTestWorld.Cave(sun, floor, floorId);

    // A solid emitter bright enough to light the standing cell over the limit, man-made or natural as asked
    private static ushort BrightFloor(bool natural) =>
        BlockType.Types.Values.First(b => b.Solid && b.NaturalLight == natural
            && b.LightEmission > MobConfig.MobSpawnBlockLightLimit).Id;

    // Standing on the cave floor at the origin
    private static readonly Vector3 PlayerInCave = CaveTestWorld.EyeOnFloor();

    /// <summary>The slime's cave search over every cave biome (the biome filter has tests of its own).</summary>
    internal static readonly CaveSpawnSearch AnyCave = new(
        MobConfig.SlimeSpawnMinRadiusBlocks, MobConfig.SlimeSpawnMaxRadiusBlocks,
        MobConfig.SlimeCaveSpawnTries, MobConfig.SlimeCaveSearchHalfHeight,
        MobConfig.SlimeCaveScanDepth, MobConfig.SlimeCaveMaxSkyLight,
        MobConfig.MobSpawnBlockLightLimit, CaveSpawnRules.AllBiomes);

    private static bool Search(ChunkManager chunks, out int x, out int groundY, out int z) =>
        Search(chunks, PlayerInCave, out x, out groundY, out z);

    private static bool Search(ChunkManager chunks, Vector3 player, out int x, out int groundY, out int z) =>
        MobSpawner.TryFindCaveSpawn(chunks, player, AnyCave, null, out x, out groundY, out z, out _);

    [Fact]
    public void ADarkCaveFloorAroundThePlayerTakesASpawn()
    {
        var chunks = Cave(sun: 0);
        var found = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!Search(chunks, out var x, out var groundY, out var z)) continue;
            found++;
            Assert.Equal(Floor, groundY);
            var ring = MathF.Sqrt((x + 0.5f - PlayerInCave.X) * (x + 0.5f - PlayerInCave.X)
                                  + (z + 0.5f - PlayerInCave.Z) * (z + 0.5f - PlayerInCave.Z));
            Assert.InRange(ring, MobConfig.SlimeSpawnMinRadiusBlocks - 2f,
                MobConfig.SlimeSpawnMaxRadiusBlocks + 2f);
        }
        Assert.True(found > 0, "no cave spawn was ever found");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASunlitOrLampLitCaveTakesNone(bool sunlit)
    {
        var chunks = sunlit ? Cave(sun: 15) : Cave(sun: 0, floorId: BrightFloor(natural: false));
        for (var attempt = 0; attempt < 30; attempt++)
            Assert.False(Search(chunks, out _, out _, out _));
    }

    [Fact]
    public void ACaveLitOnlyByNaturalLightStillTakesASpawn()
    {
        var chunks = Cave(sun: 0, floorId: BrightFloor(natural: true));
        var found = false;
        for (var attempt = 0; attempt < 30 && !found; attempt++)
            found = Search(chunks, out _, out _, out _);
        Assert.True(found, "natural light kept a cave spawn away");
    }

    [Fact]
    public void ADarkCaveInHellTakesNone()
    {
        var chunks = Cave(sun: 0, floor: HellFloor);
        var player = PlayerInCave with { Y = HellFloor + 1 + Camera.EyeHeight };
        for (var attempt = 0; attempt < 30; attempt++)
            Assert.False(Search(chunks, player, out _, out _, out _));
    }

    [Fact]
    public void ASlimeRingAroundAPlayerInTheCaveSpawnsOnItsFloorAsAnUndergroundSlime()
    {
        var chunks = Cave(sun: 0);
        // A roll that grows a slime under the night (underground) chance but not under the day one
        var roll = (MobConfig.SlimeLargeChanceDay + MobConfig.SlimeLargeChanceNight) * 0.5f;
        Assert.Equal(SlimeSize.Min, SlimeSize.Roll(night: 0f, underground: false, roll));

        var found = false;
        for (var attempt = 0; attempt < 30 && !found; attempt++)
        {
            if (!SlimeManager.TryFindSpawn(chunks, PlayerInCave, night: 0f, roll, out var position, out var size))
                continue;
            found = true;
            Assert.True(size > SlimeSize.Min, "a cave slime rolled as if it stood on the surface by day");
            Assert.Equal(Floor + 1f, position.Y - Slime.HalfExtentFor(size), 3);
            Assert.False(Slime.IsColliding(position, Slime.HalfExtentFor(size), chunks));
        }
        Assert.True(found, "the slime ring never found the cave floor");
    }
}
