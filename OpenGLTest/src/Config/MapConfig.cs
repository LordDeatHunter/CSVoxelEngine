namespace VoxelEngine.Config;

/// <summary>
/// Map tuning: which slice the map shows (the cave slice's probe, step and depth, the layer switch delay), how much
/// of the world it scans and keeps, the tile colors it shades with, the top-right minimap's size and scale, and the
/// [M] map screen's zoom range and pins.
/// </summary>
public static class MapConfig
{
    // --- Layer choice (MapLayer, MapLayerSelector) ---
    // Cells above the eye searched for an occluding block: one there means the player is under a roof (a cave, a
    // house) and the map shows the cave slice at head height instead of the surface
    public const int RoofProbeCells = 24;
    // The cave slice's top follows the eye in steps of this many cells, so walking a slope rescans it rarely
    public const int CaveSliceStep = 4;
    // Cells above the eye's cell the cave slice starts at (a floor one block up stays visible)
    public const int CaveSliceHeadroom = 2;
    // How deep the cave slice looks below its top for a floor before giving up (a pit)
    public const int CaveScanDepth = 32;
    // The sky layer takes over once the eye is this far below the lowest sky island cell
    public const int SkyLayerMargin = 24;
    // How long a different layer must hold before the map switches to it, so a cave mouth or a doorway never flickers
    public const float LayerSwitchSeconds = 0.6f;

    // --- Scanning (MapTracker) ---
    // Chunk columns around the player kept scanned; the streaming radius, so the map shows everything loaded
    public const int ScanRadiusChunks = StreamingConfig.ChunkRadius;
    // Tiles (chunk columns) rescanned per frame at most, nearest first
    public const int TilesPerFrame = 12;
    // Explored tiles kept for the session at most; past it the farthest go first
    public const int MaxTiles = 16384;

    // --- Tile colors (MapColumnScanner), sRGB ---
    // Brightness change per cell a column stands above (or below) its northern neighbor, and the step cap
    public const float ReliefStep = 0.07f;
    public const int ReliefMaxSteps = 3;
    // Fluid darkens per cell of depth to its floor, down to this share of its color, counting at most MaxFluidDepth
    public const float FluidDepthDarken = 0.06f;
    public const float FluidMinShade = 0.5f;
    public const int MaxFluidDepth = 8;
    // A cave slice that starts inside rock, and one that finds no floor within its depth
    public static readonly (byte R, byte G, byte B) RockColor = (34, 32, 30);
    public static readonly (byte R, byte G, byte B) PitColor = (8, 8, 10);
    // A cave floor darkens per cell it lies below the slice top, down to this share
    public const float CaveDepthDarken = 0.025f;
    public const float CaveMinShade = 0.45f;

    // --- Minimap (MinimapRenderer, MapLayout) ---
    // Side (square) or diameter (round) in canvas units, its gap from the top-right corner and its frame
    public const float MinimapSize = 200f;
    public const float MinimapMargin = 14f;
    public const float MinimapFrame = 3f;
    // Canvas units per block: 200 units show 100 blocks across
    public const float MinimapScale = 2f;

    // --- Map screen (MapScreenRenderer, MapScreenView) ---
    // Canvas units per block: the zoom range, the default (CENTER) and the factor one wheel notch multiplies it by
    public const float MinZoom = 0.25f;
    public const float MaxZoom = 8f;
    public const float DefaultZoom = 2f;
    public const float ZoomStep = 1.2f;
    // Canvas units the pointer travels with the button down before a press becomes a pan
    public const float DragThreshold = 6f;

    // --- Pins ---
    // Arrow and waystone icon sizes in canvas units, on the minimap and on the map screen
    public const float PlayerPinSize = 16f;
    public const float MobPinSize = 11f;
    public const float WaystonePinSize = 16f;
    // A pointer within this many canvas units of a waystone pin names it on the map screen
    public const float WaystoneHoverRadius = 12f;
}
