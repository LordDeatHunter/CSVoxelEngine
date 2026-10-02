using System;

namespace silkgltriangle;

/// <summary>
/// The direction chooser a tree species' <c>growth.logic</c> names (oak, conifer, cactus, ...).
/// <see cref="TreeGrower.Pulse(TreeGraph, TreeSpecies, ITreeSpace, SplitMix64, int)"/> pre-fills
/// the six direction weights and the logic reshapes them; blocked directions are masked after
/// the logic runs, so a logic can never grow into an occupied or terrain cell. Implementations
/// must hold no mutable state, because the generation worker threads call them concurrently;
/// every random draw comes from the <see cref="SplitMix64"/> passed in.
/// </summary>
public interface IGrowthLogic
{
    /// <summary>The name a species' <c>growth.logic</c> refers to.</summary>
    string Name { get; }

    /// <summary>
    /// The energy every pulse of one tree starts with, rolled once per tree (Dynamic Trees'
    /// per-tree energy variation). Must return 1..<see cref="TreeSpecies.SignalEnergy"/>.
    /// </summary>
    int TreeEnergy(TreeSpecies species, SplitMix64 rng);

    /// <summary>
    /// Reshapes the direction weights at the signal's node. <paramref name="weights"/> has six
    /// entries in <c>Chunk.CubeFaces</c> order, pre-filled by <see cref="TreeGrower"/>; the
    /// logic may raise, lower or zero entries.
    /// </summary>
    void AdjustWeights(in GrowthSignal signal, Span<float> weights);

    /// <summary>
    /// Called when the chosen direction differs from <see cref="GrowthSignal.IncomingDir"/>,
    /// with the energy left after the step; returns the remaining energy. The grower clamps the
    /// result to 0..<paramref name="energy"/>, so a turn can cost energy but never add any.
    /// </summary>
    int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy);
}
