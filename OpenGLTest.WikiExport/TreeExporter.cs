using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WikiExport;

/// <summary>
/// Grows each tree species step by step over flat ground, exactly like the item debugger's TREES tab
/// (the default seed, a root at (0, 1, 0), features from the maturity step on), and records the
/// per-step cell differences the wiki's growth slider replays.
/// </summary>
public static class TreeExporter
{
    /// <summary>The flat ground's top solid block; the root stands one above it.</summary>
    public const int GroundY = 0;

    /// <summary>The species' cells at every step 0..<see cref="TreeSpecies.MaturityStepsMax"/>, as diffs.</summary>
    public static List<TreeStepDiff.Step> GrowSteps(TreeSpecies species, int seed, out int maturity)
    {
        maturity = TreeGrower.MaturityStepsAt(species, 0, GroundY + 1, 0, seed);
        var steps = new List<TreeStepDiff.Step>();
        var previous = new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell>();
        for (var step = 0; step <= species.MaturityStepsMax; step++)
        {
            var current = CellsAt(species, seed, step, maturity);
            steps.Add(TreeStepDiff.Between(previous, current));
            previous = current;
        }
        return steps;
    }

    /// <summary>The rasterized cells of the tree grown <paramref name="step"/> pulses.</summary>
    public static Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> CellsAt(TreeSpecies species, int seed, int step, int maturity)
    {
        // A fresh space per step: the grower and rasterizer must never see another step's cells
        var space = new VirtualTreeSpace(static (_, _) => GroundY);
        var graph = TreeGrower.GrowSteps(species, 0, GroundY + 1, 0, space, seed, step, applyFeatures: step >= maturity);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out _);
        return TreeStepDiff.ToMap(cells, species.LeavesId);
    }

    /// <summary>Writes <c>outDir/species.json</c> for every species and returns how many.</summary>
    public static int WriteAll(string outDir, int seed)
    {
        Directory.CreateDirectory(outDir);
        var count = 0;
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var steps = GrowSteps(species, seed, out var maturity);
            using var stream = File.Create(Path.Combine(outDir, species.Name + ".json"));
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteString("species", species.Name);
            json.WriteNumber("seed", seed);
            json.WriteNumber("maturity", maturity);
            json.WriteNumber("maxStep", species.MaturityStepsMax);
            json.WriteStartArray("steps");
            foreach (var step in steps)
            {
                json.WriteStartObject();
                WriteInts(json, "set", step.Set);
                WriteInts(json, "del", step.Del);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            count++;
        }
        return count;
    }

    private static void WriteInts(Utf8JsonWriter json, string name, int[] values)
    {
        json.WriteStartArray(name);
        foreach (var v in values) json.WriteNumberValue(v);
        json.WriteEndArray();
    }
}
