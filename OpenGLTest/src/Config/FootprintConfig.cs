namespace VoxelEngine;

/// <summary>
/// Footprint tuning (<c>FootprintSystem</c>, <c>FootprintRules</c>, <c>FootprintRenderer</c>): how clear a
/// print is and how long it lasts by default (a block's <c>footprintVisibility</c> and
/// <c>footprintPersistence</c> override them), the print's size and the tint it darkens the ground with.
/// </summary>
public static class FootprintConfig
{
    // A hard surface: a faint print that is gone in seconds
    public const float DefaultVisibility = 0.1f;
    public const float DefaultPersistenceSeconds = 3f;

    // The last share of a print's life it fades out over; before that it holds its full visibility
    public const float FadeShare = 0.6f;

    // Most prints alive at once; the oldest goes first
    public const int Max = 256;

    // The print's footprint on the ground, and how far each foot sits off the walking line
    public const float Length = 0.34f;
    public const float Width = 0.2f;
    public const float FootSpread = 0.12f;

    // Lift off the surface, so the quad never z-fights the ground it lies on
    public const float Lift = 0.012f;

    // Prints further than this from the camera are neither drawn nor kept
    public const float DrawRangeBlocks = 48f;

    // How many prints a frame checks that the block under them is still there
    public const int SupportChecksPerFrame = 16;

    // The sRGB tint multiplied into the print sprite (the sprite's alpha is the sole shape)
    public const byte TintR = 52;
    public const byte TintG = 44;
    public const byte TintB = 40;
}
