using System;
using Silk.NET.OpenGL;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Rendering;

/// <summary>
/// The cloud-shadow coverage map: one R8 <c>GL_TEXTURE_2D</c> of <see cref="CloudField.Cells"/>
/// texels per side, baked once from the very same thresholded level grid the cloud mesh is built
/// from (<see cref="CloudField.BuildShadowCoverage"/>), so a shadow follows the drawn silhouette.
/// Linear filtering gives the penumbra and REPEAT wrap tiles it exactly like the 3x3 cloud copies.
/// The world fragment shader samples it in <c>CloudShadow</c> through
/// <see cref="CloudCopies.FieldUv"/>'s mapping. Main thread only, like every GL object.
/// </summary>
internal sealed class CloudShadowTexture : IDisposable
{
    private readonly GL _gl;

    /// <summary>The GL texture name.</summary>
    public uint Handle { get; private set; }

    /// <summary>
    /// The baked coverage bytes the texture was uploaded from, kept for the CPU readers of the
    /// cloud cover (the sun glare's <see cref="CloudCover.AcrossDisc"/>). Never modified.
    /// </summary>
    public byte[] Coverage { get; }

    public CloudShadowTexture(GL gl)
    {
        _gl = gl;
        var cells = CloudField.Cells;
        var coverage = CloudField.BuildShadowCoverage(CloudField.BuildLevels(), WorldGenerationConfig.CloudMaxLevels);
        Coverage = coverage;

        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        // Rows of one byte per texel are not 4-byte aligned in general.
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        unsafe
        {
            fixed (byte* ptr = coverage)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8,
                    (uint)cells, (uint)cells, 0, PixelFormat.Red, PixelType.UnsignedByte, ptr);
            }
        }
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        // No mip chain: sampled with Linear min filtering only.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        _gl.DeleteTexture(Handle);
        Handle = 0;
    }
}
