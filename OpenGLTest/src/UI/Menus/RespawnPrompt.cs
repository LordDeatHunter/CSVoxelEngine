using System.Numerics;

namespace VoxelEngine.UI.Menus;

/// <summary>
/// The death screen's rule, GL-free: it shows once the player has been dead for
/// <see cref="DelaySeconds"/> (the length of the death animation) and the player comes back only
/// when they ask, so a respawn is never automatic. Singleplayer's <c>LocalPlayer</c> and the
/// multiplayer <c>ClientSession</c> each own one, fed the same dead flag their health reads.
/// </summary>
public sealed class RespawnPrompt
{
    /// <summary>Seconds the death plays (the view tips over, the body falls) before the screen shows.</summary>
    public const float DelaySeconds = 0.9f;

    private bool _dead;
    private bool _requested;

    /// <summary>Seconds the player has been dead, 0 while alive.</summary>
    public float DeadSeconds { get; private set; }

    /// <summary>Whether the player is down, from the first dead frame: the fall plays, then the screen shows.</summary>
    public bool IsDead => _dead;

    /// <summary>Whether the death screen is up: dead long enough for the animation to have played.</summary>
    public bool IsShowing => _dead && DeadSeconds >= DelaySeconds;

    /// <summary>Whether the player asked to respawn and the answer is still pending.</summary>
    public bool Requested => _requested;

    /// <summary>Steps the clock; coming back to life clears the request.</summary>
    public void Update(float deltaTime, bool dead)
    {
        _dead = dead;
        if (!dead)
        {
            DeadSeconds = 0f;
            _requested = false;
            return;
        }
        DeadSeconds += deltaTime;
    }

    /// <summary>The player's click or Enter: true exactly once per death, and only while the screen shows.</summary>
    public bool Request()
    {
        if (!IsShowing || _requested) return false;
        _requested = true;
        return true;
    }

    /// <summary>Drops every state, for a world switch or once the respawn has been carried out.</summary>
    public void Reset()
    {
        _dead = false;
        _requested = false;
        DeadSeconds = 0f;
    }
}

/// <summary>
/// Pure layout of the death screen: the title, the death line and the one RESPAWN button, with a
/// hit test over the same rectangle the renderer draws. Origin is top-left, Y down, in canvas pixels.
/// </summary>
public static class DeathScreenLayout
{
    public static float TitleTop(Vector2 screenSize) => screenSize.Y * 0.5f - 130f;

    public static float MessageTop(Vector2 screenSize) => screenSize.Y * 0.5f - 50f;

    public static Vector2 ButtonCenter(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f + 40f);

    public static bool HitsRespawn(Vector2 point, Vector2 screenSize)
    {
        var center = ButtonCenter(screenSize);
        var size = PauseMenuLayout.ButtonSize;
        return float.Abs(point.X - center.X) <= size.X * 0.5f
               && float.Abs(point.Y - center.Y) <= size.Y * 0.5f;
    }
}
