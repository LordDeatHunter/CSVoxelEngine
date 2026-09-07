using System;
using System.IO;

namespace silkgltriangle;

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
            Console.WriteLine($"[visual] '{scenario.Name}' done ({scenario.Steps.Count} steps).");
            IsDone = true;
            return;
        }

        var step = scenario.Steps[_step];
        switch (step.Op)
        {
            case "teleport":
                world.Camera.TestTeleport(step.Pos!.Value,
                    step.Yaw ?? world.Camera.Yaw, step.Pitch ?? world.Camera.Pitch);
                Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} teleport {step.Pos}");
                Advance();
                break;

            case "look":
                world.Camera.TestTeleport(world.Camera.Position,
                    step.Yaw ?? world.Camera.Yaw, step.Pitch ?? world.Camera.Pitch);
                Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} look yaw {world.Camera.Yaw:F0} pitch {world.Camera.Pitch:F0}");
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
                world.UpdateBlockBreaking(deltaTime, isHolding: true);
                TickTimed(deltaTime, step, "mine");
                break;

            case "place":
                world.TryUseSelected(World.InteractionReach);
                Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} place/use");
                Advance();
                break;

            case "give":
                var id = ContentRegistry.RequireBlock(step.Block!).Id;
                world.Inventory.Add(id, step.Count);
                world.Inventory.Select(world.Inventory.Slots.IndexOf(id));
                Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} give {step.Count} {step.Block}");
                Advance();
                break;

            case "screenshot":
                ScreenshotPath = Path.Combine(scenario.OutputDir, $"{_step + 1:D2}-{step.ShotName}.png");
                ScreenshotHideHud = step.HideHud;
                TakeScreenshot = true;
                Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} screenshot -> {ScreenshotPath}");
                Advance();
                break;
        }
    }

    private void TickTimed(double deltaTime, VisualStep step, string label)
    {
        if (_timeLeft <= 0f)
        {
            _timeLeft = step.Seconds;
            Console.WriteLine($"[visual] {_step + 1}/{scenario.Steps.Count} {label} {step.Seconds}s");
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
