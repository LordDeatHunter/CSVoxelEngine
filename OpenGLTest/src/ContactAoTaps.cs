namespace silkgltriangle;

/// <summary>The nine raw window depths the contact AO pass reads around one pixel
/// (<see cref="ContactAo.Occlusion"/>): the centre, its four taps a tap radius away and its four
/// 1-pixel neighbours, which give the centre surface's own slope.</summary>
public readonly record struct ContactAoTaps(
    float Centre, float Left, float Right, float Down, float Up,
    float Left1, float Right1, float Down1, float Up1);
