namespace silkgltriangle;

/// <summary>What a <see cref="TreeSupport.Search"/> found: the start cell reaches an anchor, it
/// provably cannot (its whole component is then in <see cref="TreeSupport.Component"/>), or the
/// search could not tell (an unloaded read the caller treats as unknown, or the search cap).</summary>
public enum TreeSupportResult
{
    Supported,
    Unsupported,
    Unknown,
}
