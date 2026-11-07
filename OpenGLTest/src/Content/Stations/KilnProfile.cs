using System;
using VoxelEngine.Simulation;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// How one kind of kiln behaves, compiled from its anchor block's <c>kiln</c> object
/// (<see cref="BlockType.Kiln"/>): how many fuel units its hearth holds, how fast it melts, and
/// whether it may alloy two metals. The kiln manager reads it through the anchor's block id, so
/// the plain kiln and a larger furnace share one state, save and wire shape.
/// </summary>
/// <param name="FuelCap">Units of fuel the hearth holds at once.</param>
/// <param name="MeltSpeed">Multiplier on the melt pace: 2 melts a chunk in half the time.</param>
/// <param name="CanAlloy">Whether the kiln may hold two metals at once and alloy them.</param>
public sealed record KilnProfile(int FuelCap, float MeltSpeed, bool CanAlloy)
{
    /// <summary>Fuel cap of a kiln object that names none.</summary>
    public const int DefaultFuelCap = 4;

    /// <summary>Smallest legal fuel cap.</summary>
    public const int MinFuelCap = 1;

    /// <summary>Largest legal fuel cap.</summary>
    public const int MaxFuelCap = 64;

    /// <summary>Largest legal melt speed (the smallest is anything above 0).</summary>
    public const float MaxMeltSpeed = 8f;

    /// <summary>Seconds a kiln of speed 1 takes to melt one <see cref="KilnManager.MeltChunkMl"/>
    /// chunk of a crucible's pending charge.</summary>
    public const float BaseMeltSeconds = 2f;

    /// <summary>The plain kiln's profile: the defaults of an empty <c>kiln</c> object.</summary>
    public static readonly KilnProfile Default = new(DefaultFuelCap, 1f, false);

    /// <summary>Seconds this kiln takes to melt one <see cref="KilnManager.MeltChunkMl"/> chunk
    /// while lit.</summary>
    public float MeltSeconds => BaseMeltSeconds / MeltSpeed;

    /// <summary>
    /// A validated profile: <paramref name="fuelCap"/> in
    /// <see cref="MinFuelCap"/>..<see cref="MaxFuelCap"/> and <paramref name="meltSpeed"/> above 0
    /// and at most <see cref="MaxMeltSpeed"/>; anything else throws an
    /// <see cref="ArgumentException"/> saying which.
    /// </summary>
    public static KilnProfile Create(int fuelCap, float meltSpeed, bool canAlloy)
    {
        if (fuelCap is < MinFuelCap or > MaxFuelCap)
            throw new ArgumentException($"fuelCap {fuelCap}; a kiln holds {MinFuelCap}..{MaxFuelCap} fuel units.");
        if (!float.IsFinite(meltSpeed) || meltSpeed <= 0f || meltSpeed > MaxMeltSpeed)
            throw new ArgumentException($"meltSpeed {meltSpeed}; a kiln melts at a speed above 0 and at most {MaxMeltSpeed}.");
        return new KilnProfile(fuelCap, meltSpeed, canAlloy);
    }
}
