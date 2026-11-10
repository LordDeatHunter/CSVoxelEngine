using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Content.Blocks;

/// <summary>
/// The registry, the mesher and the chunk codecs past the byte range: a content set with 300
/// extra blocks (<see cref="SyntheticContentRoot"/>) is swapped into the process-global registry,
/// so ids above 255 exist. Each test restores the real content in a <c>finally</c> and asserts the
/// restore was exact. Only the registry, meshing and the codecs run under the swap, never
/// worldgen, so no static worldgen cache is built from the synthetic ids.
/// </summary>
[Collection("Content registry swap")]
public sealed class WideIdRegistryTests
{
    private const int SyntheticBlocks = 300;

    [Fact]
    public void RegistryPastTheByteRangeResolvesEveryId()
    {
        UnderSyntheticContent(root =>
        {
            Assert.True(BlockTables.IdCount > byte.MaxValue + 1, $"only {BlockTables.IdCount} ids");

            var ids = SyntheticIds(root);
            for (var i = 0; i < root.Count; i++)
            {
                var name = SyntheticContentRoot.NameOf(i);
                var type = BlockType.ById[ids[i]];
                Assert.NotNull(type);
                Assert.Equal(name, type.Name);
                Assert.Equal(SyntheticContentRoot.DisplayNameOf(i), type.DisplayName);
            }
            Assert.Equal(root.Count, ids.Distinct().Count());
            Assert.Contains(ids, id => id > byte.MaxValue);

            Assert.Equal(SyntheticContentRoot.EmitterLevel, BlockType.Emission[ids[root.EmitterIndex]]);
            for (var i = 0; i < root.Count; i++)
            {
                if (i == root.EmitterIndex) continue;
                var id = ids[i];
                Assert.True(BlockType.SolidById[id], SyntheticContentRoot.NameOf(i));
                Assert.True(BlockType.OccludesById[id], SyntheticContentRoot.NameOf(i));
                Assert.True(BlockType.GreedyById[id], SyntheticContentRoot.NameOf(i));
            }

            // Pure items take the ids after every block, so they are all past the byte range too
            Assert.NotEmpty(ContentRegistry.ItemIds);
            foreach (var (name, id) in ContentRegistry.ItemIds)
            {
                Assert.True(ContentRegistry.TryResolveItem(name, out var resolved), name);
                Assert.Equal(id, resolved);
                Assert.True(BlockTables.IsAssignedId(id), name);
                Assert.Null(BlockType.ById[id]);
                Assert.Equal(id, ItemRegistry.TryGet(id)?.Id);
            }
            Assert.Contains(ContentRegistry.ItemIds.Values, id => id > byte.MaxValue);
        });
    }

    [Fact]
    public void AHighIdMeshesSavesAndStreams()
    {
        UnderSyntheticContent(root =>
        {
            // Two plain blocks whose ids share their low byte: a key or a cell that kept only
            // a byte would see one block
            var plain = SyntheticIds(root).Where((_, i) => i != root.EmitterIndex).ToArray();
            var a = plain[0];
            var b = plain.First(id => id != a && (id & 0xFF) == (a & 0xFF));

            // The greedy pass keeps the two apart; the per-quad path, which never merges, draws
            // both rows alike
            Assert.True(RowVertices(a, b, greedy: true) > RowVertices(a, a, greedy: true),
                "two different ids merged into one quad");
            Assert.Equal(RowVertices(a, a, greedy: false), RowVertices(a, b, greedy: false));

            // A chunk holding the row and every synthetic id, more distinct ids than a byte index
            var chunk = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
            chunk.SetBlockRaw(5, 8, 5, a, 0);
            chunk.SetBlockRaw(6, 8, 5, b, 0);
            var all = SyntheticIds(root);
            for (var i = 0; i < all.Length; i++)
                chunk.SetBlockRaw(i % Chunk.Size, 10 + i / (Chunk.Size * Chunk.Size), i / Chunk.Size % Chunk.Size, all[i], 0);
            var snapshot = chunk.TakeSnapshot(null);
            Assert.True(snapshot.Blocks.Distinct().Count() > byte.MaxValue + 1);

            using var file = new MemoryStream();
            ChunkSaveCodec.Write(file, snapshot, static id => BlockType.ById[id]?.Name);
            file.Position = 0;
            var saved = ChunkSaveCodec.Read(file,
                static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);
            Assert.Equal(snapshot.Blocks, saved.Blocks);
            Assert.Equal(snapshot.State, saved.State);

            var streamed = ChunkNetCodec.Decode(ChunkNetCodec.Encode(snapshot));
            Assert.Equal(snapshot.Blocks, streamed.Blocks);
            Assert.Equal(snapshot.State, streamed.State);
        });
    }

    /// <summary>
    /// The frame every test runs in: snapshot the real registry, swap the synthetic content in,
    /// run <paramref name="assertions"/>, then restore the real content and assert the restore
    /// is exact, so no other test ever sees the swapped registry.
    /// </summary>
    private static void UnderSyntheticContent(Action<SyntheticContentRoot> assertions)
    {
        TestContent.EnsureLoaded();
        // Mesh once under the real content first, so every static the mesher touches is built
        // from the real registry rather than the swapped one
        var stone = ContentRegistry.RequireBlock("stone").Id;
        RowVertices(stone, stone, greedy: true);

        var real = new RegistrySnapshot();
        using var root = new SyntheticContentRoot(SyntheticBlocks);
        try
        {
            TestContent.SwapIn(root.Root);
            assertions(root);
        }
        finally
        {
            TestContent.RestoreReal();
            real.AssertMatches(new RegistrySnapshot());
        }
    }

    /// <summary>The id each synthetic block resolved to, by index.</summary>
    private static ushort[] SyntheticIds(SyntheticContentRoot root)
    {
        var ids = new ushort[root.Count];
        for (var i = 0; i < root.Count; i++)
        {
            var name = SyntheticContentRoot.NameOf(i);
            Assert.True(ContentRegistry.TryResolveBlock(name, out ids[i]), $"{name} did not resolve");
        }
        return ids;
    }

    /// <summary>Vertices of a two-cell row of <paramref name="first"/> then <paramref name="second"/>, alone in the sky.</summary>
    private static int RowVertices(ushort first, ushort second, bool greedy)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(5, 8, 5, first, 0);
        center.SetBlockRaw(6, 8, 5, second, 0);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        return mesh.Vertices.Count;
    }

    /// <summary>The registry state a swap must leave exactly as it found it.</summary>
    private sealed class RegistrySnapshot
    {
        private readonly (string, ushort)[] _blocks = Sorted(ContentRegistry.BlockIds);
        private readonly (string, ushort)[] _items = Sorted(ContentRegistry.ItemIds);
        private readonly int _idCount = BlockTables.IdCount;
        private readonly (string, int)[] _tableLengths = TableLengths();
        private readonly string?[] _names = [.. BlockType.ById.Select(t => t?.Name)];
        private readonly bool[] _occludes = [.. BlockType.OccludesById];
        private readonly bool[] _fluid = [.. BlockType.FluidById];
        private readonly byte[] _emission = [.. BlockType.Emission];

        internal void AssertMatches(RegistrySnapshot restored)
        {
            Assert.Equal(_blocks, restored._blocks);
            Assert.Equal(_items, restored._items);
            Assert.Equal(_idCount, restored._idCount);
            Assert.Equal(_tableLengths, restored._tableLengths);
            Assert.Equal(_names, restored._names);
            Assert.Equal(_occludes, restored._occludes);
            Assert.Equal(_fluid, restored._fluid);
            Assert.Equal(_emission, restored._emission);
        }

        private static (string, ushort)[] Sorted(IEnumerable<KeyValuePair<string, ushort>> ids) =>
            [.. ids.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value))];

        private static (string, int)[] TableLengths()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            return
            [
                .. typeof(BlockType).GetFields(flags)
                    .Where(f => f.FieldType.IsArray)
                    .Select(f => (f.Name, ((Array)f.GetValue(null)!).Length))
                    .Concat(typeof(BlockType).GetProperties(flags)
                        .Where(p => p.PropertyType.IsArray)
                        .Select(p => (p.Name, ((Array)p.GetValue(null)!).Length)))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
            ];
        }
    }
}
