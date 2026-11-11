using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.UI;
using VoxelEngine.UI.SpellBuilder;
using Xunit;

namespace VoxelEngine.Tests.UI.SpellBuilder;

/// <summary>The spell builder's geometry: every element is hit where it is drawn, and none overlaps another.</summary>
public sealed class SpellBuilderLayoutTests
{
    public static IEnumerable<object[]> Canvases =>
    [
        [UiCanvas.ReferenceSize.X, UiCanvas.ReferenceSize.Y],
        [2560f, 1080f],
    ];

    private static bool Overlap((Vector2 Center, Vector2 Size) a, (Vector2 Center, Vector2 Size) b) =>
        float.Abs(a.Center.X - b.Center.X) * 2f < a.Size.X + b.Size.X
        && float.Abs(a.Center.Y - b.Center.Y) * 2f < a.Size.Y + b.Size.Y;

    private static bool Inside((Vector2 Center, Vector2 Size) inner, (Vector2 Center, Vector2 Size) outer) =>
        SpellBuilderLayout.Contains(outer, inner.Center - inner.Size * 0.5f)
        && SpellBuilderLayout.Contains(outer, inner.Center + inner.Size * 0.5f);

    [Theory]
    [MemberData(nameof(Canvases))]
    public void EveryElementIsHitAtItsCenterAndNothingOverlaps(float width, float height)
    {
        var canvas = new Vector2(width, height);
        const int rows = SpellBuilderLayout.MaxRowsPerColumn;
        var panel = SpellBuilderLayout.PanelRect(canvas);
        var columns = SpellBuilderCatalog.Columns.Length;

        foreach (var slotCount in new[] { 1, 2, SpellConfig.StaffMaxSlots })
        {
            var rects = new List<(Vector2 Center, Vector2 Size)>();
            for (var slot = 0; slot < slotCount; slot++)
            {
                var rect = SpellBuilderLayout.SlotRect(slot, slotCount, canvas);
                Assert.Equal(new SpellBuilderHit(SpellBuilderHitKind.Slot, Index: slot),
                    SpellBuilderLayout.HitAt(rect.Center, canvas, _ => rows, slotCount));
                rects.Add(rect);
            }

            for (var column = 0; column < columns; column++)
            {
                for (var row = 0; row < rows; row++)
                {
                    var rect = SpellBuilderLayout.PartRect(column, row, canvas);
                    Assert.Equal(new SpellBuilderHit(SpellBuilderHitKind.Part, column, row),
                        SpellBuilderLayout.HitAt(rect.Center, canvas, _ => rows, slotCount));
                    rects.Add(rect);
                }
            }

            var bind = SpellBuilderLayout.BindRect(canvas);
            var clear = SpellBuilderLayout.ClearRect(canvas);
            Assert.Equal(SpellBuilderHitKind.Bind, SpellBuilderLayout.HitAt(bind.Center, canvas, _ => rows, slotCount).Kind);
            Assert.Equal(SpellBuilderHitKind.Clear, SpellBuilderLayout.HitAt(clear.Center, canvas, _ => rows, slotCount).Kind);
            rects.Add(bind);
            rects.Add(clear);

            for (var i = 0; i < rects.Count; i++)
            {
                Assert.True(Inside(rects[i], panel));
                for (var j = i + 1; j < rects.Count; j++)
                    Assert.False(Overlap(rects[i], rects[j]), $"rects {i} and {j} overlap");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Canvases))]
    public void OutsideThePanelIsNoHit(float width, float height)
    {
        var canvas = new Vector2(width, height);
        var panel = SpellBuilderLayout.PanelRect(canvas);
        var outside = panel.Center + new Vector2(panel.Size.X * 0.5f + 5f, 0f);

        Assert.Equal(SpellBuilderHitKind.None, SpellBuilderLayout.HitAt(outside, canvas, _ => 3, 2).Kind);
        Assert.Equal(SpellBuilderHitKind.None, SpellBuilderLayout.HitAt(Vector2.Zero, canvas, _ => 3, 2).Kind);
    }

    [Fact]
    public void ARowPastTheColumnsPartsIsNoPartHit()
    {
        var canvas = UiCanvas.ReferenceSize;
        var shownRows = new[] { 2, 0, 1, SpellBuilderLayout.MaxRowsPerColumn + 4 };

        for (var column = 0; column < shownRows.Length; column++)
        {
            for (var row = 0; row < SpellBuilderLayout.MaxRowsPerColumn; row++)
            {
                var hit = SpellBuilderLayout.HitAt(SpellBuilderLayout.PartRect(column, row, canvas).Center, canvas,
                    c => shownRows[c], 2);
                var expected = row < shownRows[column] ? SpellBuilderHitKind.Part : SpellBuilderHitKind.Panel;
                Assert.Equal(expected, hit.Kind);
            }
        }

        // Rows past the cap are never hit, however many parts the column holds.
        var below = SpellBuilderLayout.PartRect(3, SpellBuilderLayout.MaxRowsPerColumn, canvas).Center;
        Assert.NotEqual(SpellBuilderHitKind.Part, SpellBuilderLayout.HitAt(below, canvas, c => shownRows[c], 2).Kind);
    }
}
