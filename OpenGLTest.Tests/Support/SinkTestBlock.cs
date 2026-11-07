using System;
using VoxelEngine.Player;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A test-only sink block (a stand-in for the cloud block plans/LAYERS.md adds later), so the sink
/// and soft-landing rules can be driven through the real physics, collision and server probes
/// without shipping throwaway content. It lives on an id no content uses and only fills the flat
/// per-id tables those rules read: solid, soft landing and a full <see cref="SinkLayer.MaxSink"/>.
/// No other test ever writes this id into a chunk, so nothing else sees it. Idempotent and
/// thread-safe, like <see cref="TestContent.EnsureLoaded"/>.
/// </summary>
internal static class SinkTestBlock
{
    private const byte Id = 255;
    private static readonly object Gate = new();

    /// <summary>Registers the block once and returns its id.</summary>
    internal static byte Ensure()
    {
        TestContent.EnsureLoaded();
        lock (Gate)
        {
            if (BlockType.SinkById[Id] > 0f) return Id;
            if (BlockType.ById[Id] != null)
                throw new InvalidOperationException($"Block id {Id} is taken by content; move the test sink block.");
            BlockType.SolidById[Id] = true;
            BlockType.SoftLandingById[Id] = true;
            BlockType.SinkById[Id] = SinkLayer.MaxSink;
            return Id;
        }
    }
}
