using System;
using System.Collections.Generic;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// Turns a parsed <see cref="TreeSpeciesDefinition"/> into an immutable <see cref="TreeSpecies"/>,
/// validating every field. Pure: block names go through the caller's resolver and the logic and
/// feature names through their code registries, and nothing here reads or writes
/// <see cref="ContentRegistry"/>, so tests can feed hand-built definitions. Every failure is an
/// <see cref="InvalidOperationException"/> that starts with <c>Tree species '&lt;path&gt;'</c>.
/// </summary>
internal static class TreeSpeciesCompiler
{
    /// <summary>Largest tip diameter in pixels: a tip never exceeds one block.</summary>
    private const int MaxTipPx = WoodState.PixelsPerBlock;

    /// <summary>Largest trunk diameter in pixels (eight blocks), room for wide trunks.</summary>
    internal const int MaxTrunkPx = 128;

    /// <summary>Largest leaf cluster radius in blocks.</summary>
    private const int MaxLeafRadius = 8;

    /// <summary>
    /// Compiles one species. <paramref name="resolveBlock"/> returns a block id for a block
    /// reference, or null when the name is not a block; the caller owns the prefix rules.
    /// </summary>
    internal static TreeSpecies Compile(
        string name, TreeSpeciesDefinition dto, string path, Func<string, byte?> resolveBlock)
    {
        // Block references.
        if (string.IsNullOrWhiteSpace(dto.Wood))
            throw Fail(path, "is missing 'wood'.");
        byte woodId = resolveBlock(dto.Wood) ?? throw Fail(path, $"wood '{dto.Wood}' is not a block.");
        if (BlockType.IsFluid(woodId))
            throw Fail(path, $"wood '{dto.Wood}' is a fluid.");

        byte leavesId = 0;
        if (dto.Leaves != null)
        {
            leavesId = resolveBlock(dto.Leaves) ?? throw Fail(path, $"leaves '{dto.Leaves}' is not a block.");
            if (leavesId == 0)
                throw Fail(path, $"leaves '{dto.Leaves}' is not a block.");
            if (leavesId == woodId)
                throw Fail(path, $"leaves '{dto.Leaves}' is the same block as its wood.");
            if (BlockType.IsFluid(leavesId))
                throw Fail(path, $"leaves '{dto.Leaves}' is a fluid.");
        }

        // Growth.
        var growth = dto.Growth ?? throw Fail(path, "is missing 'growth'.");
        if (string.IsNullOrWhiteSpace(growth.Logic))
            throw Fail(path, $"is missing 'growth.logic' (known: {GrowthLogicRegistry.NamesForError}).");
        var logic = GrowthLogicRegistry.TryGet(growth.Logic) ?? throw Fail(path,
            $"growth.logic '{growth.Logic}' is not a registered growth logic (known: {GrowthLogicRegistry.NamesForError}).");
        int signalEnergy = Require(path, "growth.signalEnergy", growth.SignalEnergy);
        if (signalEnergy < 1)
            throw Fail(path, $"growth.signalEnergy must be at least 1, got {signalEnergy}.");
        int upProbability = Require(path, "growth.upProbability", growth.UpProbability);
        if (upProbability < 0)
            throw Fail(path, $"growth.upProbability must be at least 0, got {upProbability}.");
        int lowestBranch = Require(path, "growth.lowestBranchHeight", growth.LowestBranchHeight);
        if (lowestBranch < 0)
            throw Fail(path, $"growth.lowestBranchHeight must be at least 0, got {lowestBranch}.");
        var (maturityMin, maturityMax) = Range(path, "growth.maturitySteps", growth.MaturitySteps, 1);

        // Thickness.
        var thickness = dto.Thickness ?? throw Fail(path, "is missing 'thickness'.");
        int tipPx = Require(path, "thickness.tipPx", thickness.TipPx);
        if (tipPx < 1 || tipPx > MaxTipPx)
            throw Fail(path, $"thickness.tipPx must be 1..{MaxTipPx}, got {tipPx}.");
        int maxPx = Require(path, "thickness.maxPx", thickness.MaxPx);
        if (maxPx < tipPx || maxPx > MaxTrunkPx)
            throw Fail(path, $"thickness.maxPx must be tipPx ({tipPx})..{MaxTrunkPx}, got {maxPx}.");
        float pipeExponent = Require(path, "thickness.pipeExponent", thickness.PipeExponent);
        if (!float.IsFinite(pipeExponent) || pipeExponent <= 0f)
            throw Fail(path, $"thickness.pipeExponent must be a finite number above 0, got {pipeExponent}.");
        var girth = GirthProfile(path, thickness.GirthProfile);
        var trunkShape = ParseEnum(path, "thickness.trunkShape", thickness.TrunkShape, TrunkShape.Circle);
        var rim = ParseEnum(path, "thickness.rim", thickness.Rim, TrunkRim.Block);

        // Leaf cluster: present exactly when the species has leaves.
        var leafShape = LeafClusterShape.Blob;
        int leafRadius = 0, leafHydration = 0, tipMaxPx = 0, leafMinRadius = 0, leafFullRadiusPx = 0;
        if (leavesId == 0)
        {
            if (dto.LeafCluster != null)
                throw Fail(path, "has a 'leafCluster' but no 'leaves'.");
        }
        else
        {
            var cluster = dto.LeafCluster ?? throw Fail(path, "has 'leaves' but is missing 'leafCluster'.");
            leafShape = ParseEnum(path, "leafCluster.shape", cluster.Shape, LeafClusterShape.Blob);
            leafRadius = Require(path, "leafCluster.radius", cluster.Radius);
            if (leafRadius < 1 || leafRadius > MaxLeafRadius)
                throw Fail(path, $"leafCluster.radius must be 1..{MaxLeafRadius}, got {leafRadius}.");
            leafHydration = Require(path, "leafCluster.hydration", cluster.Hydration);
            if (leafHydration < 1 || leafHydration > LeafState.MaxHydration)
                throw Fail(path, $"leafCluster.hydration must be 1..{LeafState.MaxHydration}, got {leafHydration}.");
            tipMaxPx = Require(path, "leafCluster.tipMaxPx", cluster.TipMaxPx);
            if (tipMaxPx < 1 || tipMaxPx > maxPx)
                throw Fail(path, $"leafCluster.tipMaxPx must be 1..thickness.maxPx ({maxPx}), got {tipMaxPx}.");
            leafMinRadius = cluster.MinRadius ?? leafRadius;
            if (leafMinRadius < 1 || leafMinRadius > leafRadius)
                throw Fail(path, $"leafCluster.minRadius must be 1..leafCluster.radius ({leafRadius}), got {leafMinRadius}.");
            // The world stores at most a full cube per wood cell, so the support signal caps at one block.
            var fullRadiusCap = Math.Min(maxPx, WoodState.PixelsPerBlock);
            leafFullRadiusPx = cluster.FullRadiusPx ?? WoodState.PixelsPerBlock;
            if (leafFullRadiusPx < 1 || leafFullRadiusPx > fullRadiusCap)
                throw Fail(path, $"leafCluster.fullRadiusPx must be 1..{fullRadiusCap} (min of thickness.maxPx and one block), got {leafFullRadiusPx}.");
        }

        // Features.
        var features = new List<TreeFeatureSpec>();
        if (dto.Features != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Features.Length; i++)
            {
                var entry = dto.Features[i] ?? throw Fail(path, $"features[{i}] is null.");
                if (string.IsNullOrWhiteSpace(entry.Type))
                    throw Fail(path, $"features[{i}] is missing 'type' (known: {TreeFeatureRegistry.NamesForError}).");
                var feature = TreeFeatureRegistry.TryGet(entry.Type) ?? throw Fail(path,
                    $"features[{i}].type '{entry.Type}' is not a registered tree feature (known: {TreeFeatureRegistry.NamesForError}).");
                if (!seen.Add(feature.Name))
                    throw Fail(path, $"features names '{feature.Name}' more than once.");
                int depth = entry.Depth ?? 0;
                if (depth < 0)
                    throw Fail(path, $"features[{i}].depth must be at least 0, got {depth}.");
                if (feature.Name == "roots" && depth < 1)
                    throw Fail(path, $"features[{i}] 'roots' needs a depth of at least 1, got {depth}.");
                if (feature.Name == "buttress" && depth != 0)
                    throw Fail(path, $"features[{i}] 'buttress' takes no depth (0), got {depth}.");
                // Reactive growth recognises root-level fins by height alone (TreeGraph.FromWorld),
                // which is only exact while growth never branches off the trunk at the root's level.
                if (feature.Name == "buttress" && lowestBranch < 1)
                    throw Fail(path, $"features[{i}] 'buttress' needs growth.lowestBranchHeight of at least 1 "
                        + $"(root-level nodes are read back as fins), got {lowestBranch}.");
                features.Add(new TreeFeatureSpec(feature, depth));
            }
        }

        // Far LOD.
        var lod = dto.Lod ?? throw Fail(path, "is missing 'lod'.");
        var (crownMin, crownMax) = Range(path, "lod.crownHeight", lod.CrownHeight, 0);
        int crownRadius = Require(path, "lod.crownRadius", lod.CrownRadius);
        if (crownRadius < 0)
            throw Fail(path, $"lod.crownRadius must be at least 0, got {crownRadius}.");
        var profile = ParseEnum(path, "lod.profile", lod.Profile, LodCrownProfile.Dome);

        return new TreeSpecies
        {
            Name = name,
            WoodId = woodId,
            LeavesId = leavesId,
            Logic = logic,
            SignalEnergy = signalEnergy,
            UpProbability = upProbability,
            LowestBranchHeight = lowestBranch,
            MaturityStepsMin = maturityMin,
            MaturityStepsMax = maturityMax,
            TipPx = tipPx,
            PipeExponent = pipeExponent,
            MaxPx = maxPx,
            GirthProfile = girth,
            TrunkShape = trunkShape,
            Rim = rim,
            LeafShape = leafShape,
            LeafRadius = leafRadius,
            LeafHydration = leafHydration,
            TipMaxPx = tipMaxPx,
            LeafMinRadius = leafMinRadius,
            LeafFullRadiusPx = leafFullRadiusPx,
            Features = features.ToArray(),
            CrownHeightMin = crownMin,
            CrownHeightMax = crownMax,
            CrownRadius = crownRadius,
            LodProfile = profile,
            Worldgen = dto.Worldgen == null ? null : Worldgen(path, dto.Worldgen),
        };
    }

    private static TreeWorldgenRules Worldgen(string path, TreeSpeciesDefinition.WorldgenDefinition w)
    {
        if (w.Biomes == null || w.Biomes.Length == 0)
            throw Fail(path, $"worldgen.biomes must name at least one biome (known: {BiomeNames}).");
        var biomes = new List<Biome>(w.Biomes.Length);
        foreach (var raw in w.Biomes)
        {
            if (raw == null || int.TryParse(raw, out _) || !Enum.TryParse(raw, ignoreCase: true, out Biome biome)
                || !Enum.IsDefined(biome))
                throw Fail(path, $"worldgen.biomes '{raw}' is not a biome (known: {BiomeNames}).");
            if (biomes.Contains(biome))
                throw Fail(path, $"worldgen.biomes names '{raw}' more than once.");
            biomes.Add(biome);
        }

        float density = Require(path, "worldgen.density", w.Density);
        if (!float.IsFinite(density) || density < 0f || density > 1f)
            throw Fail(path, $"worldgen.density must be 0..1, got {density}.");
        int spacing = Require(path, "worldgen.spacing", w.Spacing);
        if (spacing < 0)
            throw Fail(path, $"worldgen.spacing must be at least 0, got {spacing}.");
        int maxSlope = Require(path, "worldgen.maxSlope", w.MaxSlope);
        if (maxSlope < 0)
            throw Fail(path, $"worldgen.maxSlope must be at least 0, got {maxSlope}.");
        int minSoil = w.MinSoil ?? 0;
        if (minSoil < 0)
            throw Fail(path, $"worldgen.minSoil must be at least 0, got {minSoil}.");
        int minGround = Require(path, "worldgen.minGround", w.MinGround);
        int maxGround = Require(path, "worldgen.maxGround", w.MaxGround);
        if (minGround > maxGround)
            throw Fail(path, $"worldgen.minGround ({minGround}) is above worldgen.maxGround ({maxGround}).");
        float openness = w.Openness ?? 1f;
        if (!float.IsFinite(openness) || openness < 0f || openness > 1f)
            throw Fail(path, $"worldgen.openness must be 0..1, got {openness}.");

        return new TreeWorldgenRules
        {
            Biomes = biomes.ToArray(),
            Oasis = w.Oasis ?? false,
            Density = density,
            Spacing = spacing,
            MaxSlope = maxSlope,
            MinSoil = minSoil,
            MinGround = minGround,
            MaxGround = maxGround,
            Openness = openness,
            SpawnClear = w.SpawnClear ?? true,
        };
    }

    private static (float Height01, float Multiplier)[] GirthProfile(string path, float[][]? raw)
    {
        if (raw == null) return [(0f, 1f)];
        if (raw.Length == 0)
            throw Fail(path, "thickness.girthProfile must have at least one [height01, multiplier] entry.");
        var result = new (float, float)[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var entry = raw[i];
            if (entry == null || entry.Length != 2)
                throw Fail(path, $"thickness.girthProfile[{i}] must be exactly [height01, multiplier].");
            float h = entry[0], m = entry[1];
            if (!float.IsFinite(h) || h < 0f || h > 1f)
                throw Fail(path, $"thickness.girthProfile[{i}] height must be 0..1, got {h}.");
            if (i == 0 && h != 0f)
                throw Fail(path, $"thickness.girthProfile must start at height 0, got {h}.");
            if (i > 0 && h <= result[i - 1].Item1)
                throw Fail(path, $"thickness.girthProfile heights must strictly increase, got {h} after {result[i - 1].Item1}.");
            if (!float.IsFinite(m) || m <= 0f)
                throw Fail(path, $"thickness.girthProfile[{i}] multiplier must be above 0, got {m}.");
            result[i] = (h, m);
        }
        return result;
    }

    private static (int Min, int Max) Range(string path, string field, int[]? raw, int lowest)
    {
        if (raw == null)
            throw Fail(path, $"is missing '{field}' ([min, max]).");
        if (raw.Length != 2)
            throw Fail(path, $"{field} must be exactly two numbers [min, max], got {raw.Length}.");
        int min = raw[0], max = raw[1];
        if (min < lowest)
            throw Fail(path, $"{field} min must be at least {lowest}, got {min}.");
        if (min > max)
            throw Fail(path, $"{field} min ({min}) is above max ({max}).");
        return (min, max);
    }

    private static T Require<T>(string path, string field, T? value) where T : struct =>
        value ?? throw Fail(path, $"is missing '{field}'.");

    private static TEnum ParseEnum<TEnum>(string path, string field, string? raw, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (raw == null) return fallback;
        // Enum.TryParse also accepts numbers; only the names are valid in content.
        if (!int.TryParse(raw, out _) && Enum.TryParse(raw, ignoreCase: true, out TEnum value) && Enum.IsDefined(value))
            return value;
        throw Fail(path, $"{field} '{raw}' is not valid (known: {string.Join(", ", LowerNames<TEnum>())}).");
    }

    private static IEnumerable<string> LowerNames<TEnum>() where TEnum : struct, Enum
    {
        foreach (var n in Enum.GetNames<TEnum>()) yield return n.ToLowerInvariant();
    }

    private static string BiomeNames => string.Join(", ", LowerNames<Biome>());

    private static InvalidOperationException Fail(string path, string message) =>
        new($"Tree species '{path}' {message}");
}
