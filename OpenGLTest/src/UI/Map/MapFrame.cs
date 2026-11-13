using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Map;

namespace VoxelEngine.UI.Map;

/// <summary>What the UI pass draws the maps from in one frame: the world's tiles, the frame's pins, the player's world
/// XZ the minimap centers on, and the [M] screen's state.</summary>
internal readonly record struct MapFrame(MapTracker Tracker, IReadOnlyList<MapPin> Pins, Vector2 Focus, MapScreenView Screen);
