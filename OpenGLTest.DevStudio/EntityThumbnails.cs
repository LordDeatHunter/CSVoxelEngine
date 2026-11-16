using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace VoxelEngine.DevStudio;

/// <summary>
/// One still picture per entity for the QUESTS tab's icon grid: each entity drawn once, at load, in its rest pose and from
/// the fixed angle of <see cref="EntityFraming"/>, into its own texture with a transparent background, the way the
/// ENTITIES tab draws it (<see cref="DebugSceneRenderer.RenderEntityThumbnail"/>). Main thread only; owns the textures.
/// </summary>
public sealed class EntityThumbnails : IDisposable
{
    /// <summary>The side of each thumbnail texture, in pixels.</summary>
    public const int Size = 96;

    // 3 s of 1/30 s ticks: longer than any spawn pop
    private const int SettleTicks = 90;

    private readonly GL _gl;
    private readonly Dictionary<string, uint> _textures = new(StringComparer.Ordinal);

    public EntityThumbnails(GL gl, DebugSceneRenderer scene, IEnumerable<string> entityNames)
    {
        _gl = gl;
        var state = new EntityPreviewState();
        var depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, Size, Size);
        var framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);
        foreach (var name in entityNames)
        {
            if (!state.SelectByName(name)) continue;
            // Past the spawn pop (a fresh mob grows in from nothing) and settled in its first mode, as the tab shows it
            for (var tick = 0; tick < SettleTicks; tick++) state.Update(1f / 30f);
            var texture = NewTexture();
            gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, texture, 0, 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            {
                gl.DeleteTexture(texture);
                continue;
            }
            scene.RenderEntityThumbnail(state, Size);
            _textures[name] = texture;
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.DeleteFramebuffer(framebuffer);
        gl.DeleteRenderbuffer(depth);
    }

    /// <summary>The thumbnail of the entity with that file name, if it could be drawn.</summary>
    public bool TryGet(string entityName, out uint texture) => _textures.TryGetValue(entityName, out texture);

    public void Dispose()
    {
        foreach (var texture in _textures.Values) _gl.DeleteTexture(texture);
        _textures.Clear();
    }

    // A one-layer texture array: the UI draws every texture as an array (UIRenderer.DrawMesh)
    private unsafe uint NewTexture()
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, texture);
        _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8, Size, Size, 1, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }
}
