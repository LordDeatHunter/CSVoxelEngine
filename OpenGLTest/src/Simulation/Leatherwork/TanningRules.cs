using VoxelEngine.Content.Stations;

namespace VoxelEngine.Simulation.Leatherwork;

/// <summary>
/// The tanning vat's rules, GL-free: a batch of inputs tans while the vat holds water and a tannin unit for every input,
/// and pauses (keeping its progress) whenever either runs short. The batch finishes after its profile's seconds: every
/// input becomes <see cref="TanningProfile.ResultPerInput"/> of the result, a tannin unit per input is spent and the
/// water is used up. Nothing is ever lost to a pause.
/// </summary>
public static class TanningRules
{
    /// <summary>Whether the vat's batch is tanning now: inputs in it, water, and a tannin unit for each input.</summary>
    public static bool CanTan(TanningVatManager.VatState state) =>
        state.Hides > 0 && state.Water && state.Bark >= state.Hides;

    /// <summary>How far the batch is, 0..1.</summary>
    public static float Progress01(TanningVatManager.VatState state, TanningProfile profile) =>
        state.Progress >= profile.Seconds ? 1f : state.Progress / profile.Seconds;

    /// <summary>
    /// Runs the batch for <paramref name="deltaTime"/> seconds when it can tan (<see cref="CanTan"/>), else leaves it
    /// paused. Returns whether the batch finished: its inputs became the result waiting to be collected, its tannin and
    /// water were spent and the progress starts over.
    /// </summary>
    public static bool Step(TanningVatManager.VatState state, TanningProfile profile, float deltaTime)
    {
        if (deltaTime <= 0f || !CanTan(state)) return false;
        state.Progress += deltaTime;
        if (state.Progress < profile.Seconds) return false;

        state.Leather += state.Hides * profile.ResultPerInput;
        state.Bark -= state.Hides;
        state.Hides = 0;
        state.Water = false;
        state.Progress = 0f;
        return true;
    }
}
