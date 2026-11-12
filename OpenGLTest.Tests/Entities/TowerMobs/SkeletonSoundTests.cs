using System.IO;
using VoxelEngine.Audio;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

public sealed class SkeletonSoundTests
{
    public SkeletonSoundTests() => TestContent.EnsureLoaded();

    [Fact]
    public void HitAndDeathSoundsNameShippedClipGroups()
    {
        var groups = SfxLibrary.Scan(Path.Combine(TestContent.FindAssetsRoot(), "sfx"));
        var skeleton = new Skeleton(EntityRegistry.Require(Skeleton.EntityName), System.Numerics.Vector3.Zero, spawnIn: false);
        Assert.Contains(skeleton.HitSoundGroup!, groups.Keys);
        Assert.Contains(skeleton.DeathSoundGroup!, groups.Keys);
        Assert.NotEqual(skeleton.HitSoundGroup, skeleton.DeathSoundGroup);
    }
}
