using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Magic;
using VoxelEngine.Tests.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>
/// The cosmetic side of a cast (<see cref="SpellFeedback"/>): only a world that draws frames builds it, a
/// client's own projectile cast launches one cosmetic preview bolt (and spends nothing, the server bills it),
/// and singleplayer flies the real bolt with no preview beside it.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SpellFeedbackTests : IDisposable
{
    private SupportTestWorld? _server;
    private World? _world;

    public SpellFeedbackTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _server?.Dispose();
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static SpellPart[] Parts(params string[] keys) =>
        keys.Select(k => SpellPartRegistry.TryGetByKey(k) ?? throw new InvalidOperationException(k)).ToArray();

    // Binds the parts into the local player's held staff and casts slot 0 along the camera's aim
    private static CastVerdict CastLocal(World world, double now, params string[] keys)
    {
        StaffFixture.Hold(world.Inventory, "wooden_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, Parts(keys));
        return world.CastFor(world.LocalPlayer.LocalContext(), 0, new CastPacing(), now, 0);
    }

    [Fact]
    public void AHeadlessServerWorldBuildsNoCastFeedback()
    {
        _server = new SupportTestWorld();
        var world = _server.World;
        Assert.Null(world.Feedback);

        StaffFixture.Hold(_server.Player.Inventory, "wooden_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, Parts("element/fire", "shape/projectile"));
        var context = _server.Player.Context;
        context.Forward = Vector3.UnitX;
        Assert.Equal(CastVerdict.Cast, world.CastFor(context, 0, new CastPacing(), 10.0, 0));

        // The real bolt still flies, whether or not anyone watches it
        Assert.Single(world.Spells!.Projectiles);
    }

    [Fact]
    public void AClientsProjectileCastLaunchesOnePreviewBoltAndSpendsNoMana()
    {
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        var feedback = Assert.IsType<SpellFeedback>(_world.Feedback);
        var mana = _world.LocalPlayer.LocalContext().Health!.Mana.Current;

        Assert.Equal(CastVerdict.Cast, CastLocal(_world, 10.0, "element/fire", "shape/projectile"));
        Assert.Single(feedback.PreviewBolts);
        Assert.Equal(mana, _world.LocalPlayer.LocalContext().Health!.Mana.Current);

        // A self cast flies nothing, preview or otherwise
        Assert.Equal(CastVerdict.Cast, CastLocal(_world, 20.0, "element/frost", "shape/self"));
        Assert.Single(feedback.PreviewBolts);
    }

    [Fact]
    public void ASingleplayerCastFliesTheRealBoltAndNoPreview()
    {
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        var feedback = Assert.IsType<SpellFeedback>(_world.Feedback);
        _world.Caster.FizzleRoll = static () => 1f;

        Assert.Equal(CastVerdict.Cast, CastLocal(_world, 10.0, "element/fire", "shape/projectile"));

        Assert.Single(_world.Spells!.Projectiles);
        Assert.Empty(feedback.PreviewBolts);
    }

    [Fact]
    public void FeedbackFollowsEveryAcceptedCastAndReportsAFizzle()
    {
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        var calls = 0;
        var lastFizzled = false;
        _world.Caster.Feedback = (_, fizzled) =>
        {
            calls++;
            lastFizzled = fizzled;
        };

        _world.Caster.FizzleRoll = static () => 1f;
        Assert.Equal(CastVerdict.Cast, CastLocal(_world, 10.0, "element/fire", "shape/projectile"));
        Assert.Equal(1, calls);
        Assert.False(lastFizzled);

        // A roll of 0 fizzles any recipe complex enough to fizzle at all: the feedback still follows, as a fizzle
        string[] risky = ["element/fire", "shape/projectile", "modifier/power", "modifier/range"];
        Assert.True(CastRisk.Fizzles(new SpellRecipe(Parts(risky)).Compile(), 0f));
        var bolts = _world.Spells!.Projectiles.Count;
        _world.Caster.FizzleRoll = static () => 0f;
        Assert.Equal(CastVerdict.Cast, CastLocal(_world, 20.0, risky));
        Assert.Equal(2, calls);
        Assert.True(lastFizzled);
        Assert.Equal(bolts, _world.Spells.Projectiles.Count);
    }
}
