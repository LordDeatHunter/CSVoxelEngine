using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The hit-stop on a landed punch or slice: a swing that connected holds at its impact pose for a
/// bounded beat and still ends when it would have; a miss never freezes anything.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HitStopTests : IDisposable
{
    private const float Dt = 1f / 60f;
    // A slice-like swing for the clock tests: 0.7 s long, impact just over a third of the way in.
    private const float SwingRate = 1f / 0.7f;
    private const float SwingImpact = 0.375f;

    private World? _world;

    public HitStopTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // Plays one swing to its end and reports how long it took, how long it was held and where.
    private static (int Frames, float Held, float HeldAt) PlaySwing(SwingHitStop stop, float rate, float impactAt,
        float requestAtStart = 0f, float requestEveryFrame = 0f)
    {
        if (requestAtStart > 0f) stop.Request(requestAtStart);
        var progress = 0f;
        var frames = 0;
        var held = 0f;
        var heldAt = -1f;
        while (progress < 1f && frames < 100_000)
        {
            if (requestEveryFrame > 0f) stop.Request(requestEveryFrame);
            var before = progress;
            progress = stop.Advance(progress, Dt, rate, impactAt);
            frames++;
            if (progress == before)
            {
                held += Dt;
                heldAt = progress;
            }
        }
        return (frames, held, heldAt);
    }

    [Fact]
    public void AMissNeverFreezesTheClock()
    {
        var stop = new SwingHitStop();
        var progress = 0f;
        while (progress < 1f)
        {
            var next = stop.Advance(progress, Dt, SwingRate, SwingImpact);
            Assert.True(next > progress);
            Assert.False(stop.IsHolding);
            progress = next;
        }
    }

    [Fact]
    public void AConnectedSwingHoldsAtItsImpactPoseAndStillEndsOnTime()
    {
        var plain = PlaySwing(new SwingHitStop(), SwingRate, SwingImpact);
        var frozen = PlaySwing(new SwingHitStop(), SwingRate, SwingImpact,
            requestAtStart: WorldGenerationConfig.HitStopSeconds);

        // Held at the impact pose for (about) what was asked...
        Assert.Equal(SwingImpact, frozen.HeldAt);
        Assert.InRange(frozen.Held, WorldGenerationConfig.HitStopSeconds - 2 * Dt, WorldGenerationConfig.HitStopSeconds + Dt);
        // ...and the swing still ends on the same frame, so nothing that waits on it is delayed.
        Assert.InRange(frozen.Frames, plain.Frames - 1, plain.Frames + 1);
    }

    [Fact]
    public void TheHoldNeverPassesTheMaxEvenWhenRequestsPileUp()
    {
        // A long swing, so the share of its follow-through is not what limits the hold: only the cap.
        const float slowRate = 1f / 10f;
        var piled = PlaySwing(new SwingHitStop(), slowRate, 0.2f, requestAtStart: 100f, requestEveryFrame: 100f);
        Assert.True(piled.Held > 0f);
        Assert.True(piled.Held <= WorldGenerationConfig.HitStopMaxSeconds + Dt);

        // Hits landing back to back, one per swing like a held jab: every swing holds once, bounded,
        // and the holds never chain into one longer freeze.
        var stop = new SwingHitStop();
        for (var swing = 0; swing < 20; swing++)
        {
            stop.Reset();
            var result = PlaySwing(stop, SwingRate, SwingImpact, requestAtStart: 100f, requestEveryFrame: 100f);
            Assert.True(result.Held > 0f);
            Assert.True(result.Held <= WorldGenerationConfig.HitStopMaxSeconds + Dt);
        }
    }

    [Fact]
    public void AHeavierSwingNeverHoldsShorterAndNoneHoldsPastTheMax()
    {
        var previous = 0f;
        for (var weight = 0f; weight <= 1.0001f; weight += 0.05f)
        {
            var seconds = SwingHitStop.SecondsFor(weight);
            Assert.True(seconds > 0f);
            Assert.True(seconds >= previous);
            Assert.True(seconds <= WorldGenerationConfig.HitStopMaxSeconds);
            previous = seconds;
        }
    }

    // Plays the model's punch to its end: frames taken, and whether it was ever held.
    private static (int Frames, bool Held) PlayPunch(PlayerModel model)
    {
        var frames = 0;
        var held = false;
        while (model.MineT < 1f && frames < 600)
        {
            model.Update(Dt, 0f);
            frames++;
            held |= model.IsHitStopped;
        }
        return (frames, held);
    }

    // Plays the model's slice to its end, asking for a hold when the swing lands if hitAtImpact
    // (where World's slice lands its damage): frames taken, and whether it was ever held.
    private static (int Frames, bool Held) PlaySlice(PlayerModel model, bool hitAtImpact)
    {
        var frames = 0;
        var held = false;
        var landed = false;
        while (model.IsSlicing && frames < 600)
        {
            model.Update(Dt, 0f);
            frames++;
            if (hitAtImpact && !landed && model.SliceProgress >= PlayerModel.SliceImpactProgress)
            {
                model.HitStop(WorldGenerationConfig.HitStopSeconds);
                landed = true;
            }
            held |= model.IsHitStopped;
        }
        return (frames, held);
    }

    [Fact]
    public void AMissedSwingNeverFreezesTheModel()
    {
        // A request with no swing in progress is dropped: it never freezes the next swing.
        var model = new PlayerModel();
        model.HitStop(WorldGenerationConfig.HitStopSeconds);
        model.Punch();
        var previous = model.MineT;
        while (model.MineT < 1f)
        {
            model.Update(Dt, 0f);
            Assert.True(model.MineT > previous);
            Assert.False(model.IsHitStopped);
            previous = model.MineT;
        }

        model.StartSlice(1);
        Assert.False(PlaySlice(model, hitAtImpact: false).Held);
    }

    [Fact]
    public void ALandedSwingHoldsTheModelAndStillEndsOnTime()
    {
        var plainPunch = new PlayerModel();
        var hitPunch = new PlayerModel();
        plainPunch.Punch();
        hitPunch.Punch();
        hitPunch.HitStop(WorldGenerationConfig.HitStopSeconds);
        var plain = PlayPunch(plainPunch);
        var hit = PlayPunch(hitPunch);
        Assert.False(plain.Held);
        Assert.True(hit.Held);
        Assert.InRange(hit.Frames, plain.Frames - 1, plain.Frames + 1);

        // A new swing drops the last one's hold: the next punch plays plain.
        hitPunch.Punch();
        Assert.False(PlayPunch(hitPunch).Held);

        var plainSlice = new PlayerModel();
        var hitSlice = new PlayerModel();
        plainSlice.StartSlice(1);
        hitSlice.StartSlice(1);
        var plainSwing = PlaySlice(plainSlice, hitAtImpact: false);
        var hitSwing = PlaySlice(hitSlice, hitAtImpact: true);
        Assert.True(hitSwing.Held);
        Assert.InRange(hitSwing.Frames, plainSwing.Frames - 1, plainSwing.Frames + 1);
    }

    [Fact]
    public void HoldingThePunchPinchFreezesTheFovUntilReleased()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);
        for (var i = 0; i < 60; i++)
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
        var rest = camera.CurrentFov;

        camera.PunchFov(-1f);
        for (var i = 0; i < 3; i++)
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
        var pinched = camera.CurrentFov;
        Assert.True(pinched < rest);

        camera.HoldPunchFov = true;
        for (var i = 0; i < 10; i++)
        {
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
            Assert.Equal(pinched, camera.CurrentFov);
        }

        camera.HoldPunchFov = false;
        for (var i = 0; i < 300; i++)
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
        Assert.Equal(rest, camera.CurrentFov, 2);
    }

    // A storeless singleplayer world with the eye high in open air facing -Z (nothing generates up
    // there, so the punch's line of sight is always clear).
    private World NewWorld(out Vector3 eye)
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        eye = new Vector3(0.5f, 300f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        return _world;
    }

    [Fact]
    public void APunchThatLandsOnAMobFreezesTheSwing()
    {
        var world = NewWorld(out var eye);
        // A goblin two blocks straight ahead, its body centred on the aim line.
        Assert.True(world.SpawnEntity("goblin", eye + new Vector3(0f, -Goblin.Height * 0.5f, -2f)));

        world.UpdateBlockBreaking(Dt, isHolding: true);
        var held = false;
        for (var i = 0; i < 60; i++)
        {
            world.Player.Update(Dt, 0f);
            held |= world.Player.IsHitStopped;
        }
        Assert.True(held);
    }

    [Fact]
    public void AClientFreezesOnThePunchItPredictsWillLand()
    {
        // A client never damages a mob itself: it sends the punch, and freezes on the predicted
        // connect (the server's mirrored mob in the cone when the swing lands).
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        var eye = new Vector3(0.5f, 300f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, eye + new Vector3(0f, 0f, -2f), 0f, 3, 0, 0,
            Vector3.Zero)]);
        var sent = 0;
        _world.IntentRaised += _ => sent++;

        _world.UpdateBlockBreaking(Dt, isHolding: true);
        var held = false;
        for (var i = 0; i < 60; i++)
        {
            _world.Player.Update(Dt, 0f);
            held |= _world.Player.IsHitStopped;
        }

        Assert.Equal(1, sent);
        Assert.True(held);
        // The prediction is cosmetic: the mirrored mob took no damage on the client.
        Assert.Equal(3, _world.Slimes[0].Health);
    }

    [Fact]
    public void APunchAtNothingNeverFreezesTheSwing()
    {
        var world = NewWorld(out _);

        // With nothing in front the click still swings the fist (the swing really plays)...
        world.UpdateBlockBreaking(Dt, isHolding: true);
        Assert.True(world.Player.MineT < 1f);
        // ...and never holds it.
        for (var i = 0; i < 60; i++)
        {
            world.Player.Update(Dt, 0f);
            Assert.False(world.Player.IsHitStopped);
        }
    }
}
