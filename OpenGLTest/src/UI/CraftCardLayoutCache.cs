using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Memoises the crafting card's <see cref="CraftCardLayout"/>, so the word wrap and every text
/// measurement behind it run only when something on the card actually changed: the canvas size,
/// the open recipe or the status line. The previous layout is also what
/// <see cref="UIRenderer.CraftHitAt"/> hit-tests (<see cref="Current"/>), so a click always reads
/// the geometry that was drawn.
///
/// <para>Three properties the callers' correctness rests on:</para>
/// <list type="bullet">
/// <item><b>Render-thread only, no locking.</b> The only caller is <see cref="UIRenderer"/>, which
/// runs on the render thread (AGENTS.md), so these are plain fields — exactly as
/// <see cref="UITextCache"/> is.</item>
/// <item><b>Only the layout is cached, never the draw.</b> A hit skips wrapping and measuring and
/// nothing else: the card is still drawn from the returned layout every frame.</item>
/// <item><b>Float keys are compared bit-for-bit</b> through
/// <see cref="BitConverter.SingleToInt32Bits(float)"/>, following <see cref="UITextCache"/>, so a
/// hit proves the rebuild would have produced the same geometry.</item>
/// </list>
///
/// <para>The measure delegates and the "Batch" label width are bound once at construction, so no
/// delegate is allocated per frame and neither is part of the key. The status string is the only
/// input that changes often; an ordinal compare of it is far cheaper than re-measuring.</para>
/// </summary>
internal sealed class CraftCardLayoutCache(TextMeasure measureHead, TextMeasure measureBody, float batchLabelWidth)
{
    private readonly TextMeasure _measureHead = measureHead;
    private readonly TextMeasure _measureBody = measureBody;
    private readonly float _batchLabelWidth = batchLabelWidth;

    private CraftCardLayout? _current;
    private int _screenXBits, _screenYBits;
    private CraftingRecipe? _recipe;
    private string? _status;

    /// <summary>The layout last built (what was drawn), or null; the hit-test reads it.</summary>
    public CraftCardLayout? Current => _current;

    /// <summary>
    /// The layout for <paramref name="recipe"/> on a <paramref name="screen"/>-sized canvas with
    /// <paramref name="status"/> as its status line; the previous instance when all three match.
    /// </summary>
    public CraftCardLayout Get(Vector2 screen, CraftingRecipe recipe, string status)
    {
        var xBits = BitConverter.SingleToInt32Bits(screen.X);
        var yBits = BitConverter.SingleToInt32Bits(screen.Y);
        if (_current != null && xBits == _screenXBits && yBits == _screenYBits
            && ReferenceEquals(_recipe, recipe) && string.Equals(_status, status, StringComparison.Ordinal))
            return _current;

        // The single definition of the card's texts.
        var title = Inventory.DisplayName(recipe.ResultId);
        var makes = $"Makes {recipe.ResultCount} per craft";
        var labels = new string[recipe.Ingredients.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var ing = recipe.Ingredients[i];
            var name = ing.IsTag ? $"Any {ing.Tag}" : Inventory.DisplayName(ing.Id);
            // Double space deliberate, as the card has always drawn it.
            labels[i] = $"{i + 1}. {name}  x{ing.Count}/craft";
        }

        _current = CraftCardLayout.Build(screen, recipe, title, makes, labels, status, _batchLabelWidth,
            _measureHead, _measureBody);
        _screenXBits = xBits;
        _screenYBits = yBits;
        _recipe = recipe;
        _status = status;
        return _current;
    }

    /// <summary>Forgets the layout, so a stale one is never hit-tested while no recipe is open.</summary>
    public void Clear()
    {
        _current = null;
        _recipe = null;
    }
}
