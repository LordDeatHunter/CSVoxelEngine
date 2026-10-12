using System;
using System.Collections;
using System.Collections.Generic;

namespace VoxelEngine.Particles;

/// <summary>
/// A fixed-capacity, order-preserving particle store: a circular buffer that behaves exactly like
/// the <see cref="List{T}"/> it replaces in LeafParticleSystem, BubbleParticleSystem,
/// FireParticleSystem and BlockDebrisSystem (PERF_TODO C12(a)), except that evicting the oldest
/// entry - <c>RemoveAt(0)</c>, which each of those systems does on every spawn once it is at its
/// cap - is O(1) instead of an O(n) shift over up to 512 entries.
///
/// <para><b>Why this type exists at all: the draw-order argument.</b> The four systems are consumed
/// only as <see cref="IReadOnlyList{T}"/>, so "cheaper eviction" is only admissible if the sequence
/// a consumer observes is unchanged. Three of the four consumers do not care: LeafParticleRenderer,
/// BubbleParticleRenderer and FireParticleRenderer each re-derive their own back-to-front order
/// every frame (<c>Array.Sort</c> over a depth array, then indexed access through that permutation -
/// LeafParticleRenderer.cs:122-127, BubbleParticleRenderer.cs:92-99, FireParticleRenderer.cs:85-93),
/// so source order reaches the GPU through a depth sort and is discarded. The fourth does care:
/// <c>RenderManager.RenderBlockDebris</c> (RenderManager.cs:2114) walks its list with a plain
/// <c>foreach</c> and submits each cube in exactly that order.</para>
///
/// <para><b>The debris blend state.</b> Debris always draws with depth WRITES ON - the leaf
/// renderer clears its own <c>DepthMask(false)</c> by restoring <c>DepthMask(true)</c> before it
/// returns (LeafParticleRenderer.cs:174) - and RenderBlockDebris sets no blend state of its own.
/// <c>Blend</c> is already ENABLED at <c>SrcAlpha</c>/<c>OneMinusSrcAlpha</c> by
/// <c>RenderManager.BeginFrame</c> and left that way by the opaque passes, so debris composites
/// regardless of whether any leaves are alive; the leaf pass no longer runs just before debris in
/// any case, since it draws after the cloud pass. So overlapping semi-transparent debris cubes
/// composite in submission order, and submission order is visually load-bearing.</para>
///
/// <para><b>What that buys.</b> Because indexing and enumeration here are OLDEST-FIRST, this type
/// reproduces <see cref="List{T}"/>'s sequence element for element under the same operations, so
/// every renderer keeps its existing behavior with ZERO renderer changes. That is a stronger
/// guarantee than the "head index plus a modular walk in the renderers" PERF_TODO's C12(a) bullet
/// proposed: a modular walk would have to be repeated correctly at each of the four call sites and
/// would be one edit away from silently re-sorting the debris pass, whereas an
/// <see cref="IReadOnlyList{T}"/>-faithful container cannot reorder anything by construction.
/// ParticleRingTests.RingMatchesAListUnderTheSameOperations is the fact that pins the equivalence,
/// differentially against a <see cref="List{T}"/> oracle.</para>
///
/// <para><b>Why every removal path preserves order.</b> Eviction (index 0) advances the head; a tail
/// removal drops the last slot; an interior removal shifts the SHORTER of the two sides by one
/// (front block up, head advancing - or back block down). All three move a contiguous run one slot
/// while leaving its internal sequence intact, so the relative order of every surviving element is
/// unchanged. Shifting the shorter side is never worse than <see cref="List{T}"/>.RemoveAt, which
/// always shifts the tail, and the interior case is the one the four Update reap loops take as they
/// walk backwards dropping dead particles.</para>
///
/// <para><b>Capacity.</b> Each system constructs the ring with its own Max, so in production the
/// buffer never grows - the systems evict at their cap and the array stays exactly that size. The
/// doubling path exists so the type is correct rather than merely correct-in-context, and it copies
/// in LOGICAL order and resets the head to 0, which is also what makes the post-growth sequence
/// checkable (ParticleRingTests.GrowthPreservesOrderAcrossTheWrap).</para>
///
/// <para><b>Vacated slots are cleared.</b> Every removal writes <c>default!</c> into the slot it
/// releases, so a removed particle is not kept alive by a stale reference in the backing array -
/// the four element types are classes, so without this a 512-entry ring would pin up to 512 dead
/// particles indefinitely. <see cref="List{T}"/> does the same thing for reference types, so this
/// is equivalence, not extra caution.</para>
/// </summary>
internal sealed class ParticleRing<T> : IReadOnlyList<T>
{
    private T[] _items;
    private int _head;
    private int _count;

    /// <summary>
    /// Creates a ring backed by <paramref name="capacity"/> slots (at least one, so the modular
    /// arithmetic always has a non-zero divisor). Callers pass the system's own particle cap, which
    /// is why the growth path is unreachable in production.
    /// </summary>
    internal ParticleRing(int capacity) => _items = new T[Math.Max(1, capacity)];

    public int Count => _count;

    /// <summary>
    /// The <paramref name="index"/>-th oldest live entry. Logical, not physical: index 0 is always
    /// the head, whatever slot the head currently occupies.
    /// </summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length];
        }
    }

    /// <summary>
    /// Appends <paramref name="item"/> as the newest entry, growing (and de-wrapping) the buffer
    /// first if the ring is full. Production callers evict before adding at their cap, so the growth
    /// branch is not taken in the game.
    /// </summary>
    public void Add(T item)
    {
        var cap = _items.Length;
        if (_count == cap)
        {
            // Copy in logical order so the grown buffer starts unwrapped at head 0.
            var grown = new T[cap * 2];
            for (var i = 0; i < _count; i++) grown[i] = _items[(_head + i) % cap];
            _items = grown;
            _head = 0;
            cap = grown.Length;
        }

        _items[(_head + _count) % cap] = item;
        _count++;
    }

    /// <summary>
    /// Removes the entry at logical <paramref name="index"/>, preserving the relative order of every
    /// survivor (see the type doc). Index 0 - the eviction C12(a) exists for - is O(1); an interior
    /// removal shifts whichever side is shorter.
    /// </summary>
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));

        var cap = _items.Length;

        if (index == 0)
        {
            _items[_head] = default!;
            _head = _head + 1 == cap ? 0 : _head + 1;
            _count--;
            return;
        }

        if (index == _count - 1)
        {
            _items[(_head + index) % cap] = default!;
            _count--;
            return;
        }

        var behind = index;                 // entries before the hole
        var ahead = _count - 1 - index;     // entries after it

        if (behind < ahead)
        {
            // Slide the front block one slot later, then drop the old head slot.
            for (var i = index; i > 0; i--)
                _items[(_head + i) % cap] = _items[(_head + i - 1) % cap];
            _items[_head] = default!;
            _head = _head + 1 == cap ? 0 : _head + 1;
        }
        else
        {
            // Slide the back block one slot earlier, then drop the old tail slot.
            for (var i = index; i < _count - 1; i++)
                _items[(_head + i) % cap] = _items[(_head + i + 1) % cap];
            _items[(_head + _count - 1) % cap] = default!;
        }

        _count--;
    }

    /// <summary>
    /// Yields the live entries oldest-first, which is the sequence <see cref="List{T}"/> would have
    /// produced under the same operations. A plain iterator is enough: every consumer reaches this
    /// through <see cref="IReadOnlyList{T}"/>, where <see cref="List{T}"/>'s struct enumerator is
    /// boxed just the same, so allocation per enumeration is unchanged.
    /// </summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return _items[(_head + i) % _items.Length];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
