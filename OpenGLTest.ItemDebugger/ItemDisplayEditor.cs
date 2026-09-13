using System;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Editable copy of the selected item's <c>display</c> block plus the debugger-only renderer
/// tweaks. The display values are the ones <c>[SAVE]</c> writes back to the item JSON; the
/// held/grip overrides only affect the preview and are never persisted.
/// </summary>
public sealed class ItemDisplayEditor
{
    public byte SelectedId { get; private set; }

    public Vector3 Rotation { get; set; }
    public Vector3 Translation { get; set; }
    public float Scale { get; set; } = 1f;

    public float ItemHeldScale { get; set; } = RenderManager.HeldItemScale;
    public Vector3 ItemGrip { get; set; } = RenderManager.HeldItemGrip;
    public float BlockHeldScale { get; set; } = RenderManager.HeldBlockScale;
    public Vector3 BlockGrip { get; set; } = RenderManager.HeldBlockGrip;

    public bool HasSelection => SelectedId != 0;
    public bool IsPureItem => SelectedId != 0 && ItemRegistry.IsItem(SelectedId);
    public Item? Type => SelectedId == 0 ? null : ItemRegistry.TryGet(SelectedId);
    public string DisplayName => Type?.Name ?? "(nothing selected)";

    public string? SourcePath => IsPureItem && Type is { SourceName.Length: > 0 } type
        ? Path.Combine("assets", "data", "items", type.SourceName + ".json")
        : null;

    public bool CanSave => SourcePath is { } path && File.Exists(path);

    public void Select(byte id)
    {
        SelectedId = id;
        ReloadDisplay();
    }

    /// <summary>Resets the display sliders to the values the item JSON loaded with.</summary>
    public void ReloadDisplay()
    {
        if (IsPureItem && Type is { } type)
        {
            Rotation = type.DisplayRotation;
            Translation = type.DisplayTranslation;
            Scale = type.DisplayScale;
        }
        else
        {
            Rotation = Vector3.Zero;
            Translation = Vector3.Zero;
            Scale = 1f;
        }
    }

    /// <summary>Resets the preview-only held scale and grip overrides to the renderer defaults.</summary>
    public void ResetRenderer()
    {
        ItemHeldScale = RenderManager.HeldItemScale;
        ItemGrip = RenderManager.HeldItemGrip;
        BlockHeldScale = RenderManager.HeldBlockScale;
        BlockGrip = RenderManager.HeldBlockGrip;
    }

    /// <summary>The edited display transform, composed exactly like <see cref="Item.DisplayMatrix"/>.</summary>
    public Matrix4x4 DisplayMatrix()
    {
        var radians = Rotation * (MathF.PI / 180f);
        return Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z)
            * Matrix4x4.CreateTranslation(Translation);
    }
}
