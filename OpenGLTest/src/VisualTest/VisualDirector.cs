using System;
using System.IO;
using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Player;
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
                if (Inventory.CategoryOf(id) == InventoryCategory.Tools)
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
                if (!world.SpawnEntity(step.Entity!, feet, step.Size))
                    throw new InvalidOperationException(
                        $"Step {_step + 1} cannot spawn '{step.Entity}': an unknown entity, or the mob hard cap is full.");
                Log.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} spawn {step.Entity} (size {step.Size}) at {feet}");
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
