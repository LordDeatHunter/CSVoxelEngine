namespace VoxelEngine.Content.Recipes;

/// <summary>One compiled work-station recipe: so many of one input stacked on a work station whose recipe folder is
/// <see cref="Station"/> (an anvil's <c>forge</c>, a fleshing beam's <c>scrape</c>, a leather table's <c>cut</c>), picked
/// from its list and worked <see cref="Hits"/> times with its tool, make the result and its byproduct
/// (<see cref="Simulation.WorkStations.WorkStationManager.TryHit"/>). One recipe per result, across every station.</summary>
public sealed class WorkRecipe
{
    public WorkRecipe(string name, string station, ushort inputId, int inputCount, ushort resultId, int resultCount,
        int hits, ushort byproductId = 0, int byproductCount = 0, ushort collectWithId = 0)
    {
        Name = name;
        Station = station;
        InputId = inputId;
        InputCount = inputCount;
        ResultId = resultId;
        ResultCount = resultCount;
        Hits = hits;
        ByproductId = byproductId;
        ByproductCount = byproductCount;
        CollectWithId = collectWithId;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>The recipe folder it was loaded from, which a station's profile names
    /// (<see cref="Stations.WorkStationProfile.Recipes"/>).</summary>
    public string Station { get; }

    /// <summary>The input the station must hold.</summary>
    public ushort InputId { get; }

    /// <summary>How many of the input the work takes off the station.</summary>
    public int InputCount { get; }

    /// <summary>What the work makes.</summary>
    public ushort ResultId { get; }

    /// <summary>How many of the result one work makes.</summary>
    public int ResultCount { get; }

    /// <summary>Tool hits the work takes once the recipe is picked.</summary>
    public int Hits { get; }

    /// <summary>What the work leaves besides the result (a bloom's slag), 0 for nothing.</summary>
    public ushort ByproductId { get; }

    /// <summary>How many of <see cref="ByproductId"/> one work leaves.</summary>
    public int ByproductCount { get; }

    /// <summary>The item a finished result waits on the station for (a scraped hide waits for salt): a right-click with
    /// it spends one and hands one result over. 0 when the result drops at once.</summary>
    public ushort CollectWithId { get; }
}
