using System;
using System.IO;
using VoxelEngine.Content;
using VoxelEngine.Player;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.VisualTest;

/// <summary>
/// Runs a <see cref="VisualScenario"/> inside the live game: each frame advances one step by
/// calling the same <c>World</c> functions real input reaches, with movement keys coming from
/// a <see cref="ScriptedKeyboard"/>. Steps are timed or instant, so a scenario always ends.
/// </summary>
public sealed class VisualDirector(VisualScenario scenario, World world)
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
                // A firestarter strike that never misses: the scripted keyboard cannot equip a tool
                // instance, and the strike's chance roll would make a blast scenario flaky
                if (!world.ItemUse.TestPrimeTnt(step.Pos!.Value))
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

            case "give":
                var id = ContentRegistry.Require(step.Block!);
                world.Inventory.Add(id, step.Count);
                if (world.Inventory.IdAt(world.Inventory.SelectedIndex) != id)
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

    private void Advance() => _step++;
}
