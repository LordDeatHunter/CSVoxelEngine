using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// One entry of a <c>glMultiDrawElementsIndirect</c> command buffer, laid out exactly as the GL
/// 4.6 spec (§10.4, "DrawElementsIndirectCommand") requires: five 32-bit words in this order and
/// nothing else, since the driver reads the buffer memory directly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DrawElementsIndirectCommand
{
    /// <summary>Indices to draw, i.e. <see cref="GeometrySlice.IndexCount"/>.</summary>
    public uint Count;

    /// <summary>Instances; always 1 here — chunks are drawn once each.</summary>
    public uint InstanceCount;

    /// <summary>First index into the pool's element buffer, i.e. <see cref="GeometrySlice.IndexOffset"/>.</summary>
    public uint FirstIndex;

    /// <summary>
    /// Added to every index, i.e. <see cref="GeometrySlice.VertexOffset"/> — this is what lets a
    /// slice keep its indices slice-relative <c>ushort</c>s however far into the pool it sits.
    /// </summary>
    public int BaseVertex;

    /// <summary>First instance; always 0 (nothing here is instanced).</summary>
    public uint BaseInstance;
}

/// <summary>
/// The per-frame command stream behind the pooled chunk draws: each pass (shadow, reflection,
/// opaque front-to-back, translucent back-to-front) appends one
/// <see cref="DrawElementsIndirectCommand"/> plus one <c>vec4(origin, alpha)</c> per visible chunk
/// and then issues a single <c>glMultiDrawElementsIndirect</c>. The CPU-side culling and sorting
/// are unchanged; what disappears is the per-chunk uniform upload and draw call.
///
/// Both buffers are one immutable <c>glBufferStorage</c> allocation holding <see cref="Slots"/>
/// frames' worth of entries, persistently and coherently mapped, so a frame writes straight into
/// GPU-visible memory with no map/unmap and no orphaning. The frames rotate: frame N writes slot
/// N % 3 while the GPU may still be reading slots N-1 and N-2.
///
/// The rotation alone is not a guarantee, though — VSync is off, so the CPU can run arbitrarily
/// far ahead, and a GPU more than two frames behind would find the slot it is reading rewritten
/// underneath it (silently corrupted draws, not an error). Each slot therefore carries a fence:
/// <see cref="BeginFrame"/> signals one for the slot just finished and waits on the fence of the
/// slot it is about to hand out. At rest the wait returns already-signalled immediately.
///
/// Passes share one slot: each pass occupies a contiguous run, so the run's start goes to the
/// shader as <c>uDrawBase</c> and the vertex stage reads <c>draws[uDrawBase + gl_DrawID]</c>.
/// (Binding just the pass's range with <c>glBindBufferRange</c> would work too, but would have to
/// respect <c>GL_SHADER_STORAGE_BUFFER_OFFSET_ALIGNMENT</c>; one <c>glUniform1i</c> does not.)
///
/// Render thread only.
/// </summary>
public sealed unsafe class ChunkDrawBatch : IDisposable
{
    /// <summary>Frames in flight the ring covers; 3 is the usual driver queue depth.</summary>
    public const int Slots = 3;

    /// <summary>
    /// Commands one frame can hold before the ring has to grow. Renderable chunk counts are three
    /// orders of magnitude below this, so growth is a safety net rather than a code path.
    /// </summary>
    public const int InitialCommandsPerSlot = 32768;

    /// <summary>Binding point of the ChunkInfo SSBO; nothing else in the engine binds one.</summary>
    public const uint SsboBinding = 0;

    /// <summary>Bytes per indirect command — the spec layout, not whatever the CLR might pick.</summary>
    private const int CommandBytes = 20;

    /// <summary>Bytes per SSBO entry: one <c>vec4</c> of origin.xyz plus fade alpha in .w.</summary>
    private const int DrawBytes = 16;

    private const ulong FenceTimeoutNs = 1_000_000_000;

    private static bool _fenceWarned;

    private readonly GL _gl;
    private readonly nint[] _fences = new nint[Slots];

    private uint _indirect, _ssbo;
    private DrawElementsIndirectCommand* _cmds;
    private Vector4* _draws;
    private int _capacityPerSlot;
    private int _slot;
    private int _cursor;
    private int _passStart;
    private int _passCount;
    private bool _disposed;

    static ChunkDrawBatch()
    {
        // The driver reads these buffers as raw memory, so a layout surprise would be a silent
        // garbage-geometry bug rather than an exception. Check once, at startup.
        if (sizeof(DrawElementsIndirectCommand) != CommandBytes)
            throw new InvalidOperationException(
                $"DrawElementsIndirectCommand must be {CommandBytes} bytes, got {sizeof(DrawElementsIndirectCommand)}.");

        if (sizeof(Vector4) != DrawBytes)
            throw new InvalidOperationException($"Vector4 must be {DrawBytes} bytes, got {sizeof(Vector4)}.");
    }

    /// <param name="gl">The render thread's GL context.</param>
    public ChunkDrawBatch(GL gl)
    {
        _gl = gl;
        Allocate(InitialCommandsPerSlot);
    }

    /// <summary>
    /// Rotates to the next slot at the top of a frame: fences the slot the previous frame drew
    /// from, then waits until the GPU is finished with the slot about to be overwritten.
    /// </summary>
    public void BeginFrame()
    {
        // Everything that reads the current slot has been submitted by now (this runs before any
        // of the frame's draws), so a fence here means exactly "the GPU is done with this slot".
        _fences[_slot] = _gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);

        _slot = (_slot + 1) % Slots;

        if (_fences[_slot] != 0)
        {
            // SyncObjectMask.Bit is Silk's current name for GL_SYNC_FLUSH_COMMANDS_BIT (0x1); the
            // spelled-out alias is deprecated. It flushes, so a wait cannot deadlock on unsubmitted work.
            var result = _gl.ClientWaitSync(_fences[_slot], SyncObjectMask.Bit, FenceTimeoutNs);
            if (result is GLEnum.TimeoutExpired or GLEnum.WaitFailed && !_fenceWarned)
            {
                _fenceWarned = true;
                Log.WriteLine($"ChunkDrawBatch: fence wait returned {result}; chunk draws may tear this frame.");
            }

            _gl.DeleteSync(_fences[_slot]);
            _fences[_slot] = 0;
        }

        _cursor = 0;
    }

    /// <summary>
    /// Starts a pass's contiguous run in the current slot and reserves room for it.
    /// </summary>
    /// <param name="maxCommands">
    /// Upper bound on the <see cref="Add"/> calls that follow — the culled chunk count. Exceeding
    /// it is what <see cref="Add"/>'s guard catches.
    /// </param>
    public void BeginPass(int maxCommands)
    {
        if (_cursor + maxCommands > _capacityPerSlot)
            Grow(Math.Max(_capacityPerSlot * 2, _cursor + maxCommands));

        _passStart = _cursor;
        _passCount = 0;
    }

    /// <summary>
    /// Appends one chunk to the current pass. <paramref name="origin"/> is the chunk's world-space
    /// corner (chunk model matrices are pure translations, so an origin replaces the model matrix)
    /// and <paramref name="alpha"/> its stream-in fade; together they are the <c>vec4</c> the
    /// vertex stage reads at <c>uDrawBase + gl_DrawID</c>.
    /// </summary>
    public void Add(in GeometrySlice slice, Vector3 origin, float alpha)
    {
        if (_cursor >= _capacityPerSlot)
            throw new InvalidOperationException(
                $"ChunkDrawBatch slot is full at {_cursor} commands; BeginPass under-reserved.");

        var i = _slot * _capacityPerSlot + _cursor;

        _cmds[i] = new DrawElementsIndirectCommand
        {
            Count = (uint)slice.IndexCount,
            InstanceCount = 1,
            FirstIndex = (uint)slice.IndexOffset,
            BaseVertex = slice.VertexOffset,
            BaseInstance = 0
        };
        _draws[i] = new Vector4(origin, alpha);

        _cursor++;
        _passCount++;
    }

    /// <summary>
    /// Issues the current pass as one multi-draw. The caller must already have
    /// <paramref name="shader"/> (a <c>CHUNK_DRAW</c> variant) in use, since <c>uDrawBase</c> is
    /// set on the bound program. Commands execute in array order, so the order chunks were added
    /// in — front-to-back or back-to-front — is the order they are drawn in.
    /// </summary>
    /// <param name="vao">The geometry pool's VAO (opaque or translucent).</param>
    /// <param name="shader">The bound chunk program.</param>
    /// <param name="locDrawBase">Cached location of that program's <c>uDrawBase</c>.</param>
    public void Draw(uint vao, Shader shader, int locDrawBase)
    {
        if (_passCount == 0) return;

        // One index addresses both the SSBO (through uDrawBase) and the indirect buffer (through
        // the byte offset below); the two must never drift apart.
        var first = _slot * _capacityPerSlot + _passStart;
        shader.SetUniform(locDrawBase, first);

        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _indirect);
        // Coherent mapping: the writes above are visible to the GPU without a flush of our own.
        _gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedShort,
            (void*)(nint)((long)first * CommandBytes), (uint)_passCount, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Allocates and persistently maps both ring buffers for <paramref name="perSlot"/> commands
    /// per slot, and binds the SSBO to <see cref="SsboBinding"/> — a plain
    /// <c>glBindBufferBase</c> of the whole buffer, so no offset-alignment rules apply.
    /// </summary>
    private void Allocate(int perSlot)
    {
        var total = (long)perSlot * Slots;

        const BufferStorageMask storageFlags = BufferStorageMask.MapWriteBit |
                                               BufferStorageMask.MapPersistentBit |
                                               BufferStorageMask.MapCoherentBit;
        // Same three bits as storageFlags; MapBufferAccessMask spells them without the "Map"
        // prefix (the prefixed aliases are deprecated in Silk.NET).
        const MapBufferAccessMask mapFlags = MapBufferAccessMask.WriteBit |
                                             MapBufferAccessMask.PersistentBit |
                                             MapBufferAccessMask.CoherentBit;

        var commandBytes = (nuint)(total * CommandBytes);
        _indirect = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _indirect);
        _gl.BufferStorage(BufferStorageTarget.DrawIndirectBuffer, commandBytes, null, storageFlags);
        _cmds = (DrawElementsIndirectCommand*)_gl.MapBufferRange(
            BufferTargetARB.DrawIndirectBuffer, 0, commandBytes, mapFlags);
        if (_cmds == null)
            throw new InvalidOperationException("Failed to persistently map the chunk indirect command buffer.");

        var drawBytes = (nuint)(total * DrawBytes);
        _ssbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssbo);
        _gl.BufferStorage(BufferStorageTarget.ShaderStorageBuffer, drawBytes, null, storageFlags);
        _draws = (Vector4*)_gl.MapBufferRange(BufferTargetARB.ShaderStorageBuffer, 0, drawBytes, mapFlags);
        if (_draws == null)
            throw new InvalidOperationException("Failed to persistently map the chunk info SSBO.");

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SsboBinding, _ssbo);

        _capacityPerSlot = perSlot;
    }

    /// <summary>
    /// Reallocates the ring larger, keeping the commands this frame has already written. Only
    /// reachable if one pass ever exceeds <see cref="InitialCommandsPerSlot"/> draws, and it
    /// stalls the pipeline outright (<c>glFinish</c>): the older slots may still be in flight and
    /// their storage is about to be deleted.
    /// </summary>
    private void Grow(int perSlot)
    {
        _gl.Finish();

        var keptCommands = new DrawElementsIndirectCommand[_cursor];
        var keptDraws = new Vector4[_cursor];
        var oldBase = _slot * _capacityPerSlot;
        for (var i = 0; i < _cursor; i++)
        {
            keptCommands[i] = _cmds[oldBase + i];
            keptDraws[i] = _draws[oldBase + i];
        }

        Release();
        Allocate(perSlot);

        var newBase = _slot * perSlot;
        for (var i = 0; i < keptCommands.Length; i++)
        {
            _cmds[newBase + i] = keptCommands[i];
            _draws[newBase + i] = keptDraws[i];
        }

        Log.WriteLine($"Chunk draw ring grew to {perSlot} commands per slot ({Slots} slots)");
    }

    /// <summary>Unmaps and deletes both buffers and drops every outstanding fence.</summary>
    private void Release()
    {
        if (_cmds != null)
        {
            _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _indirect);
            _gl.UnmapBuffer(BufferTargetARB.DrawIndirectBuffer);
            _cmds = null;
        }

        if (_draws != null)
        {
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssbo);
            _gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            _draws = null;
        }

        _gl.DeleteBuffer(_indirect);
        _gl.DeleteBuffer(_ssbo);
        _indirect = 0;
        _ssbo = 0;

        for (var i = 0; i < Slots; i++)
        {
            if (_fences[i] == 0) continue;

            _gl.DeleteSync(_fences[i]);
            _fences[i] = 0;
        }
    }
}
