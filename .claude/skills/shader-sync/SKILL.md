---
name: shader-sync
description: List of GLSL blocks and C# constants in CSVoxelEngine that must be edited together (SkySample copies, wave/sway blocks, tint gain, light-tint decode, sRGB twins). Use whenever you touch any shader under assets/shaders or a GL-free twin class.
---

# Shader / twin sync

Several pieces of logic exist in more than one place on purpose. Change one and you must change its partners in the same commit, then run the guarding tests.

| Group | Locations | Guard |
|---|---|---|
| **SkySample** (sky colour at a view direction; fog target) | `assets/shaders/sky-fragment.glsl`, `test-fragment.glsl`, `cloud-fragment.glsl` (verbatim duplicates) + C# reference `SkyPalette.Sample` | `SkyPaletteTests` |
| **Wave and leaf-sway blocks** | `test-vertex.glsl` and `shadow-vertex.glsl` must stay **byte-identical**; the `TO_WORLD(p)` macro is what lets them be. The sway weight decode (`bool swaying` / `float vSway` from `vColor.a`) is part of it | `ChunkDrawShaderTests` (the sway block and its decode; the water wave lives only in the world shader) |
| **Biome tint gain** | literal `4.0` behind `fTintGain` in `test-vertex.glsl` == `BiomeTint.Gain` (C#); also read in `test-fragment.glsl`, with the textureless grey `vec3(0.1221)` (= sRGB 98 decoded, `BiomeTintTextureGrey`) | `BiomeTintTests`, `BiomeTintTextureTests` |
| **Block-light tint decode** | `test-vertex.glsl` decode of the 10-bit code into `fBlockTint` == `LightTint.Decode`; palette uploaded as `uBlockLightPalette[8]` from `BlockLightPalette.Linear`; GLSL constants standing for a palette colour are written decoded with the sRGB original in a comment | light tint tests, `ColorSpaceTests` |
| **Vertex layout** | `Vertex` (32 B), vertex attribute setup in `Chunk`/the pools, attribute locations in the vertex shaders (5 = normal+tint ushort, 7 = layer, 8 = water surface) and the bit meanings of `Vertex.Normal` (0-2 face, 3 submerged, 4 biome tint, 5 sway, 6-15 tint code); vertex alpha is the sway weight on a sway vertex, else 254 water / 253 LOD foliage / 255 | mesh tests, `PlantSwayTests` |
| **Tonemap / sRGB** | `post-fragment.glsl` exposure + clamp + sRGB encode == `Tonemap.cs`; `ColorSpace.SrgbToLinear` on CPU colours | `ColorSpaceTests` |
| **Contact AO** | `contact-ao-fragment.glsl` == `ContactAo` | `ContactAoTests` |
| **Snow glint grid** | `const float SnowTexels` in `test-fragment.glsl` == the atlas tile size (`TextureAtlas.TileWidth`/`TileHeight`), so a glint lights exactly one texel of the snow art | `SnowSparkleTests` |
| **Water fresnel / wet weight** | `test-fragment.glsl` == `SurfaceFresnel` | its tests |
| **Texture sampling convention** | meshes sample `sampler2DArray` with `vec3(uv, float(layer))`; textures sampled before the tonemap are `SRGB8_ALPHA8`, data atlases plain; `ui-fragment.glsl` `uSrgbTex` re-encodes the two shared textures | check by eye |
| **Material atlas** | `fTangent`/`fBitangent` in `test-vertex.glsl` must agree with `Chunk.TiledUv` per-face UV orientation | ore scenario |
| **Stars** | `sky-fragment.glsl` `Stars`/`StarHash`/`StarCellHash`/`StarFaceDirection` (cube face, equi-angular warp, solid-angle weight, in-cell jitter, brightness law, twinkle rate/phase and curve) == `StarField` | `StarFieldTests` |
| **Sun glare falloff** | `sky-fragment.glsl` `uGlareColor * pow(max(dot(dir, uSunDir), 0), uGlareExponent)` == `SunGlare.Radial` (the intensity itself is CPU-only, `SunGlare.Intensity`) | `SunGlareTests` |
| **Cloud cover along a ray** | `sky-fragment.glsl` `CloudLevelAt`/`CloudCoverAlongRay` == `CloudCover.LevelAt`/`AlongRay` (cell-exact read of the cloud-shadow map, layer-boundary tests, cubic fog fade) | `CloudCoverTests` |

## Rules
- All chunk shaders are `#version 460 core`; the world/shadow vertex shaders compile twice (`CHUNK_DRAW` define). Both variants must still compile (`ShaderDefinesTests`).
- Depth is D3D-style 0..1 (`System.Numerics` projection): never write a near/far linearisation; reconstruct positions by inverting the matrix that wrote the depth (`uInvViewProj`).
- Shaders are loaded from `OpenGLTest/assets/shaders` source at runtime. A temporary debug patch goes there; restore it (`git diff` clean) and do not edit the bin copy.
- Run the full test suite, then a scripted visual check (`visual-verify` skill; `linear-lighting.json` at VOXEL_TIME 147, 440, 700 covers most lighting changes, `sky.json` the SkySample).
