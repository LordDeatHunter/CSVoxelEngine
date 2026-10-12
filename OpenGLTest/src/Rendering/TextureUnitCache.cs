namespace silkgltriangle;

/// <summary>
/// The GL-free bookkeeping behind the render-state rebind cache (PERF_TODO R5): which texture unit
/// the context has selected, and what is bound to each of the tracked units. It issues no GL call
/// and holds no GL handle - the caller does that, and only when one of the <c>Mark*</c> methods says
/// the call is actually needed. The split exists for the same reason <c>ReflectionReuse</c> and
/// <c>HalfResTarget</c> are separate types: <c>RenderManager</c> needs a live GL context to
/// construct, so the only way to pin this arithmetic headlessly is to keep it out here. The GL half
/// - that a skipped <c>glBindTexture</c> really did leave the right texture bound - is not covered
/// by tests at all and is only reachable by running the game.
///
/// <para>Semantics worth stating, because the caller's correctness rests on them:</para>
/// <list type="bullet">
/// <item><see cref="MarkActive"/> records the unit as selected whether or not it tells the caller to
/// issue <c>glActiveTexture</c>. Skipping a redundant select leaves the same unit selected as
/// issuing it would have, so <see cref="ActiveUnit"/> is truthful either way. <c>BindUnit</c>'s
/// eager select relies on exactly this.</item>
/// <item>A bind of a <em>different target</em> on a unit replaces that unit's whole record rather
/// than keeping one slot per target. That is conservative, not wrong: the next request for the
/// displaced pair misses and rebinds. GL does keep a separate binding per target per unit, but the
/// engine only ever samples one target per unit, so a per-target table would buy nothing and could
/// only ever turn a needless rebind into a missed one.</item>
/// <item>Only units <c>[0, <see cref="TrackedUnits"/>)</c> are tracked. Anything above always
/// reports "you must bind" and records nothing, so a pass that reaches for a higher unit (the
/// underwater rays use unit 5) can never produce a stale hit here.</item>
/// <item>After <see cref="Invalidate"/> nothing is known: the first <see cref="MarkActive"/> of any
/// unit reports a needed call and <see cref="Matches"/> is false for every unit.</item>
/// </list>
/// </summary>
internal sealed class TextureUnitCache
{
    /// <summary>Units 0-4: the world-texture set (atlas, shadow map, reflection, scene color and
    /// scene depth). Higher units are deliberately untracked - see the class remarks.</summary>
    internal const int TrackedUnits = 5;

    private const int UnknownUnit = -1;

    // -1 in Targets means "nothing known about this unit"; a recorded unbind (texture 0) is a real
    // record, because rebinding 0 over 0 is just as redundant as rebinding a texture over itself.
    private readonly int[] _targets = new int[TrackedUnits];
    private readonly uint[] _textures = new uint[TrackedUnits];

    internal TextureUnitCache() => Invalidate();

    /// <summary>The unit the cache believes <c>glActiveTexture</c> has selected, or -1 when
    /// unknown (a fresh cache, or one that has just been invalidated).</summary>
    internal int ActiveUnit { get; private set; } = UnknownUnit;

    /// <summary>
    /// Records <paramref name="unit"/> as the selected one and returns true when the caller must
    /// actually issue <c>glActiveTexture</c>. The record is updated either way, because a skipped
    /// select leaves the same unit selected as an issued one would have.
    /// </summary>
    internal bool MarkActive(int unit)
    {
        if (ActiveUnit == unit) return false;
        ActiveUnit = unit;
        return true;
    }

    /// <summary>
    /// A pure query: is <paramref name="texture"/> already bound to <paramref name="unit"/> on
    /// <paramref name="target"/>? Records nothing, changes nothing, and is always false for an
    /// untracked unit. Calling it never affects what a following <see cref="MarkActive"/> or
    /// <see cref="MarkBound"/> returns.
    /// </summary>
    internal bool Matches(int unit, int target, uint texture) =>
        (uint)unit < TrackedUnits && _targets[unit] == target && _textures[unit] == texture;

    /// <summary>
    /// Records that <paramref name="texture"/> is bound to <paramref name="unit"/> on
    /// <paramref name="target"/> and returns true when the caller must actually issue
    /// <c>glBindTexture</c>. Untracked units always return true and record nothing.
    /// </summary>
    internal bool MarkBound(int unit, int target, uint texture)
    {
        if ((uint)unit >= TrackedUnits) return true;
        if (_targets[unit] == target && _textures[unit] == texture) return false;
        _targets[unit] = target;
        _textures[unit] = texture;
        return true;
    }

    /// <summary>Forgets everything: every unit's binding and the active unit itself.</summary>
    internal void Invalidate()
    {
        for (var i = 0; i < TrackedUnits; i++)
        {
            _targets[i] = -1;
            _textures[i] = 0;
        }
        ActiveUnit = UnknownUnit;
    }
}
