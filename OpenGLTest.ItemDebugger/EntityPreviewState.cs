using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// GL-free state behind the item debugger's ENTITIES tab: which entity is previewed and which
/// mode it is shown in. The modes are data-driven — one per clip the selected model authored,
/// in file order, followed by <see cref="HurtMode"/> (the hit-flash tint, which is not a clip) —
/// so a model with no clips, such as the slime, offers only HURT and previews its rest pose.
/// Idle and walk drive the locomotion layer at rest and at the walk reference speed; any other
/// clip is a one-shot that replays after <see cref="RestBetweenPlays"/>, and the preview consumes
/// the events it fires so none pile up. Snapshots <see cref="EntityRegistry"/> on construction,
/// so build it only after content is loaded.
/// </summary>
public sealed class EntityPreviewState
{
    /// <summary>The mode that pulses the hit flash instead of playing a clip.</summary>
    public const string HurtMode = "hurt";

    /// <summary>Seconds a one-shot clip rests between the end of one play and the next.</summary>
    public const float RestBetweenPlays = 0.4f;

    /// <summary>Seconds between two hit flashes in <see cref="HurtMode"/>.</summary>
    public const float HurtPeriod = 1f;

    /// <summary>Seconds one hit flash takes to fade from 1 to 0 (the in-game fade).</summary>
    public const float HitFlashSeconds = 0.25f;

    private static readonly string[] HurtOnly = [HurtMode];

    private float _rest;
    private float _hurtTimer;

    public EntityPreviewState()
    {
        var entries = new List<EntityType>(EntityRegistry.ByName.Values);
        entries.Sort((a, b) =>
        {
            var byDisplay = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byDisplay != 0 ? byDisplay : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        Entries = entries;
        Animator = EmptyAnimator();
        Modes = HurtOnly;
        Mode = HurtMode;
        SelectedIndex = -1;
        if (entries.Count > 0) Select(0);
    }

    /// <summary>Every registered entity, sorted by display name then file name.</summary>
    public IReadOnlyList<EntityType> Entries { get; }

    /// <summary>Index into <see cref="Entries"/> of the previewed entity; -1 when there is none.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>The previewed entity, or null when no entity is registered.</summary>
    public EntityType? Selected => SelectedIndex >= 0 ? Entries[SelectedIndex] : null;

    /// <summary>Plays the selected model's clips; rebuilt on every selection.</summary>
    public EntityAnimator Animator { get; private set; }

    /// <summary>The selected model's clip names in file order, then <see cref="HurtMode"/>.</summary>
    public IReadOnlyList<string> Modes { get; private set; }

    /// <summary>The active mode, always one of <see cref="Modes"/> as spelled there.</summary>
    public string Mode { get; private set; }

    /// <summary>True when the selected model authored at least one clip.</summary>
    public bool HasClips => Animator.Clips.Count > 0;

    /// <summary>True when the previewed rig has the hand the goblin holds its flint in.</summary>
    public bool ShowsHeldStone => Selected != null && Selected.Model.Parts.ContainsKey(Goblin.HandPart);

    /// <summary>The hit-flash tint to draw with, 0..1; only non-zero in <see cref="HurtMode"/>.</summary>
    public float HitFlash { get; private set; }

    /// <summary>Yaw that turns the model (which faces -Z at 0) toward the default orbit camera
    /// on the +Z side.</summary>
    public float FacingYaw => MathF.PI;

    /// <summary>Seconds fed to <see cref="Update"/> since the last <see cref="Select"/> or
    /// <see cref="SetMode"/>.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Previews <see cref="Entries"/>[<paramref name="index"/>] (clamped): a fresh
    /// animator, its modes, the first mode active.</summary>
    public void Select(int index)
    {
        if (Entries.Count == 0) return;
        SelectedIndex = Math.Clamp(index, 0, Entries.Count - 1);
        var clips = Entries[SelectedIndex].Model.Animations;
        Animator = new EntityAnimator(clips, WorldGenerationConfig.GoblinWalkSpeed);
        if (clips.Count == 0)
        {
            Modes = HurtOnly;
        }
        else
        {
            var modes = new string[clips.Count + 1];
            for (var i = 0; i < clips.Count; i++) modes[i] = clips[i].Name;
            modes[^1] = HurtMode;
            Modes = modes;
        }
        Mode = Modes[0];
        ResetTimers();
    }

    /// <summary>Selects the entity whose file name or display name matches (ignoring case).</summary>
    public bool SelectByName(string name)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!string.Equals(Entries[i].Name, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Entries[i].DisplayName, name, StringComparison.OrdinalIgnoreCase)) continue;
            Select(i);
            return true;
        }
        return false;
    }

    /// <summary>Switches to one of <see cref="Modes"/> (ignoring case), restarting the replay
    /// rest, the hurt pulse and <see cref="Elapsed"/>; false for any other name. The animator is
    /// kept, so switching from walk to idle crossfades rather than snapping.</summary>
    public bool SetMode(string mode)
    {
        for (var i = 0; i < Modes.Count; i++)
        {
            if (!string.Equals(Modes[i], mode, StringComparison.OrdinalIgnoreCase)) continue;
            Mode = Modes[i];
            ResetTimers();
            return true;
        }
        return false;
    }

    /// <summary>Advances the preview by <paramref name="deltaTime"/> seconds in the active mode.</summary>
    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f) return;
        Elapsed += deltaTime;

        if (string.Equals(Mode, EntityAnimator.IdleClip, StringComparison.OrdinalIgnoreCase))
        {
            Animator.Update(deltaTime, 0f);
        }
        else if (string.Equals(Mode, EntityAnimator.WalkClip, StringComparison.OrdinalIgnoreCase))
        {
            Animator.Update(deltaTime, Animator.WalkReferenceSpeed);
        }
        else if (Mode == HurtMode)
        {
            _hurtTimer -= deltaTime;
            if (_hurtTimer <= 0f)
            {
                HitFlash = 1f;
                _hurtTimer += HurtPeriod;
            }
            else
            {
                HitFlash = MathF.Max(0f, HitFlash - deltaTime / HitFlashSeconds);
            }
            Animator.Update(deltaTime, 0f);
        }
        else
        {
            // A one-shot: the rest only counts down between plays, so each play ends, rests
            // RestBetweenPlays, then starts again (the first starts at once).
            if (!Animator.IsPlaying(Mode))
            {
                _rest -= deltaTime;
                if (_rest <= 0f)
                {
                    Animator.Play(Mode);
                    _rest = RestBetweenPlays;
                }
            }
            Animator.Update(deltaTime, 0f);
            Animator.DrainEvents();
        }
    }

    private void ResetTimers()
    {
        _rest = 0f;
        _hurtTimer = 0f;
        HitFlash = 0f;
        Elapsed = 0f;
    }

    private static EntityAnimator EmptyAnimator() =>
        new(Array.Empty<AnimationClip>(), WorldGenerationConfig.GoblinWalkSpeed);
}
