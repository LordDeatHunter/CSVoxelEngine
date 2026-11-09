using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.Status;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.VisualTest;

/// <summary>
/// Runs a <see cref="VisualScenario"/> inside the live game: each frame advances one step by
/// calling the same <c>World</c> functions real input reaches, with movement keys coming from
/// a <see cref="ScriptedKeyboard"/>. Steps are timed or instant, so a scenario always ends.
/// </summary>
public sealed class VisualDirector(VisualScenario scenario, World world, IScenarioUi? ui = null)
{
    private readonly ScriptedKeyboard _keys = new();
    private int _step;
    private float _timeLeft;

    public bool IsDone { get; private set; }
    public bool TakeScreenshot { get; set; }
    public string ScreenshotPath { get; private set; } = "";
    public bool ScreenshotHideHud { get; private set; }

    public void Drive(double deltaTime)
    {
        if (IsDone) return;

        if (_step >= scenario.Steps.Count)
        {
            Log.WriteLine($"[visual] '{scenario.Name}' done ({scenario.Steps.Count} steps).");
            IsDone = true;
            return;
        }

        var step = scenario.Steps[_step];
        switch (step.Op)
        {
            case "teleport":
                world.Camera.TestTeleport(step.Pos!.Value,
                    step.Yaw ?? world.Camera.Yaw, step.Pitch ?? world.Camera.Pitch);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} teleport {step.Pos}");
                Advance();
                break;

            case "look":
                world.Camera.TestTeleport(world.Camera.Position,
                    step.Yaw ?? world.Camera.Yaw, step.Pitch ?? world.Camera.Pitch);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} look yaw {world.Camera.Yaw:F0} pitch {world.Camera.Pitch:F0}");
                Advance();
                break;

            case "wait":
                _keys.ReleaseAll();
                world.Update(deltaTime, _keys);
                TickTimed(deltaTime, step, "wait");
                break;

            case "walk":
                _keys.Press(step.Keys);
                world.Update(deltaTime, _keys);
                TickTimed(deltaTime, step, $"walk [{string.Join("+", step.Keys)}]");
                break;

            case "mine":
                _keys.ReleaseAll();
                world.Update(deltaTime, _keys);
                FrameProfiler.Begin(ProfileSection.Breaking);
                world.LeftClick.UpdateBlockBreaking(deltaTime, isHolding: true);
                FrameProfiler.End(ProfileSection.Breaking);
                TickTimed(deltaTime, step, "mine");
                break;

            case "place":
                FrameProfiler.Begin(ProfileSection.UsePlace);
                world.RightClick.TryUseSelected(World.InteractionReach);
                FrameProfiler.End(ProfileSection.UsePlace);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} place/use");
                Advance();
                break;

            case "prime":
                // A firestarter strike that never misses: the strike's chance roll would make a
                // blast scenario flaky
                if (!world.ItemUse.PrimeTnt(step.Pos!.Value))
                    throw new InvalidOperationException(
                        $"Step {_step + 1} primes {step.Pos}, which holds no primable block.");
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} prime {step.Pos}");
                Advance();
                break;

            case "fly":
                world.Camera.TestSetFlight(step.On);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} fly {(step.On ? "on" : "off")}");
                Advance();
                break;

            case "creative":
                // Creative alone, no flight: a dropped player still falls and lands, but takes no
                // fall damage. Turning it off ends flight too, as the C key does.
                world.Camera.SetModes(step.On, step.On && world.Camera.IsFlying);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} creative {(step.On ? "on" : "off")}");
                Advance();
                break;

            case "give":
                var id = ContentRegistry.Require(step.Block!);
                world.Inventory.Add(id, step.Count);
                if (Inventory.CategoryOf(id) == InventoryCategory.Gear)
                {
                    // A piece of gear is put on, in its first fitting slot, so a shot shows it worn
                    var worn = world.Inventory.GearEntries().Where(t => t.ItemId == id).Select(t => t.InstanceId)
                        .DefaultIfEmpty(-1).Max();
                    if (worn >= 0) world.Inventory.Equipment.TryEquipAuto(worn);
                }
                else if (Inventory.CategoryOf(id) == InventoryCategory.Tools)
                {
                    // A tool arrives as a new instance: put the newest one of that id in hand
                    var newest = world.Inventory.ToolEntries().Where(t => t.ItemId == id).Select(t => t.InstanceId).DefaultIfEmpty(-1).Max();
                    if (newest >= 0 && world.Inventory.ToolInstanceAt(world.Inventory.SelectedIndex) != newest)
                        world.Inventory.EquipTool(world.Inventory.SelectedIndex, newest);
                }
                else if (world.Inventory.IdAt(world.Inventory.SelectedIndex) != id)
                    world.Inventory.EquipStackable(world.Inventory.SelectedIndex, id);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} give {step.Count} {step.Block}");
                Advance();
                break;

            case "spawn":
                // The entity name is validated here rather than at parse time, so parsing stays
                // registry-free for the tests.
                var feet = step.Pos!.Value;
                if (step.SnapToGround)
                {
                    if (!world.TryGroundHeight((int)MathF.Floor(feet.X), (int)MathF.Floor(feet.Z), out var ground))
                        throw new InvalidOperationException(
                            $"Step {_step + 1} spawns on column ({feet.X}, {feet.Z}), whose ground height is unavailable.");
                    feet.Y = ground + 1;
                }
                if (!world.SpawnEntity(step.Entity!, feet, step.Size, step.Tier))
                    throw new InvalidOperationException(
                        $"Step {_step + 1} cannot spawn '{step.Entity}': an unknown entity, or the mob hard cap is full.");
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} spawn {step.Entity} (size {step.Size}, tier {step.Tier}) at {feet}");
                Advance();
                break;

            case "grow":
            {
                // The species name is validated here rather than at parse time, so parsing stays
                // registry-free for the tests.
                var column = step.Pos!.Value;
                var gx = (int)MathF.Floor(column.X);
                var gz = (int)MathF.Floor(column.Z);
                if (!world.TryGroundHeight(gx, gz, out var growGround))
                    throw new InvalidOperationException(
                        $"Step {_step + 1} grows on column ({gx}, {gz}), whose ground height is unavailable.");
                if (TreeSpeciesRegistry.TryGet(step.Species!) == null)
                    throw new InvalidOperationException(
                        $"Step {_step + 1} grows unknown tree species '{step.Species}'.");
                var result = world.TryGrowTree(step.Species!, gx, growGround + 1, gz, step.GrowSteps);
                if (!result.Succeeded)
                    throw new InvalidOperationException(
                        $"Step {_step + 1} could not grow {step.Species} on column ({gx}, {gz}): {result.Status}.");
                Log.WriteLine(
                    $"[visual] {_step + 1}/{scenario.Steps.Count} grow {step.Species} at ({gx}, {gz}): "
                    + $"{result.Status}, +{result.NodesAdded} nodes, {result.CellsWritten} cells");
                Advance();
                break;
            }

            case "damage":
                if (step.Target == "player")
                    world.LocalPlayer.DamagePlayer(step.Amount);
                else
                    world.DamageAllMobs(step.Amount);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} damage {step.Target} by {step.Amount}");
                Advance();
                break;

            case "effect":
                ApplyEffect(step);
                Advance();
                break;

            case "cast":
                Cast(step);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} cast {string.Join(" + ", step.Parts)}");
                Advance();
                break;

            case "view":
                world.Camera.TestSetMode(step.Mode switch
                {
                    "shoulder" => CameraMode.Shoulder,
                    "orbit" => CameraMode.Orbit,
                    _ => CameraMode.FirstPerson,
                });
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} view {step.Mode}");
                Advance();
                break;

            case "player":
                world.LocalPlayer.TestPlayerAction(step.Action!);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} player {step.Action}");
                Advance();
                break;

            case "select":
                // What a hotbar number key does in singleplayer
                world.Inventory.Select(step.Slot - 1);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} select slot {step.Slot}");
                Advance();
                break;

            case "panel":
                RunUi("panel", u => u.SetPanelOpen(step.On));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} panel {(step.On ? "open" : "closed")}");
                Advance();
                break;

            case "tab":
                RunUi("tab", u => u.SelectTab(step.Tab!));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} tab {step.Tab}");
                Advance();
                break;

            case "search":
                RunUi("search", u => u.TypeSearch(step.Text!));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} search '{step.Text}'");
                Advance();
                break;

            case "row":
                RunUi("row", u => u.PickRow(step.Row - 1));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} row {step.Row}");
                Advance();
                break;

            case "hover":
                RunUi("hover", u => u.SetPointer(step.Point));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} hover {(step.Point is { } p ? p.ToString() : "cleared")}");
                Advance();
                break;

            case "setblock":
            {
                var blockId = ContentRegistry.Require(step.Block!);
                var cell = step.Pos!.Value;
                var chunks = world.GetChunkManager();
                var chunk = chunks.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)
                    ?? throw new InvalidOperationException($"Step {_step + 1} sets {cell}, which is not in a loaded chunk.");
                chunk.PlaceBlockAt(cell, blockId, chunks, (byte)step.State);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} setblock {step.Block} at {cell}");
                Advance();
                break;
            }

            case "bloomery":
                BuildBloomery(step);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} bloomery at {step.Pos}");
                Advance();
                break;

            case "anvil":
                BuildAnvil(step);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} anvil at {step.Pos}");
                Advance();
                break;

            case "hammer":
                // One hit that ignores the hammer's gap, so a scenario can film the whole work in a few frames
                world.HammerFor(step.Pos!.Value, 0f, 0f);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} hammer {step.Pos}");
                Advance();
                break;

            case "goblincamp":
            {
                // A night's goblin camp pitched now on that column: its lit fire and its goblins round it
                var at = step.Pos!.Value;
                var risen = world.Director.PitchGoblinCamp((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} goblincamp at ({at.X}, {at.Z}): "
                              + (risen >= 0 ? $"{risen} goblins" : "no camp here"));
                Advance();
                break;
            }

            case "towerclear":
                // Clears the floor of the tower around the camera (its piles spent, its skeletons dead), so its
                // door opens on the next frames as if the floor had been fought
                var cleared = world.ClearTowerFloor(world.Camera.Position, step.Floor);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} towerclear floor {step.Floor}: "
                              + (cleared ? "cleared" : "no tower here"));
                Advance();
                break;

            case "altar":
                // A right-click on the tower altar at that cell, through the same call the click chain makes
                var verdict = world.UseAltar(step.Pos!.Value);
                world.Toast.Show(TowerReadout.AltarToast(verdict));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} altar {step.Pos}: {verdict}");
                Advance();
                break;

            case "towerdefeat":
                // Marks the Skeleton King of the tower around the camera killed, so the real collapse runs
                var defeated = world.DefeatTower(world.Camera.Position);
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} towerdefeat: "
                              + (defeated ? "defeated" : "no tower here"));
                Advance();
                break;

            case "chest":
                RunUi("chest", u =>
                {
                    if (step.On) u.OpenChest((int)step.Pos!.Value.X, (int)step.Pos.Value.Y, (int)step.Pos.Value.Z);
                    else u.CloseChest();
                });
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} chest {(step.On ? "open" : "closed")}");
                Advance();
                break;

            case "chestrow":
                RunUi("chestrow", u => u.PickChestRow(step.Row - 1, step.On));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} chest row {step.Row}");
                Advance();
                break;

            case "waystone":
                RunUi("waystone", u => u.Waystone(step.Action!, step.Pos, step.Text, step.Row - 1));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} waystone {step.Action}");
                Advance();
                break;

            case "spellbuilder":
                RunUi("spellbuilder", u => u.SetSpellBuilderOpen(step.On));
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} spellbuilder {(step.On ? "open" : "closed")}");
                Advance();
                break;

            case "screenshot":
                ScreenshotPath = Path.Combine(scenario.OutputDir, $"{_step + 1:D2}-{step.ShotName}.png");
                ScreenshotHideHud = step.HideHud;
                TakeScreenshot = true;
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} screenshot -> {ScreenshotPath}");
                Advance();
                break;
        }
    }

    private void TickTimed(double deltaTime, VisualStep step, string label)
    {
        if (_timeLeft <= 0f)
        {
            _timeLeft = step.Seconds;
            Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} {label} {step.Seconds}s");
        }

        _timeLeft -= (float)deltaTime;
        if (_timeLeft <= 0f)
        {
            _timeLeft = 0f;
            _keys.ReleaseAll();
            Advance();
        }
    }

    /// <summary>An <c>effect</c> step: the effect name is validated here rather than at parse time, so
    /// parsing stays registry-free; an unknown name warns and the step is skipped.</summary>
    // A bloomery at the step's anchor, built the way the converter builds one (a 2x2x2 of its structure, converted
    // in place) and loaded with the step's ore and fuel; a lit one burns at the step's heat
    private void BuildBloomery(VisualStep step)
    {
        var type = ContentRegistry.RequireBlock("bloomery");
        var anchor = step.Pos!.Value;
        var chunks = world.GetChunkManager();
        var touched = new HashSet<Vector3>();
        foreach (var (dx, dy, dz) in Multiblock.Offsets(type.MultiblockSize))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var chunk = chunks.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)
                ?? throw new InvalidOperationException($"Step {_step + 1} builds at {cell}, which is not in a loaded chunk.");
            chunk.PlaceBlockAt(cell, type.MultiblockSourceId, chunks, 0);
        }
        Multiblock.Convert(chunks, anchor, step.State & 3, type, touched);
        foreach (var chunkPos in touched)
            if (chunks.Chunks.TryGetValue(chunkPos, out var chunk))
                chunk.RemeshAfterBatchEdit(chunks);

        var bloomeries = world.Smithing.Bloomeries;
        bloomeries.Ensure(anchor, step.State & 3, type.Id);
        for (var i = 0; i < step.Ore; i++)
            bloomeries.TryAddOre(anchor, type.Bloomery!.OreId);
        var fuel = ContentRegistry.RequireItem("coal");
        for (var i = 0; i < step.Fuel; i++)
            bloomeries.TryAddFuel(anchor, fuel);
        if (step.On && bloomeries.Ignite(anchor))
            bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z)!.Heat = step.Heat;
    }

    // An anvil at the step's cell, with a bloom on it that has taken the step's hits
    private void BuildAnvil(VisualStep step)
    {
        var type = ContentRegistry.RequireBlock("anvil");
        var cell = step.Pos!.Value;
        var chunks = world.GetChunkManager();
        var chunk = chunks.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)
            ?? throw new InvalidOperationException($"Step {_step + 1} builds at {cell}, which is not in a loaded chunk.");
        chunk.PlaceBlockAt(cell, type.Id, chunks, 0);
        var anvils = world.Smithing.Anvils;
        anvils.Ensure(cell, type.Id);
        if (!step.On) return;
        anvils.TryPlace(cell, type.Anvil!.InputId);
        anvils.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z)!.Hits = Math.Clamp(step.Hits, 0, type.Anvil.Hits - 1);
    }

    private void ApplyEffect(VisualStep step)
    {
        var label = $"[visual] {_step + 1}/{scenario.Steps.Count} effect {step.Effect} on {step.Target} (x{step.Scale})";
        if (!StatusEffectRegistry.Contains(step.Effect!))
        {
            Log.Warn($"{label}: no status effect '{step.Effect}' is registered; skipped.");
            return;
        }

        if (step.Target == "player")
        {
            var applied = world.ApplyPlayerEffect(step.Effect!, step.Scale);
            Log.WriteLine(applied ? label : $"{label}: refused (immune or dead)");
            return;
        }

        var count = 0;
        foreach (var mob in world.Mobs)
        {
            if (StatusEffects.Apply(mob, step.Effect!, step.Scale))
                count++;
        }
        Log.WriteLine($"{label}: {count} mob(s)");
    }

    /// <summary>A <c>cast</c> step: the part keys are resolved here rather than at parse time, so parsing
    /// stays registry-free; an unknown key or an invalid recipe stops the run.</summary>
    private void Cast(VisualStep step)
    {
        var parts = new SpellPart[step.Parts.Length];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = SpellPartRegistry.TryGetByKey(step.Parts[i])
                       ?? throw new InvalidOperationException($"Step {_step + 1} (cast): no spell part '{step.Parts[i]}'.");
        var spells = world.Spells
                     ?? throw new InvalidOperationException($"Step {_step + 1} (cast): this world casts no spells.");
        if (!spells.TestCast(parts))
            throw new InvalidOperationException(
                $"Step {_step + 1} (cast): '{string.Join(", ", step.Parts)}' is not a valid spell.");
    }

    // A UI step runs against the live UI; a refusal (no world playing, a creative row, no staff) stops the run
    // naming the step, like the other run-time checks.
    private void RunUi(string op, Action<IScenarioUi> action)
    {
        if (ui == null)
            throw new InvalidOperationException($"Step {_step + 1} ({op}) needs the live UI, which this run has none of.");
        try
        {
            action(ui);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException($"Step {_step + 1} ({op}): {e.Message}", e);
        }
    }

    private void Advance() => _step++;
}
