namespace silkgltriangle;

/// <summary>A plain pickup: no right-click behaviour (vine, branch, compass). Exists as a class
/// so the factory always returns a behaviour object and callers never null-check.</summary>
public sealed class PlainItem : Item
{
    internal PlainItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }
}
