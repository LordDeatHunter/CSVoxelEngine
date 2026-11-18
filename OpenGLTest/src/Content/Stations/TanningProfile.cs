namespace VoxelEngine.Content.Stations;

/// <summary>
/// How one kind of tanning vat behaves, compiled from its block's <c>tanning</c> object (<see cref="BlockType.Tanning"/>):
/// what it tans into what, what keeps it working and how long a batch takes. The rules every vat shares live in
/// <see cref="Simulation.Leatherwork.TanningRules"/>.
/// </summary>
/// <param name="InputId">The item it tans (<c>input</c>: the salted hide).</param>
/// <param name="ResultId">What a tanned input becomes (<c>result</c>: leather).</param>
/// <param name="ResultPerInput">How many of the result one input makes (<c>count</c>).</param>
/// <param name="TanninId">The item that tans (<c>tannin</c>: bark); one unit is spent per input tanned.</param>
/// <param name="WaterId">The item that fills it (<c>water</c>: a water bucket, which hands back its emptied self).</param>
/// <param name="Capacity">How many inputs one batch holds, and how many tannin units the vat holds (<c>capacity</c>).</param>
/// <param name="Seconds">Seconds a batch takes while it has water and tannin enough (<c>seconds</c>).</param>
/// <param name="LiquidY">Height of the liquid's surface in the cell, 0..1 (<c>liquid</c>).</param>
public sealed record TanningProfile(ushort InputId, ushort ResultId, int ResultPerInput, ushort TanninId, ushort WaterId,
    int Capacity, float Seconds, float LiquidY);
