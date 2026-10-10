using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The item debugger's slider state: RESET PREVIEW puts every slider back (the display ones to what the item loaded
/// with, the preview-only renderer overrides to their defaults), RELOAD FROM JSON only the display ones.
/// </summary>
public sealed class ItemDisplayEditorTests
{
    public ItemDisplayEditorTests() => TestContent.EnsureLoaded();

    private static ItemDisplayEditor EditedBlade()
    {
        var editor = new ItemDisplayEditor();
        editor.Select(ContentRegistry.RequireItem("obsidian_blade").Id);
        // Drag every slider off where it loaded.
        editor.Rotation += new Vector3(10f, -55f, 25f);
        editor.Translation += new Vector3(0.1f, -0.1f, 0.2f);
        editor.Scale *= 1.5f;
        editor.ItemHeldScale *= 2f;
        editor.ItemGrip += new Vector3(0.05f, 0.05f, 0.05f);
        editor.BlockHeldScale *= 2f;
        editor.BlockGrip += new Vector3(0.05f, 0.05f, 0.05f);
        return editor;
    }

    [Fact]
    public void ResetPreviewPutsEverySliderBack()
    {
        var editor = EditedBlade();
        var item = editor.Type!;

        editor.ResetPreview();

        Assert.Equal(item.DisplayRotation, editor.Rotation);
        Assert.Equal(item.DisplayTranslation, editor.Translation);
        Assert.Equal(item.DisplayScale, editor.Scale);
        Assert.Equal(RenderManager.HeldItemScale, editor.ItemHeldScale);
        Assert.Equal(RenderManager.HeldItemGrip, editor.ItemGrip);
        Assert.Equal(RenderManager.HeldBlockScale, editor.BlockHeldScale);
        Assert.Equal(RenderManager.HeldBlockGrip, editor.BlockGrip);
    }

    [Fact]
    public void ReloadFromJsonPutsOnlyTheDisplaySlidersBack()
    {
        var editor = EditedBlade();
        var item = editor.Type!;
        var heldScale = editor.ItemHeldScale;
        var grip = editor.ItemGrip;

        editor.ReloadDisplay();

        Assert.Equal(item.DisplayRotation, editor.Rotation);
        Assert.Equal(item.DisplayTranslation, editor.Translation);
        Assert.Equal(item.DisplayScale, editor.Scale);
        // The preview-only overrides are not the item's and are left as dragged.
        Assert.Equal(heldScale, editor.ItemHeldScale);
        Assert.Equal(grip, editor.ItemGrip);
    }

    [Fact]
    public void ABlockHasNoDisplayOfItsOwnSoItsSlidersResetToIdentity()
    {
        var editor = new ItemDisplayEditor();
        editor.Select(ContentRegistry.RequireBlock("stone").Id);
        editor.Rotation = new Vector3(5f, 5f, 5f);
        editor.Scale = 2f;

        editor.ResetPreview();

        Assert.False(editor.IsPureItem);
        Assert.Equal(Vector3.Zero, editor.Rotation);
        Assert.Equal(Vector3.Zero, editor.Translation);
        Assert.Equal(1f, editor.Scale);
    }
}
