using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The cosmetic ground dust: a landing from a real fall kicks up a puff of chips of the block
/// landed on, decided by <see cref="LandingDust"/> and drawn as <see cref="BlockDebris"/> through
/// <see cref="BlockDebrisSystem.Puff"/>, and a landing slime splats chips of its own green instead
/// (<see cref="BlockDebrisSystem.Splat"/>). Owned by <see cref="World"/>, which feeds it the local
/// player each frame (singleplayer and a client, whose own physics runs locally) and the mobs'
/// landings in singleplayer; a headless server never calls it. Main thread only.
/// </summary>
public sealed class GroundDust
{
    // The player's own landings, tracked apart from the health pool's FallTracker so the dust runs
    // the same way on a client, which bills no fall damage itself
    private readonly FallTracker _landing = new();

    /// <summary>
    /// One frame of the local player: a landing that ended a fall kicks up its puff. Landing in
    /// water raises none.
    /// </summary>
    public void UpdatePlayer(Camera camera, ChunkManager chunks, BlockDebrisSystem debris, Random? random = null)
    {
        var feet = camera.Position - new Vector3(0f, Camera.EyeHeight, 0f);
        _landing.Observe(feet.Y, camera.IsGrounded, camera.IsFlying, camera.IsInWater, camera.IsCreative,
            FallTracker.DefaultMaxStepBlocks);
        if (_landing.LandedDrop > 0f && !camera.IsInWater)
            Land(feet, _landing.LandedDrop, PlayerPhysics.PlayerRadius, chunks, debris, random);
    }

    /// <summary>
    /// Takes every landing the <paramref name="mobs"/> noted this frame and raises its dust: a slime
    /// splats its own goo on every real landing (<see cref="Splat"/>), any other mob kicks up the
    /// ground's dust after a real fall (<see cref="Land"/>).
    /// </summary>
    public void TakeMobLandings(IReadOnlyList<Mob> mobs, ChunkManager chunks, BlockDebrisSystem debris,
        Random? random = null)
    {
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (!mob.TryTakeLanding(out var impactSpeed, out var fallBlocks)) continue;
            var feet = mob.Position - new Vector3(0f, mob.BodyHalfHeight, 0f);
            if (mob is Slime)
                Splat(feet, impactSpeed, SplatColourOf(mob.Type), mob.BodyHalfExtent, debris, random: random);
            else
                Land(feet, fallBlocks, mob.BodyHalfExtent, chunks, debris, random);
        }
    }

    // Each splatting kind's skin colour, read once from its model
    private readonly Dictionary<EntityType, Vector3> _splatColours = new();

    private Vector3 SplatColourOf(EntityType type)
    {
        if (!_splatColours.TryGetValue(type, out var colour))
        {
            colour = LandingDust.SkinColour(type.Model, WorldGenerationConfig.SlimeSplatFallbackColor);
            _splatColours[type] = colour;
        }
        return colour;
    }

    /// <summary>
    /// A slime whose feet are at <paramref name="feet"/> landed at <paramref name="impactSpeed"/>
    /// blocks per second: throws the goo <see cref="LandingDust.SplatCount"/> sizes, in
    /// <paramref name="colourSrgb"/>, from under a body <paramref name="halfWidth"/> across.
    /// <paramref name="scale"/> is the slime's size (1 today), growing the count, the fling and the
    /// chips. Returns how many chips it spawned.
    /// </summary>
    public static int Splat(Vector3 feet, float impactSpeed, Vector3 colourSrgb, float halfWidth,
        BlockDebrisSystem debris, float scale = 1f, Random? random = null)
    {
        var count = LandingDust.SplatCount(impactSpeed, scale);
        if (count <= 0) return 0;
        var size = WorldGenerationConfig.SlimeSplatSize * scale;
        debris.Splat(feet + new Vector3(0f, size, 0f), colourSrgb, halfWidth * scale, count,
            LandingDust.SplatSpeed(impactSpeed, scale), size, random);
        return count;
    }

    /// <summary>
    /// A body whose feet are at <paramref name="feet"/> landed after falling
    /// <paramref name="fallBlocks"/>: kicks up the puff <see cref="LandingDust"/> sizes, of the block
    /// under the feet. Returns how many chips it spawned (0 for a short fall or no ground found).
    /// </summary>
    public static int Land(Vector3 feet, float fallBlocks, float halfWidth, ChunkManager chunks,
        BlockDebrisSystem debris, Random? random = null)
    {
        var count = LandingDust.Count(fallBlocks);
        if (count <= 0) return 0;
        if (!TryFindSurface(chunks, feet, halfWidth, out var cell, out var blockId)) return 0;
        debris.Puff(feet + new Vector3(0f, WorldGenerationConfig.LandingDustSize, 0f), cell, blockId, count,
            LandingDust.Speed(fallBlocks), WorldGenerationConfig.LandingDustSize, random, chunks.ColumnSource);
        return count;
    }

    /// <summary>
    /// The block a body standing with its feet at <paramref name="feet"/> stands on, and its cell:
    /// the feet's own cell under their centre first (a cover the feet sink into, such as a layered
    /// snow drift, or a slab the body stands on), then the cell below, under the centre and then under
    /// the four corners of a body <paramref name="halfWidth"/> across, so a body on a ledge's edge
    /// still finds the ledge. Only a solid block or a stack block (snow, pebbles) counts; a plant or
    /// a fluid is looked through. False when nothing is underfoot.
    /// </summary>
    public static bool TryFindSurface(ChunkManager chunks, Vector3 feet, float halfWidth, out Vector3 cell,
        out byte blockId)
    {
        var x0 = MathUtils.FloorToInt(feet.X);
        var z0 = MathUtils.FloorToInt(feet.Z);
        var feetRow = MathUtils.FloorToInt(feet.Y + 0.01f);
        if (Probe(chunks, x0, feetRow, z0, out cell, out blockId)) return true;

        var belowRow = MathUtils.FloorToInt(feet.Y - 0.05f);
        if (belowRow == feetRow) return false;
        if (Probe(chunks, x0, belowRow, z0, out cell, out blockId)) return true;
        // A hair inside the body, so a body pressed against a wall never reads the wall's cell
        var h = halfWidth * 0.9f;
        for (var corner = 0; corner < 4; corner++)
        {
            var x = MathUtils.FloorToInt(feet.X + ((corner & 1) == 0 ? -h : h));
            var z = MathUtils.FloorToInt(feet.Z + ((corner & 2) == 0 ? -h : h));
            if (Probe(chunks, x, belowRow, z, out cell, out blockId)) return true;
        }

        return false;
    }

    private static bool Probe(ChunkManager chunks, int x, int y, int z, out Vector3 cell, out byte blockId)
    {
        blockId = chunks.GetBlockGlobal(x, y, z);
        cell = new Vector3(x, y, z);
        if (StandsOn(blockId)) return true;
        blockId = BlockType.Air;
        return false;
    }

    // What dust can come off: a solid block, or a stack block a body stands in or on (a snow
    // drift's partial layers are walked through, yet that is the ground the feet scuff).
    private static bool StandsOn(byte id) =>
        id != BlockType.Air && !BlockType.IsFluid(id)
        && (BlockType.IsSolid(id) || BlockType.ById[id] is { StackMax: > 0 });
}
