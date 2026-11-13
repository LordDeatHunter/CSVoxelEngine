using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Map;
using VoxelEngine.UI;
using VoxelEngine.UI.Map;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// Draws the maps from the <see cref="MapTracker"/>'s tiles (uploaded per region by <see cref="MapRegionTextures"/>):
/// <list type="bullet">
/// <item>the minimap under the top-right corner, north-up around the player, square (scissored) or round (clipped by
/// the ui shader's <c>uClipCircle</c>), with the pins over it: arrows for players, hostile and neutral mobs, an icon
/// per discovered waystone and per battle tower marked with a tower map, a far player, waystone or tower held to the rim
/// and a far mob left out;</item>
/// <item>the [M] map screen over the whole canvas, panned and zoomed by its <see cref="MapScreenView"/>, with the same
/// pins, the name of the waystone or tower under the pointer, the pointer's block coordinates, the layer it shows and the
/// CENTER and minimap shape buttons.</item>
/// </list>
/// Owns the pin sprites and the region textures. Render thread only.
/// </summary>
internal sealed class MapRenderer : IDisposable
{
    private static readonly Rgba PlayerColor = new(90, 200, 255);
    private static readonly Rgba HostileColor = new(240, 72, 60);
    private static readonly Rgba NeutralColor = new(250, 214, 80);
    private static readonly Rgba NorthColor = new(255, 92, 92);
    // What shows where nothing has been explored, and the minimap's frame
    private static readonly Rgba Unexplored = new(14, 16, 20);
    private static readonly Rgba FrameColor = new(58, 62, 72);
    private const float NorthInset = 11f;

    private readonly GL _gl;
    private readonly UIRenderer _ui;
    private readonly uint _arrowTexture;
    private readonly uint _waystoneTexture;
    private readonly uint _towerTexture;
    private readonly MapRegionTextures _regions;
    private readonly int _locClipCircle;
    private int _readoutX = int.MinValue, _readoutZ = int.MinValue;
    private string _readout = "";

    internal MapRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
        _arrowTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/map/pin_arrow.png");
        _waystoneTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/map/waystone.png");
        _towerTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/map/tower.png");
        _regions = new MapRegionTextures(gl);
        _locClipCircle = ui.UiShader.GetUniformLocation("uClipCircle");
    }

    /// <summary>Takes the tracker's rescanned tiles; once a frame, after its update and before either map draws.</summary>
    internal void Sync(MapTracker tracker) => _regions.Sync(tracker);

    /// <summary>The minimap around world XZ <paramref name="focus"/> (the player).</summary>
    internal void DrawMinimap(MapTracker tracker, IReadOnlyList<MapPin> pins, Vector2 focus, bool round)
    {
        var box = MapLayout.Minimap(_ui.ScreenSize);
        var inner = box.Inset(MapConfig.MinimapFrame);
        if (round)
        {
            ClipToCircle(box.Center, box.Size.X * 0.5f);
            _ui.DrawRect(box.Center, box.Size, FrameColor);
            ClipToCircle(inner.Center, inner.Size.X * 0.5f);
        }
        else
        {
            _ui.DrawRect(box.Center, box.Size, FrameColor);
            ScissorTo(inner);
        }
        try
        {
            _ui.DrawRect(inner.Center, inner.Size, Unexplored);
            DrawRegions(tracker, focus, MapConfig.MinimapScale, inner);
        }
        finally
        {
            Unclip();
        }

        foreach (var pin in pins)
        {
            if (pin.Kind == MapPinKind.Self) continue;
            var at = MapLayout.ToCanvas(pin.Position, focus, MapConfig.MinimapScale, inner.Center);
            var size = SizeOf(pin.Kind);
            // A far player, waystone or tower stays on the rim, pointing the way; a far mob is no concern
            if (MapLayout.ClampToRim(ref at, inner, round, size * 0.5f) && pin.Kind is MapPinKind.Hostile or MapPinKind.Neutral)
                continue;
            DrawPin(pin, at, size);
        }
        if (pins.Count > 0 && pins[^1] is { Kind: MapPinKind.Self } self)
            DrawPin(self, inner.Center, SizeOf(MapPinKind.Self));

        _ui.DrawTextMiddle("N", new Vector2(inner.Center.X, inner.Top + NorthInset), _ui.CountScale, NorthColor);
    }

    /// <summary>The [M] map screen over the whole canvas.</summary>
    internal void DrawScreen(MapScreenView view, MapTracker tracker, IReadOnlyList<MapPin> pins)
    {
        var canvas = _ui.ScreenSize;
        var whole = new UiRect(canvas * 0.5f, canvas);
        _ui.DrawRect(whole.Center, whole.Size, Unexplored);
        DrawRegions(tracker, view.Center, view.Zoom, whole);

        MapPin? hovered = null;
        foreach (var pin in pins)
        {
            var at = MapLayout.ToCanvas(pin.Position, view.Center, view.Zoom, whole.Center);
            if (!whole.Inset(-MapConfig.PlayerPinSize).Contains(at)) continue;
            DrawPin(pin, at, SizeOf(pin.Kind));
            if (pin.Kind is MapPinKind.Waystone or MapPinKind.Tower && view.Hover is { } pointer
                && Vector2.Distance(pointer, at) <= MapConfig.PlaceHoverRadius)
                hovered = pin;
        }
        if (hovered is { Label: { } name } place)
        {
            var at = MapLayout.ToCanvas(place.Position, view.Center, view.Zoom, whole.Center);
            _ui.DrawTextMiddle(name, at - new Vector2(0f, SizeOf(place.Kind) + 4f), _ui.CountScale, AccentGold);
        }

        var header = MapLayout.Header;
        _ui.DrawTextCenteredY("MAP", header.X, header.Y, _ui.HeadScale, AccentGold);
        _ui.DrawTextCenteredY(LayerName(tracker.Layer), header.X + _ui.TextWidth("MAP", _ui.HeadScale) + 20f, header.Y,
            _ui.CountScale, TextDim);
        var hint = MapLayout.Hint(canvas);
        _ui.DrawTextCenteredY("Drag to pan   Wheel to zoom   M or Esc to close", hint.X, hint.Y, _ui.CountScale, TextDim);
        if (view.Hover is { } point)
        {
            var world = MapLayout.ToWorld(point, view.Center, view.Zoom, canvas);
            var readout = Readout((int)MathF.Floor(world.X), (int)MathF.Floor(world.Y));
            var right = MapLayout.Readout(canvas);
            _ui.DrawTextCenteredY(readout, right.X - _ui.TextWidth(readout, _ui.CountScale), right.Y, _ui.CountScale, Rgba.White);
        }

        _ui.DrawButton(MapLayout.ShapeButton(canvas), GameSettings.MinimapRound ? "MINIMAP: ROUND" : "MINIMAP: SQUARE",
            true, AccentGold);
        _ui.DrawButton(MapLayout.CenterButton(canvas), "CENTER", !view.Following, ReadyGreen);
    }

    private static string LayerName(MapLayer layer) => layer.Kind switch
    {
        MapLayerKind.Sky => "Sky islands",
        MapLayerKind.Cave => $"Caves, from Y {layer.TopY}",
        _ => "Surface",
    };

    // The pointer's block, formatted only when it moves to another one
    private string Readout(int x, int z)
    {
        if (x == _readoutX && z == _readoutZ) return _readout;
        _readoutX = x;
        _readoutZ = z;
        _readout = $"X {x}   Z {z}";
        return _readout;
    }

    // Every region texture overlapping a view of world XZ center and zoom drawn into `view`
    private void DrawRegions(MapTracker tracker, Vector2 center, float zoom, UiRect view)
    {
        var half = view.Size / (2f * zoom);
        const float region = MapRegionTextures.RegionBlocks;
        var rx0 = (int)MathF.Floor((center.X - half.X) / region);
        var rx1 = (int)MathF.Floor((center.X + half.X) / region);
        var rz0 = (int)MathF.Floor((center.Y - half.Y) / region);
        var rz1 = (int)MathF.Floor((center.Y + half.Y) / region);
        var size = new Vector2(region * zoom);
        for (var rz = rz0; rz <= rz1; rz++)
        for (var rx = rx0; rx <= rx1; rx++)
        {
            var texture = _regions.TextureFor(rx, rz, tracker);
            if (texture == 0) continue;
            var topLeft = MapLayout.ToCanvas(new Vector2(rx * region, rz * region), center, zoom, view.Center);
            _ui.DrawTexture(topLeft + size * 0.5f, size, texture);
        }
    }

    private static float SizeOf(MapPinKind kind) => kind switch
    {
        MapPinKind.Self or MapPinKind.Player => MapConfig.PlayerPinSize,
        MapPinKind.Waystone or MapPinKind.Tower => MapConfig.PlacePinSize,
        _ => MapConfig.MobPinSize,
    };

    // An arrow turned to its heading and tinted by kind, or a place's icon upright
    private void DrawPin(MapPin pin, Vector2 at, float size)
    {
        if (pin.Kind is MapPinKind.Waystone or MapPinKind.Tower)
        {
            _ui.DrawTexture(at, new Vector2(size), pin.Kind == MapPinKind.Tower ? _towerTexture : _waystoneTexture);
            return;
        }
        var color = pin.Kind switch
        {
            MapPinKind.Hostile => HostileColor,
            MapPinKind.Neutral => NeutralColor,
            _ => PlayerColor,
        };
        // The flip into the Y-down canvas comes first, so a positive turn is clockwise on screen, like a heading
        var model = Matrix4x4.CreateScale(size, -size, 1f)
                    * Matrix4x4.CreateRotationZ(pin.Heading * (MathF.PI / 180f))
                    * Matrix4x4.CreateTranslation(at.X, at.Y, 0f);
        _ui.DrawMesh(_ui.UnitSquare, model, color, _arrowTexture);
    }

    // Drops every fragment outside a canvas circle: the shader compares against gl_FragCoord, in framebuffer pixels
    // with y up, so the circle is carried over from canvas units the way UiCanvas.ScissorRect carries a rect
    private void ClipToCircle(Vector2 canvasCenter, float canvasRadius)
    {
        var kx = _ui.WindowSize.X > 0f ? _ui.FramebufferSize.X / _ui.WindowSize.X : 1f;
        var ky = _ui.WindowSize.Y > 0f ? _ui.FramebufferSize.Y / _ui.WindowSize.Y : 1f;
        var center = canvasCenter + _ui.OverlayOffset;
        var clip = new Vector3(center.X * _ui.Scale * kx, _ui.FramebufferSize.Y - center.Y * _ui.Scale * ky,
            canvasRadius * _ui.Scale * kx);
        _ui.UiShader.Use();
        _ui.UiShader.SetUniform(_locClipCircle, clip);
    }

    private void ScissorTo(UiRect rect)
    {
        var (x, y, width, height) = UiCanvas.ScissorRect(new Vector2(rect.Left, rect.Top) + _ui.OverlayOffset, rect.Size,
            _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(x, y, (uint)width, (uint)height);
    }

    private void Unclip()
    {
        _gl.Disable(EnableCap.ScissorTest);
        _ui.UiShader.Use();
        _ui.UiShader.SetUniform(_locClipCircle, Vector3.Zero);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_arrowTexture);
        _gl.DeleteTexture(_waystoneTexture);
        _gl.DeleteTexture(_towerTexture);
        _regions.Dispose();
    }
}
