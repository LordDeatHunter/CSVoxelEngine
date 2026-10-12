using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Silk.NET.Input;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="Camera.GetViewProjection"/> is cached per distinct camera pose (PERF_TODO R7).
/// The cache is key-validated rather than dirty-flagged: a hit means every input the two matrix
/// builders read was bit-identical, so the recomputation would have produced a bit-identical
/// matrix. These facts pin that claim from both sides.
///
/// <c>Reference</c> is the pre-change expression reimplemented inline — <c>GetViewMatrix() *
/// GetProjectionMatrix()</c>, both deliberately left uncached — and every fact asserts exact
/// equality against it. But an oracle-only fact would pass with no cache at all, and a walking
/// drive moves several key fields at once, so dropping one from the key would be invisible to it.
/// Hence one single-field fact per key entry, driven through
/// <see cref="Camera.TestSetViewState"/>, <see cref="Camera.TestTeleport"/>,
/// <see cref="Camera.TestSetCameraLook"/> and <see cref="Camera.SetAspectRatio"/> (the last being
/// the very method the window's FramebufferResize handler calls), each asserting the matrix
/// actually CHANGED as well as matching the oracle. Drop a field from the key and its fact fails
/// on the "changed" half. <c>KeyHoldsExactlyTheMatrixInputs</c> makes editing the key itself a
/// conscious act.
///
/// What is NOT covered here: nothing in this cache touches GL, but the key can only be proved
/// complete against the builders as they are today — a new Camera field wired into
/// <see cref="Camera.GetViewMatrix"/> tomorrow without a matching key entry would still slip
/// through, which is what the invariant comment on GetViewProjection is for.
///
/// <see cref="Camera.GetViewFrustum"/> (PERF_TODO R11) rides the same key and is refreshed in the
/// same miss branch, replacing three independent <c>new Frustum(camera.GetViewProjection())</c>
/// builds a frame in RenderWorld, CullWater and RenderLod. <see cref="Frustum"/> is a readonly
/// struct of private <see cref="Plane"/> fields with no equality of its own, so the frustum facts
/// compare BEHAVIOUR: <c>IntersectsAABB</c> over a grid of boxes around the camera, against a
/// frustum freshly built from the oracle matrix, requiring both an accepted and a rejected box so
/// the comparison cannot pass vacuously.
/// </summary>
public sealed class CameraViewProjectionCacheTests
{
    public CameraViewProjectionCacheTests()
    {
        TestContent.EnsureLoaded();
        // GameSettings.FieldOfView is 0 until the game (or a settings test) initialises it, and
        // Matrix4x4.CreatePerspectiveFieldOfView rejects a zero FOV. Camera reads it through
        // DefaultFov both at construction and every Update, so these facts must not depend on
        // some other test class having loaded settings first.
        if (GameSettings.FieldOfView <= 0f) GameSettings.ResetToDefaults();
    }

    /// <summary>The pre-change expression, verbatim, as the reference the cache must reproduce.</summary>
    private static Matrix4x4 Reference(Camera c) => c.GetViewMatrix() * c.GetProjectionMatrix();

    private static Camera Settled()
    {
        var cam = new Camera(null, null);
        cam.TestTeleport(new Vector3(12.5f, 40f, -7.25f), yaw: -90f, pitch: 0f);
        return cam;
    }

    private static bool SameBits(Matrix4x4 a, Matrix4x4 b)
    {
        foreach (var field in typeof(Matrix4x4).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var av = (float)field.GetValue(a)!;
            var bv = (float)field.GetValue(b)!;
            if (BitConverter.SingleToInt32Bits(av) != BitConverter.SingleToInt32Bits(bv))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Populates the cache, mutates exactly one key field, and asserts the returned matrix both
    /// moved and still equals a fresh view * projection.
    /// </summary>
    private static void OneFieldMoves(Action<Camera> mutate)
    {
        var cam = Settled();
        var before = cam.GetViewProjection();

        mutate(cam);

        var after = cam.GetViewProjection();
        Assert.NotEqual(before, after);
        Assert.Equal(Reference(cam), after);
    }

    // A floor at y=0 across one chunk, the shape CameraStepTests walks over.
    private static ChunkManager FlatFloor(out Camera camera)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        manager.Chunks[Vector3.Zero] = chunk;

        camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return manager;
    }

    /// <summary>
    /// Boxes on a 5x3x5 lattice around the camera, 24 apart and 8 across. Some sit in front of the
    /// camera and some behind it at every pose these facts use, and all sit well inside the 4096
    /// far plane, so the lattice separates two frusta that differ in any plane.
    /// </summary>
    private static (Vector3 Min, Vector3 Max)[] Grid(Vector3 center)
    {
        var boxes = new List<(Vector3, Vector3)>();
        for (var x = -2; x <= 2; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -2; z <= 2; z++)
        {
            var min = center + new Vector3(x * 24f, y * 24f, z * 24f);
            boxes.Add((min, min + new Vector3(8f)));
        }

        return boxes.ToArray();
    }

    private static bool[] Pattern(Frustum frustum, Vector3 center) =>
        Grid(center).Select(b => frustum.IntersectsAABB(b.Min, b.Max)).ToArray();

    /// <summary>
    /// The cached frustum must accept and reject exactly what a frustum built right now from the
    /// oracle matrix does, and the lattice must produce both answers so the comparison is live.
    /// </summary>
    private static void FrustumAgrees(Camera cam, string where)
    {
        var cached = Pattern(cam.GetViewFrustum(), cam.Position);
        var fresh = Pattern(new Frustum(Reference(cam)), cam.Position);

        Assert.True(cached.SequenceEqual(fresh), $"cached frustum disagreed with a fresh one: {where}");
        Assert.True(cached.Any(v => v) && cached.Any(v => !v),
            $"the lattice produced only one outcome, so the fact is vacuous: {where}");
    }

    [Fact]
    public void CachedViewProjectionMatchesViewTimesProjection()
    {
        var cam = Settled();

        // Cold: the cache is empty and must compute the same thing the old expression did.
        Assert.Equal(Reference(cam), cam.GetViewProjection());
        // Warm: the identical call again, now served from the cache.
        Assert.Equal(Reference(cam), cam.GetViewProjection());
    }

    [Fact]
    public void RepeatedCallsAreBitIdentical()
    {
        var cam = Settled();

        var first = cam.GetViewProjection();
        var second = cam.GetViewProjection();

        Assert.True(SameBits(first, second), "a cache hit returned a different matrix");
    }

    [Fact]
    public void TeleportChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestTeleport(new Vector3(80.5f, 40f, -7.25f), yaw: -90f, pitch: 0f));

    [Fact]
    public void LookingChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetCameraLook(yaw: 35f, pitch: -20f));

    [Fact]
    public void AspectChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.SetAspectRatio(4f / 3f));

    [Fact]
    public void FovChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(fov: cam.CurrentFov + 10f));

    [Fact]
    public void RollChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(roll: 0.3f));

    [Fact]
    public void BobHeightChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(bobHeight: 0.25f));

    [Fact]
    public void BobStrafeChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(bobStrafe: 0.25f));

    [Fact]
    public void ShakeOffsetChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(shakeOffset: new Vector3(0.2f, -0.1f, 0.15f)));

    [Fact]
    public void ShakeRollChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(shakeRoll: 0.03f));

    [Fact]
    public void ShoulderOffsetChangeChangesTheMatrix() =>
        OneFieldMoves(cam => cam.TestSetViewState(shoulderOffset: new Vector3(0.6f, 0.2f, -1.5f)));

    [Fact]
    public void ShoulderViewDirChangeChangesTheMatrix() =>
        // Long enough to pass ViewDirection's 1e-8 length test, and not the spawn forward vector.
        OneFieldMoves(cam =>
            cam.TestSetViewState(shoulderViewDir: Vector3.Normalize(new Vector3(0.3f, -0.2f, 0.9f))));

    [Fact]
    public void KeyHoldsExactlyTheMatrixInputs()
    {
        // A mechanical forcing function for anyone editing the key: unlike a whole-Camera field
        // scan it does not fire on unrelated new fields, but the key cannot be edited silently.
        string[] expected =
        [
            "Position", "ShoulderViewDir", "ShoulderOffset", "Yaw", "Pitch",
            "Roll", "BobHeight", "BobStrafe", "ShakeOffset", "ShakeRoll", "Fov", "Aspect",
        ];

        var key = typeof(Camera).GetNestedType("ViewProjKey", BindingFlags.NonPublic);
        Assert.NotNull(key);

        var positional = key!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(expected, positional);

        var properties = key.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), properties);
    }

    [Fact]
    public void WalkingRevalidatesEveryStep()
    {
        // An integration fact over the real mutation path: Update writes Position, the bob and
        // the roll every step, so the cache has to revalidate against a moving pose rather than
        // against a hand-set field.
        var manager = FlatFloor(out var cam);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);

        var first = cam.GetViewProjection();
        var last = first;
        for (var i = 0; i < 120; i++)
        {
            if (i == 60) keys.Press([Key.W, Key.ShiftLeft]);
            cam.Update(1f / 60f, keys, manager);
            last = cam.GetViewProjection();
            Assert.Equal(Reference(cam), last);
        }

        keys.ReleaseAll();
        Assert.NotEqual(first, last);
    }

    [Fact]
    public void ViewFrustumMatchesAFreshFrustumFromViewTimesProjection()
    {
        var cam = Settled();
        FrustumAgrees(cam, "fresh camera");

        cam.TestTeleport(new Vector3(80.5f, 40f, -7.25f), yaw: -90f, pitch: 0f);
        FrustumAgrees(cam, "after teleport");

        cam.TestSetCameraLook(yaw: 35f, pitch: -20f);
        FrustumAgrees(cam, "after look");

        cam.SetAspectRatio(4f / 3f);
        FrustumAgrees(cam, "after aspect change");

        // One key field at a time, the same seams the matrix facts drive.
        cam.TestSetViewState(fov: cam.CurrentFov + 10f);
        FrustumAgrees(cam, "after fov change");
        cam.TestSetViewState(roll: 0.3f);
        FrustumAgrees(cam, "after roll change");
        cam.TestSetViewState(bobHeight: 0.25f);
        FrustumAgrees(cam, "after bob height change");
        cam.TestSetViewState(bobStrafe: 0.25f);
        FrustumAgrees(cam, "after bob strafe change");
        cam.TestSetViewState(shakeOffset: new Vector3(0.2f, -0.1f, 0.15f));
        FrustumAgrees(cam, "after shake offset change");
        cam.TestSetViewState(shakeRoll: 0.03f);
        FrustumAgrees(cam, "after shake roll change");
        cam.TestSetViewState(shoulderOffset: new Vector3(0.6f, 0.2f, -1.5f));
        FrustumAgrees(cam, "after shoulder offset change");
        cam.TestSetViewState(shoulderViewDir: Vector3.Normalize(new Vector3(0.3f, -0.2f, 0.9f)));
        FrustumAgrees(cam, "after shoulder view dir change");

        // And over the real mutation path, every step of a walk.
        var manager = FlatFloor(out var walker);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        for (var i = 0; i < 120; i++)
        {
            if (i == 60) keys.Press([Key.W, Key.ShiftLeft]);
            walker.Update(1f / 60f, keys, manager);
            FrustumAgrees(walker, $"walking step {i}");
        }

        keys.ReleaseAll();
    }

    [Fact]
    public void RepeatedFrustumCallsAgree()
    {
        var cam = Settled();
        var center = cam.Position;

        var first = Pattern(cam.GetViewFrustum(), center);
        var second = Pattern(cam.GetViewFrustum(), center);

        Assert.Equal(first, second);
        Assert.True(first.Any(v => v) && first.Any(v => !v), "the lattice produced only one outcome");
    }

    [Fact]
    public void FrustumAndMatrixShareOneKey()
    {
        // Guards against the two caches drifting apart if EnsureViewCache is ever split: one
        // mutation must move both the matrix and the frustum, and the frustum must still match a
        // fresh build from the moved matrix. The lattice is pinned to the ORIGINAL centre here, so
        // a half-turn of the yaw has to flip which boxes are accepted.
        var cam = Settled();
        var center = cam.Position;

        var beforeMatrix = cam.GetViewProjection();
        var beforeFrustum = Pattern(cam.GetViewFrustum(), center);

        cam.TestSetCameraLook(yaw: 90f, pitch: 0f);

        var afterMatrix = cam.GetViewProjection();
        var afterFrustum = Pattern(cam.GetViewFrustum(), center);

        Assert.NotEqual(beforeMatrix, afterMatrix);
        Assert.NotEqual(beforeFrustum, afterFrustum);
        Assert.Equal(Pattern(new Frustum(Reference(cam)), center), afterFrustum);
    }
}
