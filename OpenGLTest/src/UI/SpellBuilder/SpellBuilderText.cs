using VoxelEngine.Content.Spells;

namespace VoxelEngine.UI.SpellBuilder;

/// <summary>The spell builder's fixed strings: the title, the no-staff hint and a phrase per recipe verdict.</summary>
public static class SpellBuilderText
{
    /// <summary>The panel's title.</summary>
    public const string Title = "Spell Table";

    /// <summary>Shown when the table is used, or the panel drawn, without a staff in hand.</summary>
    public const string HoldStaffHint = "Hold a staff to use the spell table";

    /// <summary>A readable phrase for <paramref name="verdict"/>.</summary>
    public static string Verdict(SpellVerdict verdict) => verdict switch
    {
        SpellVerdict.Valid => "Ready to bind",
        SpellVerdict.MissingElement => "Needs an element",
        SpellVerdict.TooManyElements => "Only one element",
        SpellVerdict.MissingShape => "Needs a shape",
        SpellVerdict.TooManyShapes => "Only one shape",
        SpellVerdict.TooManyTriggers => "Only one trigger",
        SpellVerdict.DuplicateModifier => "Modifier used twice",
        SpellVerdict.DisallowedPart => "This staff cannot hold a part",
        _ => verdict.ToString(),
    };
}
