namespace VoxelEngine.Effects.Post;

/// <summary>The nine raw window depths the contact AO pass reads around one pixel
/// (<see cref="ContactAo.Occlusion"/>): the center, its four taps a tap radius away and its four
/// 1-pixel neighbors, which give the center surface's own slope.</summary>
public readonly record struct ContactAoTaps(
    float Center, float Left, float Right, float Down, float Up,
    float Left1, float Right1, float Down1, float Up1);
