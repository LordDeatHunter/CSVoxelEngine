namespace VoxelEngine.Items;

/// <summary>
/// The firestarter's own code class: the worked example of a per-item override. Behavior is
/// <see cref="IgniteItem"/>'s today (primes dry TNT, lights max-count branch piles); a future
/// spark that must never prime TNT narrows <see cref="IgniteItem.CanPrimeTnt"/> here instead of
/// growing another branch in the world. Registered by file name in
/// <see cref="ItemFactory.CustomCreators"/>.
/// </summary>
public sealed class FirestarterItem : IgniteItem
{
    internal FirestarterItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }
}
