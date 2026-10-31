namespace VoxelEngine.WorldGen;

/// <summary>
/// One disc of a great lake's outline (<see cref="GreatLakeSite"/>), or its island: the center in world
/// blocks and the nominal radius, before <see cref="GreatLakes.Sample"/>
/// pulls the outline inward with the shape noise. A <see cref="Radius"/> of 0 (the <c>default</c>)
/// means the lobe or island is absent.
/// </summary>
internal readonly record struct GreatLakeLobe(float X, float Z, float Radius);
