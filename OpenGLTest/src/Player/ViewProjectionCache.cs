using System;
using System.Numerics;
using VoxelEngine.Data;

namespace VoxelEngine.Player;

/// <summary>
/// Camera's view*projection matrix and frustum, cached per distinct pose and validated bit for bit
/// against <see cref="Camera.ViewProjKey"/> (PERF_TODO R7/R11).
/// </summary>
internal sealed class ViewProjectionCache
{
    private readonly Camera _owner;
    // PERF_TODO R7: GetViewProjection ran at 22 call sites a frame, rebuilding view and
    // projection every time. The cache is validated against its own inputs rather than
    // dirty-flagged: the key holds exactly the mutable state GetViewMatrix and
    // GetProjectionMatrix read, compared bit-for-bit, so a hit means the recomputation
    // would have produced a bit-identical matrix. Render and update run on one thread
    // (AGENTS.md), so these are plain fields with no lock.
    private Camera.ViewProjKey _vpKey;
    private Matrix4x4 _viewProj;
    private Frustum _viewFrustum;
    private bool _vpValid;

    internal ViewProjectionCache(Camera owner) => _owner = owner;

    internal Matrix4x4 GetViewProjection()
    {
        EnsureViewCache();
        return _viewProj;
    }

    internal Frustum GetViewFrustum()
    {
        EnsureViewCache();
        return _viewFrustum;
    }

    /// <summary>
    /// Revalidates the cached view-projection matrix and its frustum against the current
    /// pose. One key and one miss branch for both, so a caller can never see a frustum that
    /// belongs to a different matrix than <see cref="Camera.GetViewProjection"/> returns; rebuilding
    /// the frustum on a matrix miss is free relative to the matrix build itself.
    /// </summary>
    private void EnsureViewCache()
    {
        var key = _owner.CurrentViewProjKey();
        if (_vpValid && SameBits(in _vpKey, in key)) return;

        _vpKey = key;
        _viewProj = _owner.GetViewMatrix() * _owner.GetProjectionMatrix();
        _viewFrustum = new Frustum(_viewProj);
        _vpValid = true;
    }

    /// <summary>
    /// Bit-for-bit key comparison. Deliberately not <c>==</c> or the record's generated
    /// equality: <c>float</c> equality calls +0f and -0f equal and the generated
    /// <c>Equals</c> calls NaN and NaN equal, and the correctness argument here is strictly
    /// "the inputs were bit-identical, therefore the recomputation would have been
    /// bit-identical".
    /// </summary>
    private static bool SameBits(in Camera.ViewProjKey a, in Camera.ViewProjKey b) =>
        SameBits(a.Position, b.Position)
        && SameBits(a.ShoulderViewDir, b.ShoulderViewDir)
        && SameBits(a.ShoulderOffset, b.ShoulderOffset)
        && SameBits(a.Yaw, b.Yaw)
        && SameBits(a.Pitch, b.Pitch)
        && SameBits(a.Roll, b.Roll)
        && SameBits(a.BobHeight, b.BobHeight)
        && SameBits(a.BobStrafe, b.BobStrafe)
        && SameBits(a.ShakeOffset, b.ShakeOffset)
        && SameBits(a.ShakeRoll, b.ShakeRoll)
        && SameBits(a.Fov, b.Fov)
        && SameBits(a.Aspect, b.Aspect);

    private static bool SameBits(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool SameBits(in Vector3 a, in Vector3 b) =>
        SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);
}
