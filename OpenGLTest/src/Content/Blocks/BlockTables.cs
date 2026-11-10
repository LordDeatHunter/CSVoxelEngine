using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// Sizes <see cref="BlockType"/>'s per-id tables to the loaded content and bounds the ids that
/// arrive from outside the process. <see cref="ContentRegistry.Load"/> calls <see cref="Allocate"/>
/// once it has counted the block and item files, before any loader writes a table and before any
/// world thread exists, so a table is never resized while a worker reads it.
/// </summary>
internal static class BlockTables
{
    /// <summary>How many ids the loaded content uses: air plus every block and item.</summary>
    internal static int IdCount => BlockType.ById.Length;

    /// <summary>Whether <paramref name="id"/> names a loaded block or item (air is not one), the
    /// server's check on an id a client sent.</summary>
    internal static bool IsAssignedId(int id) => id > 0 && id < IdCount;

    /// <summary>
    /// <paramref name="id"/> as a content id when it is air or a loaded block or item; a peer's id
    /// outside this build's content throws <see cref="InvalidDataException"/>, which the client's
    /// message loop turns into a clean disconnect instead of an out-of-range table read.
    /// </summary>
    internal static ushort RequireAssigned(int id)
    {
        if ((uint)id >= (uint)IdCount)
            throw new InvalidDataException($"block or item id {id} is not in this build's content");
        return (ushort)id;
    }

    /// <summary>Whether every id in <paramref name="ids"/> is air or a loaded block or item: the client's check on a
    /// streamed chunk's ids before it stores them, since a mesh build indexes the tables with each one.</summary>
    internal static bool AllInRange(ReadOnlySpan<ushort> ids) =>
        ids.IndexOfAnyExceptInRange((ushort)0, (ushort)(IdCount - 1)) < 0;

    /// <summary>
    /// Replaces every per-id table on <see cref="BlockType"/> with a fresh one of
    /// <paramref name="idCount"/> entries, empties <see cref="BlockType.Types"/> and the sky-cover
    /// set, and seeds air's defaults. Every static array on <see cref="BlockType"/> is in this
    /// list; a test walks them by reflection.
    /// </summary>
    internal static void Allocate(int idCount)
    {
        if (idCount < 1 || idCount > ushort.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(idCount), idCount,
                $"a block or item id table holds 1..{ushort.MaxValue + 1} entries");

        BlockType.ById = new BlockType?[idCount];
        BlockType.Emission = new byte[idCount];
        BlockType.LightSlot = new byte[idCount];
        BlockType.PassesLightById = new bool[idCount];
        BlockType.SolidById = new bool[idCount];
        BlockType.CollidesByHitboxById = new bool[idCount];
        BlockType.OccludesById = new bool[idCount];
        BlockType.CastsAoById = new bool[idCount];
        BlockType.StackSolidStateById = new byte[idCount];
        BlockType.MovementSpeedById = new float[idCount];
        BlockType.FrictionById = new float[idCount];
        Array.Fill(BlockType.FrictionById, BlockType.DefaultFriction);
        BlockType.PebbleWeightById = new float[idCount];
        BlockType.FootstepDustById = new bool[idCount];
        BlockType.BounceById = new bool[idCount];
        BlockType.SoftLandingById = new bool[idCount];
        BlockType.SinkById = new float[idCount];
        BlockType.BlocksSightById = new bool[idCount];
        BlockType.ContactDamageById = new float[idCount];
        BlockType.TreeWoodById = new bool[idCount];
        BlockType.FullCubeById = new bool[idCount];
        BlockType.PixelRimById = new bool[idCount];
        BlockType.PaneById = new bool[idCount];
        BlockType.PaneProfileById = new PaneProfile?[idCount];
        BlockType.GreedyById = new bool[idCount];
        BlockType.FluidById = new bool[idCount];
        BlockType.ReplaceableById = new bool[idCount];
        BlockType.ReplaceableByPlacementById = new bool[idCount];
        BlockType.FluidTickDivisorById = new byte[idCount];
        BlockType.FluidRangeById = new byte[idCount];
        BlockType.FluidInfiniteById = new bool[idCount];
        BlockType.HardensToById = new ushort[idCount];
        BlockType.HardensTouchingById = new ushort[idCount];
        BlockType.FlammableById = new bool[idCount];
        BlockType.FreezesToById = new ushort[idCount];
        BlockType.MeltsToById = new ushort[idCount];
        BlockType.UnbreakableById = new bool[idCount];
        BlockType.CoversSkyById = new bool[idCount];
        BlockType.FallsById = new bool[idCount];

        BlockType.Types = new Dictionary<ushort, BlockType>();
        BlockType.SkyCoverChars = SearchValues.Create(ReadOnlySpan<char>.Empty);

        // Air owns no definition but still has to let light through and stop nothing
        BlockType.PassesLightById[BlockType.Air] = true;
        BlockType.ReplaceableById[BlockType.Air] = true;
        BlockType.FluidTickDivisorById[BlockType.Air] = 1;
        BlockType.FluidRangeById[BlockType.Air] = 7;
        BlockType.FluidInfiniteById[BlockType.Air] = true;
    }
}
