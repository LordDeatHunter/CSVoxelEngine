# Rendering: water, linear lighting, fog and sky, screen-space and surface effects

Water rendering, linear lighting (SH-10), fog, stars, moon, aurora, godrays, contact AO and every per-tile shader term. Shader twins that must change together: see the `shader-sync` skill.

**Water rendering** lives in the shared world shader plus two extra passes, the planar mirror (`ReflectionPass.RenderReflection`) and the scene copy. Just before `TerrainPasses.RenderWorldTranslucent` draws, `RenderTargets.CopyScene` snapshots the HDR scene target's color and depth (see "Linear lighting" below; the color copy is `Rgba16f`, like the reflection mirror) into two textures on units 3 and 4 (`CopyTexSubImage2D` from the bound HDR FBO — the water pass depth-tests while sampling, so it cannot read the target directly); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. Where that view-ray depth thins below `WaterFoamWidthBlocks` (a wall, a bank, a thin trickle) a pale **shore foam** band is mixed over the composed surface in linear light, lit by the shared opaque `light` term and fogged with it, broken up by a value-noise field the ripple phases jiggle and `FlowFoam` (the same two-phase flow map) carries downstream; top faces only, never from below or with the eye submerged, faded out by 160 blocks. The width must stay under a full cell's surface less the swell (`WaterFoamTests`), so open water over a block floor never foams; being screen-space, only contacts the depth copy sees foam (the camera-facing side of a block; a wall seen straight down shows none). `visual-examples/water-foam.json` frames liquid shores at `VOXEL_TIME` 360 and 147. Flowing water carries its baked flow into that normal (`FlowWaveNormal`: a bounded two-phase flow map that scrolls the ripples downstream and stretches them along the flow, identical to `WaveNormal` when the flow is zero), and falling side faces add fast vertical streaks; generated rivers and lakes are all source cells, so they bake no flow and stay still until the simulation makes them flow (`visual-examples/flow-water.json`, `VOXEL_TIME=147`, pours some). The mirror plane itself is the single `RenderManager.WaterPlaneY` at sea level, so a lake whose surface stands above it reflects against that plane rather than its own - an accepted approximation, since only one plane can be mirrored per frame. Seen from below — decided per fragment from the face normal (`fromBehind` in `test-fragment.glsl`), never from the sea plane, since a pond stands above it — the surface is a Snell window: the same scene copy shows through it distorted by the wave normal, the inside fresnel goes to total internal reflection past the critical angle and that band fades to the underwater fog color, and the mirror is never sampled from that side; `ReflectionReuse.MirrorApplies` also skips rendering it whenever `Camera.IsEyeInWater`, and the sky dome (`SkyRenderer`) and the sun disc stay dry in water (lava still fades the dome via `FrameLighting.SkyTintAmount`, eased through its `_lavaT`) so the window shows real sky. World, cloud and sky fog share `SkySample`, duplicated verbatim in `sky-fragment.glsl`, `test-fragment.glsl` and `cloud-fragment.glsl`, with `SkyPalette.Sample` as the tested C# reference, so change all four together. The dome currently samples the flat 28198cc sky color: `SkyPalette.Compute` returns the old night/day mix, pulled toward the dusk orange by `SkyDuskMix`, as both zenith and horizon with zero glow, and `SkyHazeStrength` is 0, so every direction (and every fully fogged fragment) is that one color. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `TerrainPasses.CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. The mirror's opaque chunk and LOD lists skip anything whose top (`Chunk.WorldMax.Y`, `LodRegion.YMax`) plus `ReflectionCull.TopMarginBlocks` (a multiblock's overhang, two blocks for the 3x3x3 furnace, plus the shader's sway and wave lift) lies below the plane, since its clip would discard every fragment. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index, bit 5 is the leaf-sway flag, bits 6-7 the low bits of the block-light tint code) and get the animated Voronoi caustic net; bit 4 is the biome-tint flag (see "Biome-tinted grass and leaves" below). The world fragment darkens a submerged face's albedo by `WetDarken` once, before the light multiply, and only at or below that face's own baked surface (`step(fWorldPos.y, fWaterSurfaceY)`; the caustics stop at the same line), so a dry face beside water — a beach top, a bank wall over the waterline — is never darkened or tinted; ore tiles (a non-zero `material.metallic`) are excluded. `SurfaceFresnel` is the GL-free twin (`Schlick`, `WetWeight`). While the eye is submerged, `UnderwaterRaysRenderer` adds one full-screen ray-march for sun shafts, composited in display space (the upsample reads the HDR target it draws into, adds the shafts to the encoded scene with the pre-SH-10 sRGB sun color and strength, clips at 1 and decodes), since the same add in linear light lost the glare toward the sun. Both measure depth from the **local** water surface, never the sea plane: the caustics from the surface baked into each submerged face's vertex (`Vertex.WaterSurface`, read as `fWaterSurfaceY`), the rays from `Camera.EyeWaterSurfaceY` (`ChunkManager.GetFluidColumnTopGlobal` over the eye's column), so a pond above sea level gets both — only the planar mirror still uses the sea plane. The rays are why the shadow map keeps rendering underwater (`FrameLighting.ShadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=360`) frames the shallows, the grazing surface, deep water from height and the underwater shots, including the surface seen from below in the sea and in a pond above sea level (a liquid one — a frozen pond has an ice lid, not a water top face).

**Deep-water darkness.** The underwater fog darkens and loses red the deeper the eye is below its
own water surface: `RenderManager.BeginFrame` reads the depth as `Camera.EyeWaterSurfaceY` minus the
eye (`RenderEye`) while the eye is in water and not lava, and `ApplyFrameUniformsTo` uploads
`DeepWaterFog.Dim(depth)` (eased in with the underwater tint, zero in lava) as `uDeepWaterDim`. The
world fragment multiplies the wet fog color by `1 - uDeepWaterDim` in the fog mix, and the Snell
window's total-internal-reflection fallback (which stands in for the water body below) the same way.
The curve is `DeepWaterFogMaxDim * (1 - exp(-loss * depth / DeepWaterFogScaleBlocks))` per channel
with `DeepWaterFogLoss` ordered red first, so it is zero at the surface, never decreasing, takes red
first and never goes pitch black (`DeepWaterFogTests`). `visual-examples/deep-water.json`
(`VOXEL_TIME=147`) frames the warm sea at 1.5, 5.5 and 9 blocks down and a frozen deep basin at 3 and
26.

**Sun glitter.** On top of the water's smooth sun highlight (`waterSpec`) the fancy water path adds
hashed sparkles along the sun's reflection path (`WaterGlitterAt`): the water plane is cut into
`WaterGlitterCellsPerBlock` cells per block, a `WaterGlitterDensity` share of which carry a tiny facet
tilted up to `WaterGlitterTiltRadians` off the wave normal (`Pcg3d`, like the snow glint), and a facet
mirrors the sun through a narrow lobe on the eye/sun half vector, so the sparkles gather where the
waves already face it. The light is a small round point at a hashed spot in its cell, each cell
re-rolls its facet `WaterGlitterRate` times a second on its own phase and fades in and out within each
roll, so they dance without popping; added as `WaterGlitterStrength` times the direct sun, gated by
the shadow and skylight, a risen sun and a fade between `WaterGlitterFadeNearBlocks` and
`WaterGlitterFadeFarBlocks`, top faces only, never from below (`fromBehind`) nor with the eye
underwater. `WaterGlitter` is the GL-free twin (`Sparkle`, `Life`, `Spot`, `Gate`;
`WaterGlitterTests`); `uWaterGlitter` 0 switches it off. `visual-examples/water-glitter.json`
(`VOXEL_TIME=360`) frames the reflection path over the warm sea twice 0.3 s apart, away from the sun
and from below.

**Linear lighting (SH-10).** The world is lit in linear light and rendered into the HDR scene
target `RenderTargets` owns — an `RGBA16F` color texture plus a sampleable `DepthComponent24`
depth texture (`RenderTargets.CreateHdrTarget`, which also builds `HdrColorOnlyFbo` over the color
alone for the contact-AO pass; recreated with the viewport like the reflection, scene-copy and
rays targets). `RenderManager.BeginFrame` binds and clears it, and **every mid-frame restore goes
back to it, never to framebuffer 0** (`RenderTargets.BindSceneTarget` at the tails of
`ShadowPass.RenderShadows` and `ReflectionPass.RenderReflection`, the `Create*Target` recreate
paths, the `UnderwaterRaysRenderer` composite); only `CreateHdrTarget`/`DeleteHdrTarget`,
`ScreenPasses.RenderPostProcess` and the world-less `UiPasses.RenderMenuFrame` bind 0. Color textures sampled
before the tonemap are uploaded as `SRGB8_ALPHA8` (the `srgb` flag on `TextureLoader` and
`TextureAtlas.Build`: the block atlas, `ItemMeshCache`, skins, sun, break progress, bubbles, the
world healthbar hearts) so the sampler decodes them; the material/normal atlas is data and stays
plain `Rgba`, and so do UI-only textures (font, the crosshair and HUD hearts `HudRenderer` loads, `UIRenderer`'s own item icons).
CPU colors are authored in sRGB and go through `ColorSpace.SrgbToLinear` where they are computed
or uploaded (the sun, ambient and sky palette in `FrameLighting.SetLighting`, torch, point lights,
water scatter, molten tints, selection outline); the selection *face* highlight is the one overlay
mixed in display space instead (`selection-fragment.glsl`'s `uDisplayMix`: it reads the HDR target
it draws into after a `glTextureBarrier`, blend off), since a linear 15% blend turned any dark
targeted face into a flat gray, and the cloud layer does the same (`cloud-fragment.glsl` lights,
fogs and mixes in display space over the target it reads through `RenderManager.SceneReadUnit`,
the HDR target or the mirror; the depth pre-pass lets one surface per pixel through), since a
linear blend left the clouds opaque, cream slabs; the block-light colors are the exception that is
uploaded pre-linearized: `BlockLightPalette.Linear` goes up once per frame as the
`uBlockLightPalette[8]` array, `test-vertex.glsl` decodes each chunk vertex's tint code into the
smooth `fBlockTint` (mirroring `LightTint.Decode`), the plain (entity) path reads
`uBlockLightPalette[uEntityTint]`, and the fragment's torch term is `fBlockTint * pow(...)` in
place of the old single `uTorchColor`; a GLSL constant that stands for one is written as its
decoded value with the sRGB original in a comment. At the end of `WorldRenderer.Render` the `postPass`
section calls `ScreenPasses.RenderPostProcess`, which binds framebuffer 0 and runs
`PostProcessRenderer` (`post-fragment.glsl`): exposure (`LightingConfig.TonemapExposure`),
a per-channel clamp to 0..1 (what the old 8-bit target did, so snow and white sand clip to white
rather than staying cream; `Tonemap.cs` is its tested C# twin), the exact sRGB encode, and the frame's one
half-LSB dither. FXAA (SH-9) runs unconditionally inside the same pass, on display-space
(tonemapped, encoded) taps filtered by hand from `texelFetch`, before the dither (knobs
`PostProcessConfig.Fxaa*`; its contrast early-out keeps it near 1 ms at 1080p). The UI draws
after it, straight into the gamma default framebuffer, so text and icons stay crisp and HUD
colors stay display-space; the two textures it shares with the world (the atlas and the item
meshes) are re-encoded in `ui-fragment.glsl` behind `uSrgbTex` (`UIRenderer`'s `atlasIsSrgb`
constructor flag, `ItemMeshCache.IsSrgb`), so icons write the same bytes as before.
`visual-examples/linear-lighting.json` (run at `VOXEL_TIME` 147, 440 and 700) frames the surface,
the Snell window, the lava cave with and without the HUD and a high LOD/fog vista for comparing a
lighting change against earlier shots.

**World fog** is the squared linear ramp `f*f` over `FogNear..FogFar` toward `SkySample` in the
view direction, reaching exactly the sky at FogFar so the LOD edge stays seamless. In dry air it is
mixed in **display space** (`test-fragment.glsl` encodes the fragment, clamped to 1, and the fog
color, mixes, and decodes), because the same mix in linear light reads visibly hazier than the
pre-SH-10 look; underwater and in lava it stays the linear mix toward `uFogColor`, and the two are
blended by `uFogWet` rather than branched so the transition never pops. Cloud fog is display
space too (see the cloud layer above); particle and healthbar fogs are still linear.

**Stars.** The night dome adds a star field in `sky-fragment.glsl`'s `main` only, after
`SkySample`, so the fog and `SkySample`'s three twins never see it. Stars live on a grid over the
sphere of directions (cube face, equi-angular warp, `StarCellsPerFace`² cells, each cell's chance
scaled by its solid angle), one star per cell kept `StarField.CellMargin` clear of the edges, drawn
as a pixel-sized gaussian (`fwidth` of the view ray), so they never swim as the view turns. They turn with the sky
instead: the grid is read in star space, the world direction turned by -`DayCycle.OrbitAngle` about +Z (`uStarTurn`,
twin `StarField.ToStarSpace`), the sun's own orbit run backward, so the sun and the moon opposite it keep their
place among the stars while the whole field rises and sets once a day. Each
star twinkles smoothly on its own rate and phase hashed from its cell (`StarField.Twinkle`, the
`uStarTime` clock wrapped hourly). Visibility fades over dusk
and dawn by the sun's elevation (`StarField.Visibility`), out into the horizon haze and around the
sun. Clouds hide them: the dome samples the cloud-shadow map (unit 8) cell by cell along each view
ray (`CloudCoverAlongRay`), since the drawn clouds alone cover at their opacity. The mirror pass
redraws the dome, so the stars reflect, and the Snell window shows them. GL-free twins:
`StarField` (cells, hash, star placement) and `CloudCover` (the cover along a ray); keep them in step
with the shader (`StarFieldTests`, `CloudCoverTests`). `visual-examples/night-sky.json` (`VOXEL_TIME`
700, 450, 147, and 350 for the glare) frames them.

**Shooting stars.** A rare streak across the night dome, added in the dome `main` right after the stars
(`ShootingStar` in `sky-fragment.glsl`, so the fog never sees it; the same fade, sun and moon masks and
`CloudCoverAlongRay` as the stars). Time is cut into `ShootingStarSlotSeconds` slots of the star clock (the
slot length divides its hourly wrap); a slot holds a streak with probability `ShootingStarChance`, hashed from
its index, which starts at a hashed moment inside the slot and lives `ShootingStarLifeSeconds`, so a pixel
only ever reads one slot, and the chance and life-window tests come before any trigonometry. The head slides
`ShootingStarArcDegrees` along a great circle through a hashed point of the upper sky (`ShootingStarMin/MaxSinElevation`)
in a hashed heading, a tail of `ShootingStarTrailDegrees` fading quadratically behind it, a pixel-relative
gaussian across, eased in and out over its life. It stays fixed in world directions. GL-free twin
`ShootingStar` (`ShootingStarTests`); `visual-examples/shooting-stars.json` (`VOXEL_TIME=600`).

**Sun glare.** A wide, faint glow around the sun, added in the same dome `main` (`uGlareColor` times
`cos(angle to the sun)^SunGlareExponent`, `SunGlare.Radial`). Its strength is computed once a frame
on the CPU (`SunGlare.Intensity`, capped at `SunGlareStrength`): how directly the camera faces the
sun, the sun's height (zero at and below the horizon) and the cloud over its disc
(`CloudCover.AcrossDisc` over `CloudShadowTexture.Coverage`, eased). It is a camera effect: zero
underwater and in lava, and the mirror pass draws its dome with `GlareColor` zero, so no reflection
ever carries it.

**Sunset glow.** The one direction-dependent part of the otherwise flat dome (this supersedes the
"zero glow" above): `SkyPalette.Compute`'s `glowAmount` scales a warm sRGB band
(`SkyDuskGlowStrength`) that every `SkySample` copy already adds low toward the sun's azimuth
(`Glow * lobe`). `SkyPalette.GlowAmount(sunElevation)` gives it a value only while the sun crosses
the horizon, at sunset and sunrise alike: zero at and above `SkyGlowAboveSunElevation` (the flat day
sky) and at and below `SkyGlowBelowSunElevation` (the afterglow gone), peaking at
`SkyGlowPeakSunElevation`. The world and cloud fog sample the same band, so fogged terrain toward
the sun melts into it. `SkyPaletteTests` holds the rules; `visual-examples/sunset-glow.json`
(`VOXEL_TIME` 430, 147, 1030) frames it.

**Cloud silver lining.** Thin cloud edges near the sun light up: `cloud-fragment.glsl` adds the
display-space sun term times `uSilverStrength` times the forward lobe `cos(view, sun)^CloudSilverLiningExponent`
times how thin the cloud is behind the lit surface along the view ray (`CloudThinness`: the share of
`CloudSilverLining.Samples` points `CloudSilverLiningStepBlocks` apart that are out of the cloud
volume, read cell by cell from the cloud-shadow coverage map on unit 8 like the stars' cloud cover,
so `CloudRenderer` now takes that unit). The frame's strength is `CloudSilverLining.Strength` of the
sun's elevation (zero at and below the horizon), set on `CloudRenderer.SilverLining` by
`FrameLighting.SetLighting`; the lobe and thinness are computed only where the lobe is non-zero.
GL-free twin `CloudSilverLining` (`CloudSilverLiningTests`); `visual-examples/cloud-silver-lining.json`
(`VOXEL_TIME` 343, 400, 147) frames it.

**Moon (V-6).** `SkyPasses.RenderMoon`, called by `SkyPasses.RenderSky` right after `RenderSun` (so the
mirror draws it too and the Snell window shows it through the scene copy), draws the sun's quad and
`sun-vertex.glsl` again opposite the sun (`-FrameLighting.SkySunDir`), additively and depth-free,
with the full-disc sprite `assets/textures/environment/moon.png` and `moon-fragment.glsl`, which
shades the phase over it: each disc point gets a sphere normal and is lit where it faces
`MoonPhase.LightDirection`, softened over `MoonTerminatorSoftness`, the unlit part keeping
`MoonEarthshine`; a faint halo grows with the lit share. `MoonPhase` (GL-free) picks the phase:
`DayIndex` counts whole days from the first noon (`DayCycle.NoonTime`), so it steps at noon while
the moon is below the horizon, day 0 (a new world's first night) is full and the cycle is
`MoonCycleDays` long. It fades in and out with the stars (`MoonPhase.Visibility` =
`StarField.Visibility`) and out in lava, and the dome hides the stars behind its disc (`uMoonMask`,
`SkyDomeFrame.MoonVisibility`). `MoonPhaseTests` (the phase never changes in view, the shaded share
matches `IlluminatedFraction`, waxing lit on the right); `visual-examples/moon.json` (`VOXEL_TIME`
700 full, 3100 last quarter, 4300 crescent, 5500 new, 7900 first quarter) frames it.

**Moonlight.** At night the moon is the world's **key light**: `FrameLighting.SunDir` (what
`uSunDir`, the shadow cascades, the clouds, the leaves and the rays read) is `Moonlight.Direction`,
the sun by day and the moon (the sun negated) once the sun is at or below
`MoonlightSwitchSunElevation`, while the sky (the dome, the sun and moon discs, the stars' turn, the
glare) keeps the true sun in `SkySunDir`, and the sun disc its own `SunDiscColor`. So the world
shader's `uSunColor`/`uSunIntensity` are the moonlight's at night: `Moonlight.ColorSrgb` (cool) at
`Moonlight.Intensity` (`MoonlightIntensity`, eased in by `Moonlight.Rise` as the moon climbs and
scaled by the phase, `MoonlightNewMoonShare` at a new moon), and the shadow strength follows the key
light's own elevation, so a night keeps soft shadows (the shadow passes now run at night too). The
handover never pops: the sun's light eases out below the horizon (`Moonlight.SunFade`, which also
removes the old night floor that lit the world from under the ground) and the moon's eases in from
the switch, both zero there. The switch sits at or below `SkyGlowBelowSunElevation`, because the
world and cloud `SkySample` copies read `uSunDir`: with no glow left, the key light's direction
changes nothing in the fog. The night ambient is a touch bluer. `MoonlightTests`;
`visual-examples/moonlight.json` (`VOXEL_TIME` 455 the handover, 950 a low moon, 700, 5450 a new moon).

**Aurora.** Slow green and violet curtains in the night dome, seen only from cold lands:
`AuroraCurtains` in `sky-fragment.glsl`'s `main`, after the stars (so the fog and the `SkySample`
twins never see it), hidden behind the clouds by the same `CloudCoverAlongRay`. Broad sectors along
the horizon come from a periodic value noise of the view azimuth, with fine vertical folds, a wavy
bright lower edge and a glow fading up from green to violet; each noise's drift is wrapped into its
period on the CPU (`Aurora.Drift`, `Aurora.Periods`), so it never jumps. The strength
(`SkyDomeFrame.Aurora`, `Aurora.Visibility`) is the eye's coldness, eased (`AuroraColdEaseRate`) and
ramped over `AuroraColdStart..AuroraColdFull`, times `StarField.Visibility`. The coldness comes from
`EyeClimate.TryAt` over `RenderManager.LightSource.ColumnSource` (the stored corner climate of the
eye's column, bilinear like the biome tint, read back through `EyeClimate.Coldness`), never from
worldgen noise, so a client reads its server's climate; an unknown column eases it out. The mirror
redraws the dome, so it reflects, and the Snell window shows it. `AuroraTests`;
`visual-examples/aurora.json` (`VOXEL_TIME` 700) films it over a frozen plain and back at the
temperate origin, where it has faded out.

**Eye adaptation.** The post pass's exposure (`uExposure`, now set per frame by
`PostProcessRenderer.Render(viewport, exposure)`) is `TonemapExposure` times `FrameLighting.EyeExposure`,
which its `SetLighting` eases each frame (`EyeAdaptation.Step`: `EyeAdaptToDarkRate` while it rises, the
faster `EyeAdaptToLightRate` while it falls) toward `EyeAdaptation.Target` of the voxel light at the
eye's cell (`EntityLight.Sample(LightSource, camera.Position)`, sky and block light, no GPU readback):
exactly 1 once the brighter of the two reaches `EyeAdaptFullLight` (open sky at any hour, a lamp-lit
room), up to `EyeAdaptMaxExposure` in total darkness. An unknown cell reads as open sky.
`EyeAdaptationTests`; `visual-examples/eye-adaptation.json` (`VOXEL_TIME` 147) films a dark tunnel on
arrival and adapted, then the over-bright step back outside settling within a couple of seconds.

**Realm layer fog.** The eye's height colors the sky around it (plans/LAYERS.md): below the barrier
band, in hell, `FrameLighting.SetLighting` eases `_hellT` (`LayerClimate.HellTarget`: exactly 0 at and
above hell's top, ramped to 1 over `SkyConfig.HellFogRampBlocks` below it; `LayerClimate.Ease` at
`LayerFogEaseRate`) and above the solid cloud slab `_altitudeT` (`AltitudeTarget`, full at the sky
islands). `LayerClimate.Apply` shifts the sRGB `SkyPalette` before it is decoded: the altitude multiplies
it by `SkyAltitudeTint` (a slightly deeper, brighter day blue; night stays dark), hell mixes zenith and
horizon toward `HellFogColor` and drops the glow. Every `SkySample` copy reads the shifted palette
through its usual uniforms, so the dome, the dry world fog, the cloud fog and the clear color all turn
red with no shader change. Hell also lerps the dry fog toward `HellFogNearBlocks..FarBlocks` and fades
the sun, moon, stars, aurora, glare and godrays out (`1 - _hellT`); water and lava still override it.
`LayerClimate.HellLayerIndex` counts the hell layers down from the barrier. `LayerClimateTests`;
`visual-examples/layers-fog.json` (`VOXEL_TIME=147`).

**Cave biome atmosphere (CB-3).** The cave biome at the eye (see "Cave biomes") colors the fog around it:
`CaveClimate.Target` maps each `CaveBiome` to a `CaveAtmosphere` (`Effects/Sky/`, GL-free): a strength in
0..1 plus a dim sRGB fog color, fog near/far distances and a per-channel ambient tint, those four stored at
full strength and every effect lerped from neutral by the strength, so plain caves (strength 0) change
nothing. `FrameLighting.SetLighting` (its optional `eyeCaveBiome`, `World.EyeCaveBiome` from `Program`) eases
`_cave` toward it beside `_hellT`/`_altitudeT` (`CaveClimate.Ease` at `SkyConfig.CaveFogEaseRate`: a fade-out
keeps the hue, a fade-in from nothing takes the target hue at once) and, gated by `1 - _hellT`, applies it
three ways: `CaveClimate.Apply` shifts the sRGB `SkyPalette` after `LayerClimate.Apply` (zenith and horizon
toward the fog color, the glow out), which is what colors the dry fog, since dry fog reads `SkySample` from
the palette uniforms, not `uFogColor`, so no shader changes; `CaveClimate.FogNear`/`FogFar` pull the dry fog
toward `SkyConfig.CaveFogNearBlocks..FarBlocks` before the hell and water lerps; and `CaveClimate.AmbientFactor`
multiplies `_ambient` before the wet mix, so water and lava still win. `ForgetWorld` resets it. The eye's biome
is computed only where the world is authoritative, because a client has no seed and its `ColumnTerrain` has no
surface biome: `EyeCaveBiomeProbe` (`WorldGen/Caves/`, memoized per cell, plain at or below the barrier
without a read) runs `CaveBiomes.At` over `IWorldSource.GetColumn` in `LocalPlayer.UpdateHealth`
(singleplayer) and `ServerPlayer.TickStatus` (server, `StatusDirty` only when the byte changes). The byte
rides the end of `PlayerStatus` (protocol 28), which a client mirrors (`ClientMirror.ApplyNetworkCaveBiome`
-> `LocalPlayer.MirrorCaveBiome`). `Classify` already gives plain above the surface, inside the echo cover, in
hell and in the sky, so the atmosphere is zero there. Each biome also drifts motes around the eye
(`CaveClimate.Motes`, `AmbientEffects.UpdateCaveMotes` on the campfire `FireParticleSystem`: embers in the
magma deep, sparks in crystal and fungal caves, steam in a frozen echo, smoke dust in a desert echo; paced by a
debt and capped at `ParticleConfig.CaveMoteMaxPerFrame`, singleplayer and a client, never a server).
`CaveClimateTests`, `EyeCaveBiomeProbeTests`, `ServerPlayerCaveBiomeTests`, the cave byte in
`NetworkProtocolTests`; `visual-examples/cave-biomes.json` (`VOXEL_TIME=147`, each biome's fog and motes, with its
mob idle in the three sub-biomes).

**Biome color grade.** The post pass multiplies the finished, anti-aliased display image by
`uGrade` (`PostEffects.Grade`, (1, 1, 1) by default): `BiomeGrade.Tint` of the eye's climate (the same
`EyeClimate.TryAt` sample the aurora reads), warm where it is warm and dry (the desert's own
weighting) and cool where it is cold enough for snow (`BiomeGradeColdStart..ColdFull`), each channel
moving at most `BiomeGradeStrength`; exactly neutral across the temperate band. `FrameLighting.Grade`
eases toward it (`BiomeGradeEaseRate`) and back to neutral over an unknown column, so a client
without its column yet never guesses. `BiomeGradeTests`; `visual-examples/biome-grade.json`
(`VOXEL_TIME` 147) compares the noon sky over the origin, a deep desert and a frozen plain.

**Damage chromatic fringe.** For a moment after a hit the screen edges split red and blue:
post-fragment.glsl reads red and blue from either side of each pixel along the line from the screen
center (`DisplayBilinear`, after FXAA, before the grade), by `uDamageFringe` times
`DamageFringe.EdgeWeight` (nothing in the middle, full at the corners) times `DamageFringeScreenShare`
of the screen height. The strength is `DamageFringe.Strength` of the vignette's own damage flash
(`PlayerHealth.DamageFlash`, which `WorldRenderer.Render` now hands to `ScreenPasses.RenderPostProcess`), so
it lasts only the first `DamageFringeSeconds` of the flash; a client mirrors the flash with its health.
`DamageFringeTests`; `visual-examples/damage-fringe.json` (`VOXEL_TIME` 147).

**Underwater wobble.** With the eye under water (`PostEffects.Underwater`, the eased underwater amount
the world shader's `uUnderwater` also carries, 0 in lava) the post pass moves the point each pixel
shows by `UnderwaterWobble.Offset` (two slow waves across the screen, up to
`UnderwaterWobbleScreenShare` of its height) and hands that point to FXAA, so every tap reads around
the swayed coordinate; a no-edge pixel then returns its bilinear color at the point rather than the
nearest texel, so the sway never steps a whole pixel. The clock is wrapped on the CPU at the waves'
common period (`UnderwaterWobble.WrapTime`), so it never jumps. `UnderwaterWobbleTests`;
`visual-examples/underwater-wobble.json` (`VOXEL_TIME` 147).

**Godrays.** Light shafts through trees, clouds and gaps in dry air when the sun is in view:
`ScreenPasses.RenderGodrays` (`GodraysRenderer`), its own `godPass` profiler row right after
`raysPass`, switched off by `GodraysEnabled`. Two draws. The march (`godrays-fragment.glsl`) is the
underwater shafts' march run over air into their half-resolution target (borrowed: the two never
need it at once): each pixel's ray up to `GodraysMaxDistance` (inside the near cascade) in
`GodraysSteps` jittered samples, summing the sunlit blocks that pass the near shadow map and the
cloud shadow up the sun ray (`GodraysCloudShadow`, unit 8), plus `GodraysSkyBlocks` for a ray that
reaches open sky (depth at the far plane: nothing drawn there, no terrain, tree or cloud), turned
into light by the saturating `Godrays.Scatter` and the forward `Godrays.Phase`. It reads the HDR
scene's own depth texture (no scene copy). The composite (`godrays-composite-fragment.glsl`)
averages that along the line from each pixel toward the sun's place on screen (`GodraysBlur*`), so
the contrast between long sunlit rays and short or shadowed ones streaks outward from the sun as
beams, and adds it in display space through the color-only HDR framebuffer like the underwater
shafts. The frame's strength is `Godrays.Intensity` (set by `FrameLighting.SetLighting`): facing the sun
within `GodraysFacingDegrees`, strongest for a sun near `GodraysPeakElevation` and `GodraysNoonShare` of
that overhead, zero at and below the horizon, dimmed by the eased cloud over the sun's disc and
zero under water (where the underwater shafts take over); the pass is also skipped while the
shadow passes do not run. Cost on the dev GPU at 1080p: about 0.35 ms GPU (under 0.8 ms worst)
and 0.02-0.05 ms CPU while active, nothing otherwise. `GodraysTests`;
`visual-examples/godrays.json` (`VOXEL_TIME` 395, 425, 147, with `VOXEL_DEBUG=1` for the cost).

**Wind (SH-27).** `Wind.cs` is the one wind: a GL-free pure function of the lighting clock whose
heading turns slowly (`SkyConfig.Wind*`), with `Wind.Offset` its bounded closed-form
integral. Leaf sway reads it in both vertex shaders (`uWind`, the direction scaled by `WindLean`,
leans the canopy downwind; `uWindOffset` advects the gust pattern, so the gust phase rate is the
same anywhere in the world — keep the two sway blocks byte-identical), fire smoke and embers drift
along it (`FireParticle.Update`'s `wind`), falling leaves (ambient and burst) ease their sideways
motion into it, pushed by the canopy's own gusts (`LeafParticleSystem.Update`'s `windTime`,
`Wind.Gust`, the C# twin of the sway's `0.6 + 0.4 sin(phase)`; a landed leaf stays put;
`visual-examples/leaf-wind.json` at `VOXEL_TIME` 147 and 1347), and the cloud layer and its shadows are carried by it
(`CloudCopies.Drift`, wrapped modulo the field on both axes; `uCloudDrift` is a `vec2`). `World`
gets the same lighting clock through `World.TimeOffset`, which `Program` sets from `VOXEL_TIME`, so
leaves, smoke and clouds agree; `FrameLighting.WindDirection` exposes the unit direction for future
rain and snow.

**Contact AO (SH-28).** A cheap screen-space contact AO darkens the crevices the baked vertex AO
cannot see — where pebbles, branch piles and other per-quad emitters meet the ground, and inner
corners between greedy quads. `ScreenPasses.RenderContactAo` (`ContactAoRenderer`,
`contact-ao-fragment.glsl`, GL-free twin `ContactAo`) runs mid entity pass as its own `aoPass`:
after everything that writes honest depth (terrain, drops, stations, TNT, falling blocks, slimes, goblins)
and before the depth-less or blended draws (healthbars, the first-person head, debris, toppling
trees, player, held item), the selection outline, water (whose refraction copy then carries the AO),
clouds and particles. It samples the HDR depth texture through the color-only
`RenderTargets.HdrColorOnlyFbo` (no feedback loop) with multiplicative blending (`Zero, SrcColor`). Four taps
(plus the four 1-pixel neighbors) per pixel, `ContactAoRadiusBlocks` wide in the world (clamped to `ContactAoMaxPx`): for each opposite
pair, the midpoint of their raw depths is reconstructed on the center ray and compared with the
center. Raw depth is affine in screen space over a plane, so planes never darken at any angle;
a concave corner creases positive, convex edges and far silhouettes are rejected by the
`ContactAoMinBlocks..RangeBlocks` window, and it fades out by `ContactAoFadeFar`. A thin object
standing in front of the ground (a torch, a drop, a mob) would crease the ground a tap radius beside
it exactly like a corner and leave displaced copies of its silhouette there, so each pair's crease is
split between its two taps along the center surface's own slope (read from the four 1-pixel
neighbors; of the two one-sided slopes the one that carries the surface out to its own tap wins, the
stronger crease on a tie), and a tap in front of that surface only counts while what it sees lies
within `ContactAoReachNear..ReachFar` tap radii of the center point (`ContactAo.Reach`). A wall or
pebble beside the point is that close, the upper part of a post is not; with every occluder in reach
the crease is exactly the plain midpoint crease. The cost is that a corner seen at a grazing angle
keeps a band about that reach wide in the world rather than a tap radius wide on screen.
`ContactAoEnabled = false` skips the pass entirely.

**Rarity chrome.** An item's rarity dresses its [E] list rows, its chest card rows and its hover
tooltip. `ui-fragment.glsl` has a `uChrome` mode with five vec4 uniforms (`uChromeBox`, `uChromeColor`,
`uChromeLayers`, `uChromeShape`, `uChromeMotion`). With `uChrome` 1 the quad is a row or the tooltip box:
`fTexCoord` runs 0..1 across it with y down, so the unit square needs no new vertex data. `RarityChromeRenderer`
(`Ui.Chrome`, built by `UIRenderer`) draws one such quad over the row or box fill and sets `uChrome` back to 0
right after it, so every rect, icon and text draw (and the ItemDebugger, where GL's default 0 holds) keeps the
plain path. The layers are a rarity-colored border band and a soft inner glow, both breathing by the pulse; a
wash fading in from the row's left or the tooltip's top; a slanted white sheen that sweeps across once a
`SheenPeriodSeconds` (`MathUtils.Sweep`, staggered `SheenRowStaggerSeconds` per row so it cascades down a list)
and rests between sweeps; and twinkling four-point sparkles in a hashed share of the cells, the hash rescaled so
the stars twinkle out of step. `RarityStyle.Of` maps a rarity to `Rarities.Color` and the layer strengths of
`RarityStyleConfig`: Common has none (no chrome draw, white name, the plain border), Uncommon the border and
wash, Rare adds the glow and the sheen, Epic the pulse, Legendary the sparkles. The time curves (the sweep, the
pulse, the twinkle phase) run on the CPU on the world's lighting clock; the shader does only the spatial work.
`RarityChrome` is the GL-free twin (`For` packs the uniforms, `Shade` is the fragment): the formula, not the bits,
since GPU `sin` differs, so `RarityChromeTests` asserts relations (see the shader-sync row). A dimmed row (an
uncraftable recipe) draws it at `DimOpacity`; the row's accent bar stays its status (equipped, ready, a recipe
hit) on top. `visual-examples/inventory-rarity.json` (`VOXEL_TIME=147`) films every tier.

**Selection outline pulse.** The targeted block's outline breathes between
`SelectionPulseMinOpacity` (70 %) and full opacity, one breath every `SelectionPulsePeriodSeconds`:
`selection-fragment.glsl` scales the outline's `uOpacity` by `1 - uPulseDepth * (0.5 + 0.5 sin)` of
`uTime`, and `SelectionPulse` is its GL-free twin (`Depth(mining)` is what
`SelectionPasses.RenderBlockSelection` uploads, `Opacity` the curve). While the block is being mined
(`LeftClickAction.IsBreaking`, passed as `RenderBlockSelection`'s `mining`) the depth is 0, so the
outline holds steady at full and the crack overlay stays readable; the face highlight (`uDisplayMix`
1) never pulses. The outline beams draw with back faces culled, so each pixel blends exactly one
face of a beam: with both faces blended an alpha `a` read as `1-(1-a)^2` and the breath all but
vanished. `SelectionPulseTests`, `visual-examples/selection-pulse.json` (`VOXEL_TIME=147`).

**Leaf backlight.** A leaf-sway face (bit 5: canopy leaves and rooted plants; `test-vertex.glsl`
hands the fragment the flag as the flat `fSwayFlag`, outside the guarded sway block) standing
between the eye and the sun glows warm, as if lit through: the world fragment adds
`albedo * LeafBacklightTint * sun` scaled by `LeafBacklightStrength` and a
`pow(dot(viewRay, sunDir), LeafBacklightPower)` lobe, gated by skylight, the cloud shadow, a risen
sun and dry eyes. Whether sunlight reaches the leaf from behind is the shadow map read
`LeafBacklightReachBlocks` toward the sun from it with the sun as the normal (the faces the eye sees
from the dark side read as self-shadowed at their own position), so a crown's thin, sunlit rim
lights up while leaves deep in its own shadow stay dark; the shadow pass is untouched.
`LeafBacklight.Weight` is the GL-free twin (`LeafBacklightTests`); `uLeafBacklight` 0 (the
ItemDebugger) switches it off. `visual-examples/leaf-backlight.json` (`VOXEL_TIME=1100`, a morning
sun behind an oak crown).

**Grass wind shimmer.** Soft bright bands lie across the wind on the grass tops and roll downwind
with the leaf sway's gusts: on a biome-tinted up face (bit 4, face 4; never a swaying face nor the
far LOD's crowns) the world fragment reads a crest pattern along `uWind` (normalized) at the
position less `uWindOffset` — the very offset the sway samples its gust wave at, which the fragment
stage now declares too — weights it by the sway's own gust (`0.6 + 0.4 sin(...)`) and brightens the
grass by `GrassShimmerStrength` of its own light, so the bands darken with shade and night. Bands
are `GrassShimmerWavelengthBlocks` apart and fade out between `GrassShimmerFadeNearBlocks` and
`GrassShimmerFadeFarBlocks`. `GrassShimmer` is the GL-free twin (its gust is `Wind.Gust`;
`GrassShimmerTests`: banded, carried exactly by the gust offset, faded, grass tops only);
`uGrassShimmer` 0 switches it off. `visual-examples/grass-shimmer.json` (`VOXEL_TIME=147`, three
shots of one meadow 0.7 s apart).

**Lava glow pulse.** Lava is the fluid path's `isLava` (a water-flagged quad on the lava tile,
`uLavaLayer`), whose glow is worked out in display space and clipped there. After that clip the
glow is multiplied by `1 + LavaPulseStrength * (2 * field - 1)`, where the field (`LavaPulseField`)
is two octaves of the shader's `ValueNoise` drifting slowly against each other, so broad patches of
the surface brighten and dim over seconds on top of the existing fast sine pulse; applied in linear
light, the dim phase reaches the clipped molten texels too. `LavaPulse` is the GL-free twin (its
`Hash12`/`ValueNoise` are float ports of the shader's; `LavaPulseTests`: bounded, identity at zero
strength, smooth, slow); `uLavaPulse` 0 (the ItemDebugger) switches it off. `lava.json` ends with
two shots of the pool 2.5 s apart (`pulse_a`, `pulse_b`).

**Glass sheen.** Glass blocks and panes share the glass tile (`uGlassLayer`) and draw blended in the
translucent pass; at a graze they mirror the sky. The weight is `GlassSheenStrength` times the
Schlick fresnel of `|V.N|` (a face seen from inside the cube reads the same), the sky is `SkySample`
of the reflected ray blended to `uFogColor` by `uFogWet` and dimmed by skylight. Tinting the color
alone would vanish under glass's low tile alpha, so the sky is folded into the blend: the fragment
writes alpha `a + F - aF` (`glassAlpha`, which replaces `texAlpha` at the end) and color
`(c a (1 - F) + sky F) / alpha`, which straight-alpha blended over any background is exactly the
plain glass composite mixed toward the sky by F. `GlassSheen` is the GL-free twin (`GlassSheenTests`
proves that identity); `uGlassSheen` 0 (the ItemDebugger) switches it off.
`visual-examples/glass-sheen.json` (`VOXEL_TIME=147`) places glass and panes on the frozen plain and
frames them head-on, grazing and from above.

Shaders (`assets/shaders/**/*.glsl`), textures (`assets/textures/**/*.png`), fonts (`assets/fonts/**/*.ttf`) and all content JSON (`assets/**/*.json` — blocks, items, loot tables, models) are copied to the output directory via `CopyToOutputDirectory` rules in the csproj — **new asset types/extensions need a matching `<None Update>` entry** or they won't be found at runtime. The JSON rule is a wildcard over the whole `assets` tree, so adding a block, item, loot table or model needs no csproj edit.

Assets are opened by **relative** path (`FileUtils.ReadFile("assets/shaders/...")`, `TextureAtlas.Build(gl, "assets/textures/blocks")` in `RenderManager.LoadResources`), so the app must be launched with a working directory that contains `assets/` — either the build output directory or `OpenGLTest/`. `dotnet run --project OpenGLTest/VoxelEngine.csproj` satisfies this (it runs from the project folder); launching the built executable from an arbitrary directory does not.
