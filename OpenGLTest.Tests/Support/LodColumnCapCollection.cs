using VoxelEngine.Tests.Lod;
using VoxelEngine.Tests.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Serializes the tests that compare LOD region builds near the origin
/// (<see cref="LodBuildPoolTests"/>, <see cref="LodHeightSamplingTests"/>) against
/// <see cref="FallingBlockTests"/>, whose toppling sand removes cells near the origin and so
/// writes <c>LodRegion.ColumnCaps</c> through <c>LowerColumnBelow</c>. The caps are process-global
/// and feed every region whose sample window reaches those columns, including the one-cell ring
/// the per-vertex sky exposure (<c>LodSkyExposure</c>) reads, so a cap landing between two builds
/// of the same region makes them differ. xUnit runs test classes in parallel by default, so
/// without this collection that interleaving is a rare flake that looks like an impure build.
/// </summary>
[CollectionDefinition("LOD column caps")]
public sealed class LodColumnCapCollection;
