using System;

namespace silkgltriangle;

/// <summary>
/// The hotbar's pickup pop: a slot whose count rises bumps its icon up and springs it back.
/// GL-free: <see cref="World"/> feeds it every slot's (item id, count) once a frame and the hotbar
/// draw scales each icon by <see cref="ScaleAt"/>. It reads the slots rather than hooking the
/// pickup, so the same rule serves singleplayer pickups and crafts and a multiplayer client, whose
/// server inventory arrives as a snapshot instead. Only a rise of the same item pops: a falling
/// count (a drop, a craft's ingredients, a placement) and a slot changing item (an equip, a
/// bucket filling) do not. Main thread only.
/// </summary>
public sealed class HotbarPickupPop
{
    private readonly byte[] _ids = new byte[Inventory.SlotCount];
    private readonly int[] _counts = new int[Inventory.SlotCount];
    private readonly bool[] _seen = new bool[Inventory.SlotCount];
    private readonly float[] _elapsed = new float[Inventory.SlotCount];
    private readonly float[] _flashElapsed = new float[Inventory.SlotCount];

    public HotbarPickupPop()
    {
        Array.Fill(_elapsed, WorldGenerationConfig.HotbarPopSeconds);
        Array.Fill(_flashElapsed, WorldGenerationConfig.HotbarFlashSeconds);
    }

    /// <summary>Advances the running pops, then observes every hotbar slot of <paramref name="inventory"/>.</summary>
    public void Update(Inventory inventory, float deltaTime)
    {
        Advance(deltaTime);
        for (var i = 0; i < Inventory.SlotCount; i++)
            Observe(i, inventory.IdAt(i), inventory.CountAt(i));
    }

    /// <summary>
    /// The rule behind <see cref="Update"/>: records what <paramref name="slot"/> shows and starts
    /// its pop when it still shows the same (non-empty) item with a higher count than last time.
    /// The first observation of a slot only records it. Returns whether a pop started.
    /// </summary>
    public bool Observe(int slot, byte id, int count)
    {
        var pop = _seen[slot] && id != 0 && id == _ids[slot] && count > _counts[slot];
        _seen[slot] = true;
        _ids[slot] = id;
        _counts[slot] = count;
        if (pop)
            _elapsed[slot] = 0f;
        return pop;
    }

    /// <summary>Moves every running pop on by <paramref name="deltaTime"/>; a finished one parks at its end.</summary>
    public void Advance(float deltaTime)
    {
        var step = MathF.Max(0f, deltaTime);
        for (var i = 0; i < _elapsed.Length; i++)
        {
            _elapsed[i] = MathF.Min(WorldGenerationConfig.HotbarPopSeconds, _elapsed[i] + step);
            _flashElapsed[i] = MathF.Min(WorldGenerationConfig.HotbarFlashSeconds, _flashElapsed[i] + step);
        }
    }

    /// <summary>Starts <paramref name="slot">'s flash: the tool it held just broke. Independent of
    /// the count pop, so it shows on a slot that is now empty.</summary>
    public void Flash(int slot)
    {
        if (slot >= 0 && slot < _flashElapsed.Length)
            _flashElapsed[slot] = 0f;
    }

    /// <summary>Flash strength of a hotbar slot: 1 right after its tool broke, easing out to 0 over
    /// <see cref="WorldGenerationConfig.HotbarFlashSeconds"/> (0 for a slot that never flashed).</summary>
    public float FlashAt(int slot) =>
        slot < 0 || slot >= _flashElapsed.Length
            ? 0f
            : MathUtils.EaseOutCubic(1f - _flashElapsed[slot] / WorldGenerationConfig.HotbarFlashSeconds);

    /// <summary>Icon scale for a hotbar slot: exactly 1 at rest, briefly larger just after its count rose.</summary>
    public float ScaleAt(int slot) =>
        1f + (WorldGenerationConfig.HotbarPopScale - 1f)
        * MathUtils.BackPulse(_elapsed[slot] / WorldGenerationConfig.HotbarPopSeconds);
}
