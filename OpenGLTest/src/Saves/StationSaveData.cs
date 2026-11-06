using System.Collections.Generic;

namespace VoxelEngine.Saves;

/// <summary>
/// The JSON shape of a world's <c>stations.json</c> (<see cref="StationSaveCodec"/>): the campfires,
/// querns, kilns and cast molds with their in-progress state. Every field is optional, so an older or
/// hand-edited file degrades field by field instead of failing the load; a cell travels as
/// <c>int[3]</c>. Content is referenced by <see cref="SaveNames"/> name (blocks and items) or molten
/// material file name, never by byte id; whoever restores the data drops an entry whose name no
/// longer resolves.
/// </summary>
public sealed class StationSaveData
{
    public List<CampfireData>? Campfires { get; set; }
    public List<QuernData>? Querns { get; set; }
    public List<KilnData>? Kilns { get; set; }
    public List<CastData>? Casts { get; set; }

    /// <summary>One branch pile with a fire and/or food on it.</summary>
    public sealed class CampfireData
    {
        public int[]? Cell { get; set; }
        public bool Lit { get; set; }
        public float BurnLeft { get; set; }
        public List<CookSlotData>? Slots { get; set; }
    }

    public sealed class CookSlotData
    {
        public string? Input { get; set; }
        public string? Output { get; set; }
        public int Count { get; set; }
        public float TimeLeft { get; set; }
        public float TotalTime { get; set; }
        public bool Done { get; set; }
    }

    public sealed class QuernData
    {
        public int[]? Cell { get; set; }

        /// <summary>The waiting crushable, null when the quern is empty.</summary>
        public string? Input { get; set; }
        public float Angle { get; set; }
        public bool Spinning { get; set; }
    }

    /// <summary>One kiln structure, keyed by its anchor cell.</summary>
    public sealed class KilnData
    {
        public int[]? Cell { get; set; }
        public string? Anchor { get; set; }
        public int Facing { get; set; }
        public int Fuel { get; set; }
        public string? FuelItem { get; set; }
        public float FuelSeconds { get; set; }
        public bool Lit { get; set; }
        public float BurnLeft { get; set; }

        /// <summary>The crucible standing in the hearth, null when there is none.</summary>
        public string? Crucible { get; set; }

        /// <summary>The molten material's file name, null for an empty vessel.</summary>
        public string? Material { get; set; }
        public int MoltenMl { get; set; }
        public int PendingMl { get; set; }
        public string? PendingInput { get; set; }
        public float MeltLeft { get; set; }
    }

    /// <summary>One mold cell holding a pour, its recipe named by mold block and material.</summary>
    public sealed class CastData
    {
        public int[]? Cell { get; set; }
        public string? Mold { get; set; }
        public string? Material { get; set; }
        public int FilledMl { get; set; }
        public bool Cooling { get; set; }
        public float CoolLeft { get; set; }
        public bool Done { get; set; }
    }
}
