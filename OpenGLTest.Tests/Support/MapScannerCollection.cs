using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// The scanner's two passes use shared static scratch. Keep its scanner and tracker test callers
/// serialized so another collection cannot replace the first pass while a scan packs its texels.
/// MapSaveTests also scans through MapTracker; this collection runs alone, so its world-seed setup
/// remains isolated too.
/// </summary>
[CollectionDefinition("Map scanner", DisableParallelization = true)]
public sealed class MapScannerCollection;
