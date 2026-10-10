using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A headless server-mode <see cref="World"/> over a flat stone plateau, for edit tests that need
/// the real placement and break paths (support pops, wall mounting) without waiting on worldgen.
/// One connected survival player stands on the plateau and acts through its own
/// <see cref="PlayerContext"/>, exactly as a server replays a client's intents; <see cref="Load"/>
/// streams the chunks a test edits in synchronously. The world is never updated, so nothing
/// unloads, ticks or spawns underneath a test.
/// </summary>
internal sealed class SupportTestWorld : IDisposable
{
    // How far above worldgen's tallest column the plateau stands, so no cave or tree reaches it
    private const int PlateauLift = 24;

    public SupportTestWorld()
    {
        Stone = ContentRegistry.RequireBlock("stone").Id;
        var tallest = int.MinValue;
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
            tallest = Math.Max(tallest, ColumnCache.Get(cx, cz).MaxHeight);
        Top = tallest + PlateauLift;

        var source = new FlatWorldSource(-1, 1, -1, 1, Top, Stone, Biome.Desert, warmth: 0.95f, wetness: 0.05f);
        var eye = new Vector3(8.5f, Top + 1 + Camera.EyeHeight, 8.5f);
        World = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true,
            spawn: eye, worldSource: source);
        Manager = World.GetChunkManager();
        Player = new ServerPlayer(1, "Builder", eye);
        World.ServerPlayers = [Player];
    }

    public World World { get; }
    public ChunkManager Manager { get; }
    public ServerPlayer Player { get; }

    /// <summary>World y of the plateau's top cell; every cell above it starts as air.</summary>
    public int Top { get; }

    public byte Stone { get; }

    /// <summary>Streams in the chunk holding the cell and its six face neighbours.</summary>
    public void Load(int x, int y, int z)
    {
        var centre = VoxelCoords.ChunkPosOf(x, y, z);
        Manager.QueueChunk(centre);
        foreach (var offset in Vector3Directions.CardinalDirections)
            Manager.QueueChunk(centre + offset);

        // A generation worker may already be building one of them, which QueueChunk then skips
        var sw = Stopwatch.StartNew();
        while (!AllLoaded() && sw.Elapsed.TotalSeconds < 20)
            Thread.Sleep(1);
        Assert.True(AllLoaded(), $"the chunks around ({x}, {y}, {z}) never loaded");

        bool AllLoaded()
        {
            if (Manager.ChunkAt(x, y, z) == null) return false;
            foreach (var offset in Vector3Directions.CardinalDirections)
            {
                var p = (centre + offset) * Chunk.Size;
                if (Manager.ChunkAt((int)p.X, (int)p.Y, (int)p.Z) == null) return false;
            }
            return true;
        }
    }

    /// <summary>Writes one cell raw, as a test's setup.</summary>
    public void Set(int x, int y, int z, byte id, byte state = 0)
    {
        var chunk = Manager.ChunkAt(x, y, z) ?? throw new InvalidOperationException($"({x}, {y}, {z}) is not loaded");
        var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, z);
        chunk.SetBlockRaw(lx, ly, lz, id, state);
    }

    public byte IdAt(int x, int y, int z) => Manager.GetBlockGlobal(x, y, z);

    public byte StateAt(int x, int y, int z) => Manager.GetStateGlobal(x, y, z);

    /// <summary>Gives the player one <paramref name="id"/>, puts it in hand and places it against the
    /// face of <paramref name="hitCell"/> with outward normal <paramref name="normal"/>.</summary>
    public bool Place(byte id, Vector3 hitCell, Vector3 normal)
    {
        Player.Inventory.Add(id);
        Assert.True(Player.Inventory.PickBlock(id));
        return World.TryPlaceSelectedBlockAt(Player.Context, hitCell, normal);
    }

    /// <summary>Breaks the cell for the player, with everything a break sets off.</summary>
    public bool Break(int x, int y, int z) => World.BreakBlockFor(Player.Context, new Vector3(x, y, z));

    public void Dispose() => World.SaveAndDispose();
}
