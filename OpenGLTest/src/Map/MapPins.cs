using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Networking.Client;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Map;

/// <summary>What a map pin stands for, which picks its icon and color.</summary>
public enum MapPinKind : byte
{
    /// <summary>The local player: the arrow the map is centered on.</summary>
    Self,
    Player,
    Hostile,
    Neutral,
    Waystone,

    /// <summary>A battle tower the player marked with a tower map.</summary>
    Tower,
}

/// <summary>One pin: its kind, world XZ, compass heading in degrees (0 north, clockwise; arrows only) and the name a
/// waystone shows under the pointer.</summary>
public readonly record struct MapPin(MapPinKind Kind, Vector2 Position, float Heading, string? Label = null);

/// <summary>
/// Builds the frame's map pins: the local player, every other player, every live mob (hostile or neutral by its kind's
/// <see cref="EntityType.Attitude"/>), every waystone the player has discovered and every battle tower they marked with
/// a tower map (<see cref="TowerMarks"/>). On a cave slice only the mobs in the
/// slice's height band show (<see cref="ShowsMobAt"/>), so the surface's animals never crowd a cave. The waystone and
/// tower pins are rebuilt only when their list's version moves. GL-free; main thread only.
/// </summary>
public sealed class MapPins
{
    private readonly List<MapPin> _pins = [];
    private readonly List<MapPin> _places = [];
    private DiscoveredWaystones? _waystoneSource;
    private int _waystoneVersion = -1;
    private TowerMarks? _towerSource;
    private int _towerVersion = -1;

    /// <summary>The name a tower pin shows under the pointer.</summary>
    public const string TowerLabel = "Battle tower";

    /// <summary>The pins of the last <see cref="Collect"/>: the waystones and towers first, then the mobs, the other players and
    /// the local player last, so the arrows draw over the icons and the player's own over everything.</summary>
    public IReadOnlyList<MapPin> Pins => _pins;

    /// <summary>The pin kind of a mob of kind <paramref name="type"/>.</summary>
    public static MapPinKind KindOf(EntityType type) =>
        type.Attitude == EntityAttitude.Neutral ? MapPinKind.Neutral : MapPinKind.Hostile;

    /// <summary>A mob's compass heading from its <see cref="Mob.Facing"/> (radians, the model facing -Z at 0 and
    /// turning toward -X): north is 0 and east is 90.</summary>
    public static float HeadingOfFacing(float facing) => (-facing * (180f / System.MathF.PI) % 360f + 360f) % 360f;

    /// <summary>Whether a mob at height <paramref name="y"/> belongs on <paramref name="layer"/>: anywhere over the
    /// surface or the sky band, but on a cave slice only from its floor to a little above its top.</summary>
    public static bool ShowsMobAt(MapLayer layer, float y) =>
        layer.Kind != MapLayerKind.Cave || (y >= layer.BottomY && y <= layer.TopY + MapConfig.CaveSliceStep);

    public void Collect(MapLayer layer, Vector3 eye, float yaw, IReadOnlyList<RemotePlayer> players,
        IReadOnlyList<Mob> mobs, DiscoveredWaystones waystones, TowerMarks towers)
    {
        _pins.Clear();
        RefreshPlaces(waystones, towers);
        _pins.AddRange(_places);
        foreach (var mob in mobs)
        {
            if (mob.IsDead || !ShowsMobAt(layer, mob.Position.Y)) continue;
            _pins.Add(new MapPin(KindOf(mob.Type), new Vector2(mob.Position.X, mob.Position.Z),
                HeadingOfFacing(mob.Facing)));
        }
        foreach (var player in players)
            _pins.Add(new MapPin(MapPinKind.Player, new Vector2(player.Eye.X, player.Eye.Z),
                Compass.HeadingFromYaw(player.Yaw), player.Name));
        _pins.Add(new MapPin(MapPinKind.Self, new Vector2(eye.X, eye.Z), Compass.HeadingFromYaw(yaw)));
    }

    // The pins that only move when a list does: the discovered waystones and the marked towers
    private void RefreshPlaces(DiscoveredWaystones waystones, TowerMarks towers)
    {
        if (ReferenceEquals(waystones, _waystoneSource) && waystones.Version == _waystoneVersion
            && ReferenceEquals(towers, _towerSource) && towers.Version == _towerVersion) return;
        _waystoneSource = waystones;
        _waystoneVersion = waystones.Version;
        _towerSource = towers;
        _towerVersion = towers.Version;
        _places.Clear();
        // A pin sits on the middle of the stone's cell, or of the tower's center column
        _places.AddRange(waystones.Entries.Select(e =>
            new MapPin(MapPinKind.Waystone, new Vector2(e.X + 0.5f, e.Z + 0.5f), 0f, e.Name)));
        foreach (var (x, z) in towers.Towers)
            _places.Add(new MapPin(MapPinKind.Tower, new Vector2(x + 0.5f, z + 0.5f), 0f, TowerLabel));
    }
}
