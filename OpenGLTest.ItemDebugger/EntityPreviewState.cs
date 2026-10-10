using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free state behind the item debugger's ENTITIES tab: which entity is previewed and which
/// mode it is shown in. The modes are data-driven. A model with clips offers one per clip in file
/// order (idle and walk drive the locomotion layer at rest and at the walk reference speed; any other
/// clip is a one-shot that replays after <see cref="RestBetweenPlays"/>, and the preview consumes the
/// events it fires so none pile up) and <see cref="HurtMode"/> when it authored no <c>hurt</c> clip of
/// its own. A clip-less model (the slime) offers the life-cycle modes <see cref="SlimeModes"/>.
/// <para>
/// A goblin or slime is previewed as the real <see cref="Goblin"/> / <see cref="Slime"/> object, driven
/// the way a client drives a mob it does not simulate (<see cref="Mob.AnimateMirror"/>), so everything the
/// game layers on top of the clips shows up here too: the spawn pop, the hit recoil and flash (and the
/// goblin's overlay flinch), the death, the goblin's body pose (<see cref="BodyEffect"/>) and the slime's
/// spring squash. The slime hops on a hidden floor. Snapshots <see cref="EntityRegistry"/> on
/// construction, so build it only after content is loaded.
/// </para>
/// </summary>
public sealed class EntityPreviewState : IPreviewDriver
{
    /// <summary>The mode that hurts the entity: the hit flash, recoil and (when authored) the hurt clip.</summary>
    public const string HurtMode = "hurt";

    /// <summary>Slime modes: standing, hopping toward the camera, taking hits, popping in, dying.</summary>
    public const string SlimeIdle = "idle";
    public const string SlimeHop = "hop";
    public const string SpawnMode = "spawn";
    public const string DeathMode = "death";

    /// <summary>Seconds a one-shot clip rests between the end of one play and the next.</summary>
    public const float RestBetweenPlays = 0.4f;

    /// <summary>Seconds between two hits in <see cref="HurtMode"/>.</summary>
    public const float HurtPeriod = 1f;

    /// <summary>Seconds one hit flash takes to fade from 1 to 0 (the in-game fade).</summary>
    public const float HitFlashSeconds = 0.25f;

    /// <summary>Seconds between two spawns in <see cref="SpawnMode"/>.</summary>
    public const float SpawnPeriod = 1.6f;

    private static readonly string[] HurtOnly = [HurtMode];
    private static readonly string[] SlimeModes = [SlimeIdle, SlimeHop, HurtMode, SpawnMode, DeathMode];

    // Where the previewed slime starts on the hidden floor, and how far ahead (toward the camera,
    // +Z) its hop target is.
    private static readonly Vector3 SlimeStart = new(8.5f, 1f + Slime.HalfExtent, 4.5f);
    private const float SlimeHopDistance = 3f;
    private const float SlimeHopCycleSeconds = 4f;

    private float _rest;
    private float _hurtTimer;
    private float _cycle;
    private Goblin? _goblin;
    private Slime? _slime;
    private ChunkManager? _floor;

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

    /// <summary>Plays the selected model's clips (a goblin's own animator); rebuilt on every selection.</summary>
    public EntityAnimator Animator { get; private set; }

    /// <summary>The selected model's modes: its clip names in file order (then <see cref="HurtMode"/> unless
    /// it has a <c>hurt</c> clip), or the slime's life-cycle modes.</summary>
    public IReadOnlyList<string> Modes { get; private set; }

    /// <summary>The active mode, always one of <see cref="Modes"/> as spelled there.</summary>
    public string Mode { get; private set; }

    /// <summary>True when the selected model authored at least one clip.</summary>
    public bool HasClips => Animator.Clips.Count > 0;

    /// <summary>True when the previewed rig has the hand the goblin holds its flint in.</summary>
    public bool ShowsHeldStone => Selected != null && Selected.Model.Parts.ContainsKey(Goblin.HandPart);

    /// <summary>Whether the modes replay by themselves (a one-shot after a rest, the hurt pulse, a death or spawn starting
    /// over). Off, each plays once and holds; the <see cref="AnimationClock"/> turns it off and owns the replay.</summary>
    public bool Replay { get; set; } = true;

    private float RestFor(float seconds) => Replay ? seconds : float.MaxValue / 4f;

    /// <summary>True for idle and walk: no start or end of their own, so a clock wraps them.</summary>
    public bool IsContinuous =>
        string.Equals(Mode, EntityAnimator.IdleClip, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Mode, EntityAnimator.WalkClip, StringComparison.OrdinalIgnoreCase);

    /// <summary>Seconds one play of the active mode lasts: its clip's length, or the life-cycle mode's own length.</summary>
    public float Duration
    {
        get
        {
            if (string.Equals(Mode, HurtMode, StringComparison.OrdinalIgnoreCase)) return HurtPeriod;
            if (string.Equals(Mode, SpawnMode, StringComparison.OrdinalIgnoreCase)) return 0.6f;
            if (string.Equals(Mode, DeathMode, StringComparison.OrdinalIgnoreCase))
                return (PreviewMob?.DeathSeconds ?? Mob.DefaultDeathSeconds) + 0.2f;
            if (string.Equals(Mode, SlimeHop, StringComparison.OrdinalIgnoreCase)) return 2.6f;
            if (Animator.Find(Mode) is { } clip && clip.Length > 0f) return clip.Length;
            return 2f;
        }
    }

    /// <summary>Back to the exact start of the active mode: a fresh mob (already settled unless the mode shows the spawn)
    /// or a fresh animator, so stepping it forward again reproduces the same frames.</summary>
    public void Restart()
    {
        var showSpawn = string.Equals(Mode, SpawnMode, StringComparison.OrdinalIgnoreCase);
        if (_goblin != null)
        {
            _goblin = NewGoblin(showSpawn);
            Animator = _goblin.Animation;
        }
        else if (_slime != null)
        {
            _slime = NewSlime();
            if (!showSpawn) _slime.FinishSpawn();
        }
        else if (Selected != null)
        {
            Animator = new EntityAnimator(Selected.Model.Animations, WorldGenerationConfig.GoblinWalkSpeed);
        }
        ResetTimers();
    }

    /// <summary>The mob being previewed (a goblin or a slime), or null for any other entity.</summary>
    public Mob? PreviewMob => (Mob?)_goblin ?? _slime;

    /// <summary>The hit-flash tint to draw with, 0..1.</summary>
    public float HitFlash => PreviewMob?.HitFlash ?? _flash;

    private float _flash;

    /// <summary>Yaw that turns the model (which faces -Z at 0) toward the default orbit camera
    /// on the +Z side.</summary>
    public float FacingYaw => MathF.PI;

    /// <summary>Where the model's feet are relative to the orbit centre: the origin, except for a hopping slime,
    /// which travels (and rises) with its hop.</summary>
    public Vector3 DrawOffset => _slime is { } s
        ? new Vector3(s.Position.X - SlimeStart.X, s.Position.Y - (1f + Slime.HalfExtent), s.Position.Z - SlimeStart.Z)
        : Vector3.Zero;

    /// <summary>
    /// The pose the clips cannot give, to multiply after each part's matrix (row-vector order): the
    /// goblin's lean, flinch, death fall and spawn pop (<see cref="Goblin.BodyEffect"/>), or the slime's
    /// squash and stretch about its feet. Identity for any other entity.
    /// </summary>
    public Matrix4x4 BodyEffect()
    {
        var feet = DrawOffset;
        if (_goblin is { } goblin) return goblin.BodyEffect(feet);
        if (_slime is { } slime)
            return Matrix4x4.CreateTranslation(-feet)
                   * Matrix4x4.CreateScale(slime.HorizontalScale, slime.VerticalScale, slime.HorizontalScale)
                   * Matrix4x4.CreateTranslation(feet);
        return Matrix4x4.Identity;
    }

    /// <summary>Seconds fed to <see cref="Update"/> since the last <see cref="Select"/> or
    /// <see cref="SetMode"/>.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Previews <see cref="Entries"/>[<paramref name="index"/>] (clamped): a fresh
    /// animator, its modes, the first mode active.</summary>
    public void Select(int index)
    {
        if (Entries.Count == 0) return;
        SelectedIndex = Math.Clamp(index, 0, Entries.Count - 1);
        var type = Entries[SelectedIndex];
        var clips = type.Model.Animations;
        _goblin = null;
        _slime = null;
        if (string.Equals(type.Name, "goblin", StringComparison.OrdinalIgnoreCase))
            _goblin = NewGoblin();
        else if (string.Equals(type.Name, "slime", StringComparison.OrdinalIgnoreCase))
            _slime = NewSlime();
        Animator = _goblin?.Animation ?? new EntityAnimator(clips, WorldGenerationConfig.GoblinWalkSpeed);

        if (_slime != null)
        {
            Modes = SlimeModes;
        }
        else if (clips.Count == 0)
        {
            Modes = HurtOnly;
        }
        else
        {
            // A model with its own hurt clip lists it once: the mode hurts the mob and plays the clip.
            var hasHurtClip = Animator.HasClip(HurtMode);
            var modes = new string[clips.Count + (hasHurtClip ? 0 : 1)];
            for (var i = 0; i < clips.Count; i++) modes[i] = clips[i].Name;
            if (!hasHurtClip) modes[^1] = HurtMode;
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
    /// rest, the hurt pulse and <see cref="Elapsed"/>; false for any other name. A goblin keeps its
    /// animator, so switching from walk to idle crossfades rather than snapping; only a mob that has
    /// died is replaced.</summary>
    public bool SetMode(string mode)
    {
        for (var i = 0; i < Modes.Count; i++)
        {
            if (!string.Equals(Modes[i], mode, StringComparison.OrdinalIgnoreCase)) continue;
            Mode = Modes[i];
            if (PreviewMob is { IsDying: true }) Respawn();
            if (_slime != null) _slime = NewSlime();
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

        if (_slime != null) UpdateSlime(deltaTime);
        else if (_goblin != null) UpdateGoblin(deltaTime);
        else UpdateGeneric(deltaTime);
    }

    // ---- goblin --------------------------------------------------------------------------

    private void UpdateGoblin(float dt)
    {
        var goblin = _goblin!;
        var walking = string.Equals(Mode, EntityAnimator.WalkClip, StringComparison.OrdinalIgnoreCase);
        // The mirrored velocity is what walks the clip (a client's goblin is driven the same way).
        goblin.NetworkSetState(Vector3.Zero, 0f, goblin.Health,
            walking ? new Vector3(0f, 0f, Animator.WalkReferenceSpeed) : Vector3.Zero);

        if (string.Equals(Mode, DeathMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!goblin.IsDying) goblin.BeginDying();
            _cycle += dt;
            if (Replay && goblin.IsRemovable && _cycle >= goblin.DeathSeconds + RestBetweenPlays)
            {
                _goblin = NewGoblin();
                Animator = _goblin.Animation;
                _cycle = 0f;
            }
        }
        else if (string.Equals(Mode, SpawnMode, StringComparison.OrdinalIgnoreCase))
        {
            _cycle += dt;
            if (Replay && _cycle >= SpawnPeriod)
            {
                _goblin = NewGoblin();
                Animator = _goblin.Animation;
                _cycle = 0f;
            }
        }
        else if (Mode == HurtMode)
        {
            HurtPulse(goblin, dt);
        }
        else if (!walking && !string.Equals(Mode, EntityAnimator.IdleClip, StringComparison.OrdinalIgnoreCase))
        {
            // A one-shot (throw, melee): the rest only counts down between plays, so each play ends,
            // rests RestBetweenPlays, then starts again (the first starts at once).
            if (!goblin.Animation.IsPlaying(Mode))
            {
                _rest -= dt;
                if (_rest <= 0f)
                {
                    goblin.Animation.Play(Mode);
                    _rest = RestFor(RestBetweenPlays);
                }
            }
        }

        _goblin!.AnimateMirror(dt);
        _goblin.Animation.DrainEvents();
    }

    // ---- slime ---------------------------------------------------------------------------

    private void UpdateSlime(float dt)
    {
        var slime = _slime!;
        Vector3? target = null;

        if (string.Equals(Mode, SlimeHop, StringComparison.OrdinalIgnoreCase))
        {
            // Hop at a point ahead, toward the camera; start over once it has arrived (or a while has
            // passed), so the hop can be watched again and the slime stays near the frame.
            target = SlimeStart + new Vector3(0f, 0f, SlimeHopDistance);
            _cycle += dt;
            if (Replay && (_cycle >= SlimeHopCycleSeconds ||
                (slime.IsGrounded && MathF.Abs(slime.Position.Z - target.Value.Z) < 0.5f && _cycle > 0.5f)))
            {
                _slime = slime = NewSlime();
                _cycle = 0f;
            }
        }
        else if (string.Equals(Mode, SpawnMode, StringComparison.OrdinalIgnoreCase))
        {
            _cycle += dt;
            if (Replay && _cycle >= SpawnPeriod)
            {
                _slime = slime = NewSlime();
                _cycle = 0f;
            }
        }
        else if (string.Equals(Mode, DeathMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!slime.IsDying)
            {
                slime.TakeDamage(slime.Health);
                slime.BeginDying();
            }
            _cycle += dt;
            if (Replay && slime.IsRemovable && _cycle >= slime.DeathSeconds + RestBetweenPlays)
            {
                _slime = slime = NewSlime();
                _cycle = 0f;
            }
        }
        else if (Mode == HurtMode)
        {
            HurtPulse(slime, dt);
        }

        _floor ??= BuildFloor();
        slime.Update(dt, _floor, target);
    }

    // ---- shared --------------------------------------------------------------------------

    // One hit per HurtPeriod; a mob that is nearly dead is topped back up so the loop never kills it.
    private void HurtPulse(Mob mob, float dt)
    {
        _hurtTimer -= dt;
        if (_hurtTimer > 0f) return;
        _hurtTimer += RestFor(HurtPeriod);
        if (mob.Health <= 2) mob.NetworkSetState(mob.Position, mob.Facing, mob.MaxHealth, mob.Velocity);
        mob.TakeDamage(1);
    }

    // A model that is neither a goblin nor a slime: clips only, with the plain hit-flash pulse.
    private void UpdateGeneric(float dt)
    {
        if (string.Equals(Mode, EntityAnimator.IdleClip, StringComparison.OrdinalIgnoreCase))
        {
            Animator.Update(dt, 0f);
        }
        else if (string.Equals(Mode, EntityAnimator.WalkClip, StringComparison.OrdinalIgnoreCase))
        {
            Animator.Update(dt, Animator.WalkReferenceSpeed);
        }
        else if (Mode == HurtMode)
        {
            _hurtTimer -= dt;
            if (_hurtTimer <= 0f)
            {
                _flash = 1f;
                _hurtTimer += RestFor(HurtPeriod);
            }
            else
            {
                _flash = MathF.Max(0f, _flash - dt / HitFlashSeconds);
            }
            Animator.Update(dt, 0f);
        }
        else
        {
            if (!Animator.IsPlaying(Mode))
            {
                _rest -= dt;
                if (_rest <= 0f)
                {
                    Animator.Play(Mode);
                    _rest = RestFor(RestBetweenPlays);
                }
            }
            Animator.Update(dt, 0f);
            Animator.DrainEvents();
        }
    }

    private void Respawn()
    {
        if (_goblin != null)
        {
            _goblin = NewGoblin();
            Animator = _goblin.Animation;
        }
        else if (_slime != null)
        {
            _slime = NewSlime();
        }
    }

    private Goblin NewGoblin(bool spawnIn = true) => new(Selected!, Vector3.Zero, ammo: 3, spawnIn);

    private Slime NewSlime() => new(Selected!, SlimeStart);

    // The hidden floor a slime hops on: one chunk of stone with its top at y = 1.
    private static ChunkManager BuildFloor()
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false) { IsBlockDataReady = true };
        chunks.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return chunks;
    }

    private void ResetTimers()
    {
        _rest = 0f;
        _hurtTimer = 0f;
        _cycle = 0f;
        _flash = 0f;
        Elapsed = 0f;
    }

    private static EntityAnimator EmptyAnimator() =>
        new(Array.Empty<AnimationClip>(), WorldGenerationConfig.GoblinWalkSpeed);
}
