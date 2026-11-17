namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/&lt;station&gt;/&lt;result&gt;.json</c>, where the folder is one a work
/// station's profile names (<c>forge</c> for the anvil, <c>scrape</c> for the fleshing beam, <c>cut</c> for the leather
/// table): what the station's tool makes from a stack of one input laid on it. The recipe is picked from the station's
/// list, then worked <c>hits</c> times.</summary>
public sealed class WorkRecipeDefinition
{
    /// <summary>The input stacked on the station, e.g. <c>item/bloom</c>.</summary>
    public string? Input { get; set; }

    /// <summary>How many of the input one work takes off the station; required, 1..64.</summary>
    public int? InputCount { get; set; }

    /// <summary>The result, e.g. <c>item/iron_axe_head</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one work makes; default 1.</summary>
    public int? Count { get; set; }

    /// <summary>Tool hits the work takes once picked; required, 1..64.</summary>
    public int? Hits { get; set; }

    /// <summary>What the work leaves besides the result (a bloom's slag); optional.</summary>
    public ByproductDefinition? Byproduct { get; set; }

    /// <summary>An item the finished result waits on the station for (<c>item/salt</c>): a right-click with it spends
    /// one and hands one result over. Optional; without it the result drops at once.</summary>
    public string? CollectWith { get; set; }

    /// <summary>The <c>"byproduct"</c> object: <c>{"item": "item/slag", "count": 3}</c>.</summary>
    public sealed class ByproductDefinition
    {
        /// <summary>The byproduct item; required.</summary>
        public string? Item { get; set; }

        /// <summary>How many one work leaves; default 1.</summary>
        public int? Count { get; set; }
    }
}
