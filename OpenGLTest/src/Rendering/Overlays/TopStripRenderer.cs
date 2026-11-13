using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Data;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using static VoxelEngine.Rendering.Overlays.UIRenderer;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The top-center HUD strip: the compass bar, or the held clock's readout in the same panel, and
/// the look-at label below it, whose start <see cref="LookLabelTop"/> places under the strip.
/// Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class TopStripRenderer
{
    // Top-center compass bar (DrawCompass), in pixels: a scrolling 120-degree window
    // of the horizon with cardinals every 45 degrees and minor ticks every 15
    private const float CompassTop = 10f;
    private const float CompassWidth = 440f;
    private const float CompassHeight = 42f;
    private const float CompassSidePad = 14f;
    private const float CompassHalfSpan = 60f;
    private const float CompassTickStep = 15f;
    private const float CompassCardinalStep = 45f;
    private const float CompassReadoutTop = 2f;
    private const float CompassTickTop = 20f;
    private const float CompassCoordsGap = 4f;
    private const float CompassSpawnDot = 8f;
    private const float CompassHideSpawnWithin = 1f;
    private static readonly Rgba CompassNorth = new(255, 92, 92);
    private static readonly Rgba CompassSpawn = new(140, 210, 255);
    private static readonly Rgba CompassTick = Rgba.Gray(0.75f, 0.9f);

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    internal TopStripRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>
    /// The look-at label at the top of the screen: a small 3D render of the targeted block (its
    /// own cube mesh and atlas tile, or a flat dark silhouette while the player has not obtained
    /// it) with its name below it — red when the held item cannot mine it — and, with the
    /// profiler on, the debug lines (id, state byte, sun and block light, chunk and cell).
    /// <paramref name="top"/> is the y its layout starts at; <see cref="LookLabelTop"/> gives the
    /// value for the frame's compass/clock state, so the strip pushes the label below it.
    /// </summary>
    internal void DrawLookLabel(LookLabelState label, float time, bool debug, float top)
    {
        if (_ui.Font == null || string.IsNullOrEmpty(label.Text)) return;

        var center = _ui.ScreenCenter;
        // Anchored by the spinning cube's largest half-height, so no yaw ever rises above `top`
        var cubeCenter = new Vector2(center.X, top + LookLabelCubeHalfHeight);
        if (_ui.BlockCubes.GetLookAt(label.BlockId, label.BlockState) is { } cube)
        {
            // The shape is centered on its own origin, so it spins in place; an unknown block is
            // the same mesh with uUseTexture off, a flat dark silhouette. FlipToScreen mirrors
            // the Y-up model into the Y-down canvas with the tilt then tipping its top face
            // toward the viewer, like the icons; the model's faces overlap on screen, so the
            // draw needs the depth test the flat shapes skip, or its back faces blend over the
            // front ones.
            _gl.Enable(EnableCap.DepthTest);
            _ui.EnsureDepthCleared();

            var model = Matrix4x4.CreateScale(LookLabelCubeSize)
                        * Matrix4x4.CreateRotationY(time * 0.6f)
                        * Matrix4x4.CreateRotationX(LookLabelCubeTilt)
                        * FlipToScreen(cubeCenter);

            // Same cutout discard as DrawBlockIcon: a leaf's transparent texels would
            // otherwise write depth and swallow the faces behind them.
            var cutout = label.Known && BlockType.ById[label.BlockId]?.Cutout == true;
            if (cutout)
            {
                _ui.UiShader.Use();
                _ui.UiShader.SetUniform(_ui.AlphaCutoffLocation, 0.5f);
            }
            _ui.DrawMesh(cube, model, label.Known ? Rgba.White : new Rgba(26, 26, 30),
                label.Known ? _ui.AtlasTexture : 0, srgb: label.Known && _ui.AtlasIsSrgb);
            if (cutout)
                _ui.UiShader.SetUniform(_ui.AlphaCutoffLocation, 0f);

            _gl.Disable(EnableCap.DepthTest);
        }

        var textTop = cubeCenter.Y + LookLabelNameGap;
        textTop = DrawWrappedCentered(label.Text, center.X, textTop, label.Mineable ? Rgba.White : DangerRed) + 2f;
        var detailTop = textTop;
        if (!string.IsNullOrEmpty(label.Detail))
            detailTop = DrawWrappedCentered(label.Detail, center.X, detailTop, TextDim);
        if (label.Bars != null)
            detailTop = DrawStationBars(label.Bars, center.X, detailTop);
        if (!debug) return;

        var chunkPos = VoxelCoords.ChunkPosOf(label.Cell);
        var lines = new[]
        {
            $"id {label.BlockId}   state {label.BlockState}",
            $"sun {label.Light.Sun:0}   block {label.Light.Block:0}",
            $"chunk {(int)chunkPos.X} {(int)chunkPos.Y} {(int)chunkPos.Z}   cell {(int)label.Cell.X & Chunk.SizeMask} {(int)label.Cell.Y & Chunk.SizeMask} {(int)label.Cell.Z & Chunk.SizeMask}",
        };
        var linesTop = detailTop;
        for (var i = 0; i < lines.Length; i++)
            _ui.DrawTextCentered(lines[i], center.X, linesTop + i * (TextSize.Body + 4f), _ui.CountScale, TextDim);
    }

    // One bar per entry under the label: its caption, then a track filled left to right by its fraction; returns the y below the last
    private float DrawStationBars(IReadOnlyList<StationBar> bars, float centerX, float top)
    {
        foreach (var bar in bars)
        {
            _ui.DrawTextCenteredFitted(bar.Label, centerX, top, _ui.CountScale, TextDim, StationBarWidth + 2f * LookLabelSideMargin);
            top += TextSize.Body + 2f;
            _ui.DrawRect(new Vector2(centerX, top + StationBarHeight * 0.5f), new Vector2(StationBarWidth, StationBarHeight), StationBarTrack);
            var fill = MathUtils.Clamp01(bar.Fraction) * StationBarWidth;
            if (fill > 0.5f)
                _ui.DrawRect(new Vector2(centerX - StationBarWidth * 0.5f + fill * 0.5f, top + StationBarHeight * 0.5f),
                    new Vector2(fill, StationBarHeight), bar.Kind switch
                    {
                        StationBarKind.Fuel => StationBarFuel,
                        StationBarKind.Done => StationBarDone,
                        _ => StationBarProgress,
                    });
            top += StationBarHeight + StationBarGap;
        }
        return top;
    }

    // Draws `text` centered on one line when it fits the label's width cap and otherwise wrapped onto as many lines as it
    // needs, so a long name or helper line grows downward instead of being cut; returns the y below the last line.
    private float DrawWrappedCentered(string text, float centerX, float top, Rgba color)
    {
        var scale = _ui.CountScale;
        var maxWidth = MathF.Min(LookLabelMaxWidth, _ui.ScreenSize.X - 2f * LookLabelSideMargin);
        _wrapScratch.Clear();
        TextWrap.Wrap(text, maxWidth, line => _ui.MeasureTextWidth(line.ToString(), scale), _wrapScratch);
        foreach (var line in _wrapScratch)
        {
            _ui.DrawTextCentered(line, centerX, top, scale, color);
            top += TextSize.Body + 4f;
        }
        return top;
    }

    /// <summary>
    /// The y the look-at label's layout starts at: below the compass or the clock while either
    /// occupies the top-center strip, the compass as low as its coordinates line when one is
    /// shown, and the strip's own top margin with neither — so equipping the compass pushes the
    /// label down instead of overlapping it. The compass wins when both are equipped, like the
    /// draw itself, and a clock has no coordinates line.
    /// </summary>
    internal static float LookLabelTop(bool compass, bool clock, bool coordinates)
    {
        if (compass)
            return CompassTop + CompassHeight
                + (coordinates ? CompassCoordsGap + TextSize.Body : 0f) + LookLabelTopGap;
        return clock ? CompassTop + CompassHeight + LookLabelTopGap : CompassTop;
    }

    // The look-at label: its cube's size and tilt, the spinning cube's largest screen half-height
    // (its layout anchor), the gap below the top HUD strip and the gap from the cube to the name
    private const float LookLabelCubeSize = 34f;
    // Positive tips the mesh's top face at the viewer once FlipToScreen has mirrored it
    private const float LookLabelCubeTilt = 0.45f;
    // The cube's vertical half extent: half the size times cos(tilt) + sin(tilt) * (|sin yaw| +
    // |cos yaw|), whose worst yaw is the sqrt(2) diagonal
    private static readonly float LookLabelCubeHalfHeight = LookLabelCubeSize * 0.5f
        * (MathF.Cos(LookLabelCubeTilt) + MathF.Sin(LookLabelCubeTilt) * MathF.Sqrt(2f));
    private const float LookLabelTopGap = 6f;
    private const float LookLabelNameGap = 14f;
    // Wider than any block name, so a long line (a bloomery's heat and ore, a locked gate's hint) wraps rather than being cut
    private const float LookLabelMaxWidth = 460f;
    private const float LookLabelSideMargin = 20f;
    private const float StationBarWidth = 180f;
    private const float StationBarHeight = 6f;
    private const float StationBarGap = 5f;
    private static readonly Rgba StationBarTrack = Rgba.Gray(0.1f, 0.75f);
    private static readonly Rgba StationBarFuel = new(255, 160, 60);
    private static readonly Rgba StationBarProgress = new(110, 200, 255);
    private static readonly Rgba StationBarDone = new(120, 220, 120);
    private readonly List<string> _wrapScratch = [];

    /// <summary>
    /// The held clock's readout: the same top-center panel the compass uses, showing the day
    /// number and the time of day (see <see cref="Clock"/>). Drawn only while the clock is in
    /// the hotbar, like the compass.
    /// </summary>
    internal void DrawClock(int day, string timeOfDay)
    {
        var cx = _ui.ScreenSize.X * 0.5f;
        _ui.DrawRect(new Vector2(cx, CompassTop + CompassHeight * 0.5f),
            new Vector2(CompassWidth, CompassHeight), PanelBackground);
        _ui.DrawTextCentered($"Day {day}   {timeOfDay}", cx, CompassTop + CompassReadoutTop,
            _ui.CountScale, Rgba.White);
    }

    /// <summary>
    /// RPG-style compass: a translucent bar across the top center showing a 120-degree
    /// window of the horizon around the camera heading, with cardinal labels every 45
    /// degrees (N in red), minor ticks every 15, a center caret, the numeric heading on
    /// top and a light-blue dot at the bearing toward the world spawn. The font only
    /// covers printable ASCII, so the readout is "N 005" rather than using a degree sign.
    /// <paramref name="yaw"/> is the camera yaw in degrees (0 looks down +X);
    /// <paramref name="position"/> is the eye position, also feeding the optional
    /// coordinates line under the bar.
    /// </summary>
    internal void DrawCompass(float yaw, Vector3 position, bool showCoordinates = true)
    {
        var heading = Compass.HeadingFromYaw(yaw);
        var cx = _ui.ScreenSize.X * 0.5f;
        var halfInner = CompassWidth * 0.5f - CompassSidePad;

        _ui.DrawRect(new Vector2(cx, CompassTop + CompassHeight * 0.5f),
            new Vector2(CompassWidth, CompassHeight), PanelBackground);

        var readout = _ui.TextCache.CompassReadout(heading);
        _ui.DrawTextCentered(readout, cx, CompassTop + CompassReadoutTop, _ui.CountScale, Rgba.White);

        var tickTop = CompassTop + CompassTickTop;
        var start = MathF.Floor((heading - CompassHalfSpan) / CompassTickStep) * CompassTickStep;
        for (var d = start; d <= heading + CompassHalfSpan + 0.01f; d += CompassTickStep)
        {
            var rel = Compass.WrapDelta(d - heading);
            if (MathF.Abs(rel) > CompassHalfSpan) continue;
            var x = cx + rel / CompassHalfSpan * halfInner;
            var norm = ((d % 360f) + 360f) % 360f;
            if (MathF.Abs(norm % CompassCardinalStep) < 0.01f)
            {
                var name = Compass.CardinalName(norm);
                _ui.DrawTextCentered(name, x, tickTop, _ui.CountScale,
                    name == "N" ? CompassNorth : Rgba.White);
            }
            else
            {
                _ui.DrawRect(new Vector2(x, tickTop + 7f), new Vector2(2f, 10f), CompassTick);
            }
        }

        var spawnDx = Compass.SpawnX - position.X;
        var spawnDz = Compass.SpawnZ - position.Z;
        if (MathF.Sqrt(spawnDx * spawnDx + spawnDz * spawnDz) > CompassHideSpawnWithin)
        {
            var spawnRel = Compass.WrapDelta(
                Compass.BearingTo(position.X, position.Z, Compass.SpawnX, Compass.SpawnZ) - heading);
            if (MathF.Abs(spawnRel) <= CompassHalfSpan)
                _ui.DrawSquare(new Vector2(cx + spawnRel / CompassHalfSpan * halfInner, tickTop + 16f),
                    CompassSpawnDot, CompassSpawn);
        }

        _ui.DrawRect(new Vector2(cx, tickTop + 8f), new Vector2(2f, 22f), Rgba.White);

        if (showCoordinates && _ui.Font != null)
        {
            var coords = _ui.TextCache.Coords(position);
            _ui.DrawTextCentered(coords, cx, CompassTop + CompassHeight + CompassCoordsGap,
                _ui.CountScale, Rgba.White);
        }
    }
}
