using VoxelEngine.Content.Quests;

namespace VoxelEngine.UI.Quests;

/// <summary>The quest book's and the toast's fixed strings and the requirement lines. GL-free.</summary>
internal static class QuestText
{
    public const string Title = "QUESTS";
    public const string Hint = "Drag to pan  -  Wheel to zoom  -  Click a quest for details  -  [P] to close";
    public const string ToastHeading = "Quest Complete!";
    public const string Complete = "Complete";
    public const string InProgress = "In progress";
    public const string Requirements = "Requirements";
    public const string Hidden = "The requirements are a secret until you complete it.";

    /// <summary>"12 / 30 complete" for the header.</summary>
    public static string Tally(int complete, int total) => $"{complete} / {total} complete";

    /// <summary>The requirement's line, "Obtain 4 Oak Log", "Kill a Slime".</summary>
    public static string Line(QuestRequirement requirement)
    {
        var verb = requirement.Kind switch
        {
            QuestRequirementKind.Obtain => "Obtain",
            QuestRequirementKind.Craft => "Craft",
            QuestRequirementKind.Break => "Break",
            QuestRequirementKind.Place => "Place",
            _ => "Kill"
        };
        var amount = requirement.Count == 1 ? (requirement.Kind == QuestRequirementKind.Kill ? "a " : "") : $"{requirement.Count} ";
        return $"{verb} {amount}{requirement.TargetName}";
    }

    /// <summary>"3/10" progress for a requirement.</summary>
    public static string Progress(int have, int count) => $"{have}/{count}";
}
