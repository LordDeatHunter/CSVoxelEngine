using System;
using System.Collections.Generic;
using System.IO;

namespace VoxelEngine.Audio;

/// <summary>
/// Maps the loose <c>.wav</c> files under a folder onto sound groups. A group is the file name
/// with its trailing <c>_N</c> variant index stripped: <c>hit_wood_1</c>, <c>hit_wood_2</c> and
/// <c>hit_wood_3</c> all belong to <c>hit_wood</c>, while <c>hit_default</c> (no numeric suffix)
/// is a group of one. <see cref="SfxManager"/> plays a random clip of a group; the content
/// registry validates block <c>sfx</c> names against the same scan, so authoring a group is
/// just dropping files in the folder. GL-free and device-free, so tests cover it headless.
/// </summary>
public static class SfxLibrary
{
    /// <summary>Group played when a block names no <c>sfx</c> or names an unknown one.</summary>
    public const string DefaultGroup = "hit_default";

    /// <summary>Stone-family hit group (stone, cobblestone, ore, gravel, ...).</summary>
    public const string StoneGroup = "hit_rock";

    /// <summary>Wood-family hit group (log, branch, limb, ...).</summary>
    public const string WoodGroup = "hit_wood";

    /// <summary>Group played when the player collects an item drop.</summary>
    public const string PickupGroup = "item_pickup";

    /// <summary>Group played when the player equips an item on the hotbar or wears gear.</summary>
    public const string EquipGroup = "equip";

    /// <summary>Group played when the player takes worn gear off.</summary>
    public const string UnequipGroup = "unequip";

    /// <summary>Group played when a chest lid starts to swing open.</summary>
    public const string ChestOpenGroup = "chest_open";

    /// <summary>Group played when a chest lid starts to swing shut.</summary>
    public const string ChestCloseGroup = "chest_close";

    /// <summary>Group played when a battle tower's floor is cleared and its door opens.</summary>
    public const string FloorClearGroup = "floor_clear";

    /// <summary>Group played when a staff casts a spell (the caster's own cast).</summary>
    public const string SpellCastGroup = "spell_cast";

    /// <summary>Group played where a spell bolt lands near the camera.</summary>
    public const string SpellImpactGroup = "spell_impact";

    /// <summary>Group played when the caster's own spell fizzles.</summary>
    public const string SpellFizzleGroup = "spell_fizzle";

    /// <summary>Group played when a hit lands on a skeleton.</summary>
    public const string SkeletonHitGroup = "skeleton_hit";

    /// <summary>Group played when a hit kills a skeleton.</summary>
    public const string SkeletonDeathGroup = "skeleton_death";

    /// <summary>Group played ambiently from time to time by a skeleton near the camera.</summary>
    public const string SkeletonRattleGroup = "skeleton_rattle";

    /// <summary>Group played when a hit lands on a slime.</summary>
    public const string SlimeHitGroup = "slime_hit";

    /// <summary>Group played when a hit kills a slime.</summary>
    public const string SlimeDeathGroup = "slime_death";

    /// <summary>Group played when a slime launches a hop (a random clip: slime_jump_1 or _2).</summary>
    public const string SlimeJumpGroup = "slime_jump";

    /// <summary>Group played when a slime lands on the ground after a hop or a fall.</summary>
    public const string SlimeLandGroup = "slime_land";

    /// <summary>Group played when a hit lands on a bug (spider, scorpion, dragonfly), and when it dies: a squish.</summary>
    public const string BugSquishGroup = "squish";

    /// <summary>Group played when a hit lands on a crow.</summary>
    public const string CrowHitGroup = "crow_hit";

    /// <summary>Group played when a hit kills a crow.</summary>
    public const string CrowDeathGroup = "crow_death";

    /// <summary>Group played ambiently from time to time by a crow near the camera.</summary>
    public const string CrowCawGroup = "crow_caw";

    /// <summary>Group played when a blade (slice, sword or axe chop) starts a swing: a swoosh.</summary>
    public const string SwooshGroup = "swoosh";

    /// <summary>
    /// Every group directly under <paramref name="root"/> (subfolders are ignored, so the
    /// ambience tracks in <c>ambience/</c> never become hit groups). Clips are file-name
    /// sorted, which makes the random picker's output reproducible in tests.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Scan(string root)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (Directory.Exists(root))
        {
            var paths = Directory.GetFiles(root, "*.wav", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var group = GroupOf(Path.GetFileNameWithoutExtension(path));
                if (!groups.TryGetValue(group, out var clips))
                    groups[group] = clips = [];
                clips.Add(path);
            }
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (group, clips) in groups)
            result[group] = clips;
        return result;
    }

    /// <summary>
    /// Group a <c>.wav</c> file (without extension) belongs to: strips a trailing
    /// <c>_&lt;digits&gt;</c> variant index, otherwise keeps the whole name. So
    /// <c>hit_rock_2</c> is <c>hit_rock</c> and <c>hit_default</c> is <c>hit_default</c>.
    /// </summary>
    public static string GroupOf(string fileNameWithoutExtension)
    {
        var underscore = fileNameWithoutExtension.LastIndexOf('_');
        if (underscore > 0 && underscore < fileNameWithoutExtension.Length - 1
            && int.TryParse(fileNameWithoutExtension.AsSpan(underscore + 1), out _))
            return fileNameWithoutExtension[..underscore];
        return fileNameWithoutExtension;
    }
}
