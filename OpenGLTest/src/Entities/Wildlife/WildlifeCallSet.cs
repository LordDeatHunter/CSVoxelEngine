using System;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Config;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>The ambient calls the wildlife makes: the crows' caw and the boars' and dire boars' grunts.</summary>
internal static class WildlifeCallSet
{
    /// <summary>One <see cref="WildlifeCalls"/> per calling species, each on its own timer.</summary>
    public static WildlifeCalls[] Build(Action<string, Vector3> play, Random random) =>
    [
        new(play, random, WildlifeKind.Crow, SfxLibrary.CrowCawGroup, AudioConfig.CrowCawRangeBlocks,
            AudioConfig.CrowCawMinSeconds, AudioConfig.CrowCawMaxSeconds),
        new(play, random, WildlifeKind.Boar, SfxLibrary.BoarAmbientGroup, AudioConfig.BoarGruntRangeBlocks,
            AudioConfig.BoarGruntMinSeconds, AudioConfig.BoarGruntMaxSeconds),
        new(play, random, WildlifeKind.DireBoar, SfxLibrary.DireBoarAmbientGroup, AudioConfig.BoarGruntRangeBlocks,
            AudioConfig.BoarGruntMinSeconds, AudioConfig.BoarGruntMaxSeconds),
    ];
}
