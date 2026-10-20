namespace VoxelEngine.Lod;

/// <summary>What one spin of a LodGen worker did, which is what tells the loop how to back off:
/// <see cref="Idle"/> means there is nothing to build anywhere (the long poll), while
/// <see cref="Throttled"/> means work exists and only the main thread's upload drain is behind,
/// which must not be waited on for <c>GenerationDelayIdleMs</c>. Item C5.</summary>
internal enum LodBuildOutcome
{
    Built,
    Idle,
    Throttled
}
