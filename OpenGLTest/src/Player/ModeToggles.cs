using Silk.NET.Input;
using VoxelEngine.UI;

namespace VoxelEngine.Player;

/// <summary>The camera's keyboard mode latches: the creative toggle (C), the creative-only double-tap Space flight toggle and the sprint latch (<see cref="SprintToggle"/>).</summary>
internal sealed class ModeToggles
{
    private readonly Camera _owner;
    private readonly PlayerPhysics _physics;

    // The sprint latch (see SprintToggle): with the toggle setting on, the key flips it on each
    // press instead of being held
    private bool _sprintLatched;
    private bool _sprintKeyWasHeld;

    internal bool SprintHeld { get; private set; }

    internal void UpdateSprintLatch(bool keyHeld)
    {
        SprintHeld = SprintToggle.Step(_sprintLatched, keyHeld, _sprintKeyWasHeld, GameSettings.SprintToggle);
        _sprintLatched = SprintHeld;
        _sprintKeyWasHeld = keyHeld;
    }

    // Two Space presses within this window toggle flight
    private const float DoubleTapWindow = 0.3f;

    private bool _spaceWasPressed;
    private bool _creativeWasPressed;
    private float _timeSinceSpacePress = float.MaxValue;

    internal ModeToggles(Camera owner, PlayerPhysics physics)
    {
        _owner = owner;
        _physics = physics;
    }

    internal void MarkTogglesHeld()
    {
        _creativeWasPressed = true;
        _spaceWasPressed = true;
    }

    internal void UpdateCreativeToggle(IKeyboard keyboard)
    {
        var creativePressed = keyboard.IsKeyPressed(Key.C);
        if (creativePressed && !_creativeWasPressed)
        {
            _owner.IsCreative = !_owner.IsCreative;
            if (!_owner.IsCreative)
                _owner.IsFlying = false;
            _physics.ResetVerticalVelocity();
            Log.WriteLine($"Creative {(_owner.IsCreative ? "ON" : "OFF")}");
        }

        _creativeWasPressed = creativePressed;
    }

    internal void UpdateFlightToggle(float deltaTime, IKeyboard keyboard)
    {
        _timeSinceSpacePress += deltaTime;

        var spacePressed = keyboard.IsKeyPressed(Key.Space);
        if (spacePressed && !_spaceWasPressed)
        {
            if (_timeSinceSpacePress <= DoubleTapWindow)
            {
                // Flight is a creative-only move: on foot the second tap is just a jump.
                if (_owner.IsCreative)
                {
                    _owner.IsFlying = !_owner.IsFlying;
                    _physics.ResetVerticalVelocity();
                    Log.WriteLine($"Flight {(_owner.IsFlying ? "ON" : "OFF")}");
                }
                _timeSinceSpacePress = float.MaxValue; // a toggle requires a fresh double tap
            }
            else
            {
                _timeSinceSpacePress = 0f;
            }
        }

        _spaceWasPressed = spacePressed;
    }
}
