using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Interaction;
using VoxelEngine.Lod;
using VoxelEngine.Networking.Client;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.SpellBuilder;
using VoxelEngine.UI.Forge;
using VoxelEngine.UI.Waystone;

namespace VoxelEngine.Rendering;

/// <summary>
/// The world's draw: every render pass of a frame in order (shadows, reflection, far LOD, chunks,
/// entities, contact AO, the selection overlay, water, clouds, particles, rays, godrays and the post
/// pass) and the HUD over it. Owned by <see cref="World"/>, which hands it every list and system it
/// draws; Program calls <see cref="Render"/> and <see cref="RenderUI"/> from its render callback, on
/// the main thread only.
/// </summary>
internal sealed class WorldRenderer
{
    private readonly World _world;
    private readonly IWindow? _window;
    private readonly ChunkManager _chunkManager;
    private readonly LodManager _lodManager;
    private readonly DropManager _dropManager;
    private readonly TntManager _tntManager;
    private readonly FallingBlockManager _falling;
    private readonly GoblinManager? _goblins;
    private readonly CampfireManager _campfires;
    private readonly QuernManager _querns;
    private readonly KilnManager _kilns;
    private readonly CastingManager _casting;
    private readonly List<TopplingTree> _toppling;
    private readonly List<ThrownProjectile> _projectiles;
    private readonly List<RemotePlayer> _remotePlayers;
    private readonly BlockDebrisSystem _debris;
    private readonly LeafParticleSystem _leaves;
    private readonly FireParticleSystem _fire;
    private readonly FireParticleSystem _lavaFire;
    private readonly FireParticleSystem _torchFire;
    private readonly BubbleParticleSystem _bubbles;
    private readonly SliceTrailSystem _sliceTrails;
    private readonly PlayerHealth _health;
    private readonly HotbarNameFade _hotbarName;
    private readonly HotbarPickupPop _hotbarPops;
    private readonly StaffSlotIndicator _staffSlot = new();

    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;

    // The armor look is rebuilt only when the worn set changes
    private int _armorVersion;
    private bool _armorSeen;
    private HudToast Toast => _world.Toast;
    private IReadOnlyList<Slime> Slimes => _world.Slimes;
    private IReadOnlyList<Goblin> Goblins => _world.Goblins;
    private IReadOnlyList<Mob> Mobs => _world.Mobs;
    private DamageNumberPool DamageNumbers => _world.DamageNumbers;
    private double Now => _world.Now;
    private double LightingTime => _world.LightingTime;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private LeftClickAction LeftClick => _world.LeftClick;
    private BlockPlacer Placer => _world.Placer;
    private BlockEditor Edits => _world.Edits;

    internal WorldRenderer(World world, IWindow? window, ChunkManager chunkManager, LodManager lodManager,
        DropManager dropManager, TntManager tntManager, FallingBlockManager falling, GoblinManager? goblins,
        CampfireManager campfires, QuernManager querns, KilnManager kilns, CastingManager casting,
        List<TopplingTree> toppling, List<ThrownProjectile> projectiles, List<RemotePlayer> remotePlayers,
        BlockDebrisSystem debris, LeafParticleSystem leaves, FireParticleSystem fire, FireParticleSystem lavaFire,
        FireParticleSystem torchFire, BubbleParticleSystem bubbles, SliceTrailSystem sliceTrails, PlayerHealth health,
        HotbarNameFade hotbarName, HotbarPickupPop hotbarPops)
    {
        _world = world;
        _window = window;
        _chunkManager = chunkManager;
        _lodManager = lodManager;
        _dropManager = dropManager;
        _tntManager = tntManager;
        _falling = falling;
        _goblins = goblins;
        _campfires = campfires;
        _querns = querns;
        _kilns = kilns;
        _casting = casting;
        _toppling = toppling;
        _projectiles = projectiles;
        _remotePlayers = remotePlayers;
        _debris = debris;
        _leaves = leaves;
        _fire = fire;
        _lavaFire = lavaFire;
        _torchFire = torchFire;
        _bubbles = bubbles;
        _sliceTrails = sliceTrails;
        _health = health;
        _hotbarName = hotbarName;
        _hotbarPops = hotbarPops;
    }

    internal void Render(RenderManager renderManager)
    {
        // Depth-only sun pass first: every world draw below samples the shadow map it fills. It
        // times itself as the shadowNear and shadowFar rows.
        renderManager.IcebergPasses.Time = _world.LightingTime;
        renderManager.Shadows.RenderShadows(Camera, _chunkManager.Renderable, _tntManager.Primed, _falling.Falling, _toppling, Player, Slimes,
            _lodManager.RegionList, goblins: Goblins, icebergs: _world.Icebergs, hellMobs: _world.HellMobs,
            skyMobs: _world.SkyMobs, sandworms: _world.Sandworms, skeletons: _world.Skeletons,
            skeletonKings: _world.SkeletonKings, caveMobs: _world.CaveMobs, wildlife: _world.Wildlife,
            chestChunks: _chunkManager.ChestChunks, chestLids: _world.ChestLids);

        FrameProfiler.Begin(ProfileSection.Reflection);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.Reflection);
        // One frustum pass over the translucent draws feeds both this and the water pass, and
        // tells the reflection whether there is any water on screen to be worth rendering
        renderManager.Terrain.CullWater(Camera, _chunkManager.TranslucentRenderable, _lodManager.RegionList);
        renderManager.Reflection.RenderReflection(Camera, _chunkManager.Renderable, _lodManager.RegionList,
            new ReflectionEntities(_dropManager.Drops, _projectiles, _tntManager.Primed, _falling.Falling, Slimes, Goblins,
                _toppling, Player, _world.Icebergs));
        renderManager.GpuTimer.EndGpuPass(ProfileSection.Reflection);
        FrameProfiler.End(ProfileSection.Reflection);

        FrameProfiler.Begin(ProfileSection.LodPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.LodPass);
        renderManager.Terrain.RenderLod(Camera, _lodManager.RegionList);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.LodPass);
        FrameProfiler.End(ProfileSection.LodPass);

        FrameProfiler.Begin(ProfileSection.WorldPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.WorldPass);
        renderManager.Terrain.RenderWorld(Camera, _chunkManager.Renderable, Now);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.WorldPass);
        FrameProfiler.End(ProfileSection.WorldPass);

        // The frustum test inside RenderWorld has just counted what is on screen
        FrameProfiler.SetCounts(_chunkManager.LoadedChunkCount, renderManager.Terrain.LastVisibleChunkCount,
            _lodManager.CompletedBuildCount, _lodManager.RegionList.Count);

        // Free-list instrument for item D5, in the fixed order opaque/translucent/lod. Same call
        // site and same frame position as the counts above: the pools are render-thread-only and
        // this is where they already live.
        FrameProfiler.SetGeometryCounts(
            renderManager.OpaqueChunkGeometry.FreeBlockCount,
            renderManager.TranslucentChunkGeometry.FreeBlockCount,
            renderManager.LodGeometry.FreeBlockCount,
            renderManager.OpaqueChunkGeometry.PeakFreeBlockCount,
            renderManager.TranslucentChunkGeometry.PeakFreeBlockCount,
            renderManager.LodGeometry.PeakFreeBlockCount,
            renderManager.OpaqueChunkGeometry.ScanSteps,
            renderManager.TranslucentChunkGeometry.ScanSteps,
            renderManager.LodGeometry.ScanSteps);

        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.EntityPass);
        renderManager.Objects.RenderItemDrops(Camera, _dropManager.Drops);
        renderManager.Objects.RenderThrown(Camera, _projectiles);
        renderManager.Stations.RenderCampfireCooking(Camera, _campfires);
        renderManager.Stations.RenderQuerns(Camera, _querns);
        renderManager.Stations.RenderKilns(Camera, _kilns);
        renderManager.Stations.RenderSmithing(Camera, _world.Smithing);
        renderManager.Stations.RenderCastings(Camera, _casting);
        renderManager.Chests.RenderChests(Camera, _chunkManager.ChestChunks, _world.ChestLids);
        renderManager.Objects.RenderPrimedTnt(Camera, _tntManager.Primed);
        renderManager.Objects.RenderFallingBlocks(Camera, _falling.Falling);
        renderManager.Mobs.RenderSlimes(Camera, Slimes);
        renderManager.Mobs.RenderGoblins(Camera, Goblins);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.HellMobs);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.SkyMobs);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.Wildlife);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.Skeletons);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.SkeletonKings);
        renderManager.Mobs.RenderRealmMobs(Camera, _world.CaveMobs);
        renderManager.Mobs.RenderSandworms(Camera, _world.Sandworms);
        renderManager.IcebergPasses.RenderIcebergs(Camera.GetViewProjection(), _world.Icebergs);
        renderManager.Players.RenderRemotePlayers(Camera, _remotePlayers);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // Contact AO reads the scene depth, so it goes after everything above (which writes
        // honest depth) and before the healthbars and first-person head (drawn without depth, so
        // terrain AO would smudge them) and every later pass. Debris, toppling trees, the player
        // and the held item simply receive none.
        FrameProfiler.Begin(ProfileSection.AoPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.AoPass);
        renderManager.Screen.RenderContactAo(Camera);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.AoPass);
        FrameProfiler.End(ProfileSection.AoPass);

        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.EntityPass);
        renderManager.Objects.RenderBlockDebris(Camera, _debris.Debris);
        if (_world.CollapseEffects is { } collapse)
            renderManager.Objects.RenderCollapseDebris(Camera, collapse.Debris);
        renderManager.Objects.RenderGroundHeave(Camera, _world.Ambient.Heave);
        foreach (var tree in _toppling)
            renderManager.Objects.RenderTopplingTree(Camera, tree);
        var armorVersion = Inventory.Equipment.Version;
        if (armorVersion != _armorVersion || !_armorSeen)
        {
            renderManager.Players.LocalArmor = ArmorLook.From(Inventory.Equipment);
            _armorVersion = armorVersion;
            _armorSeen = true;
        }
        renderManager.Players.RenderPlayer(Camera, Player);
        renderManager.Players.RenderHeldItem(Camera, Player, LocalPlayer.HeldId, LocalPlayer.HasHeld,
            (float)Now, Compass.HeadingFromYaw(Camera.Yaw));
        renderManager.GpuTimer.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // The selection/breaking overlay is world geometry on the targeted block, so it draws with
        // the entities and BEFORE the water pass. Drawn after it, the water's written depth would
        // cull the overlay outright whenever the targeted block sits under the surface while the
        // eye is above it - the crack simply would not appear. Drawing it here lets the water blend
        // and refract over it exactly as it does over the block itself. Guarded rather than an early
        // return, which would skip the passes below.
        var placePopGrow = Placer.PlacePopGrowNow;
        if (LeftClick.HasSelectedBlock || placePopGrow > 0f)
        {
            FrameProfiler.Begin(ProfileSection.SelectPass);
            renderManager.GpuTimer.BeginGpuPass(ProfileSection.SelectPass);

            if (LeftClick.HasSelectedBlock)
            {
                // While mining, the outline and the break overlay ride along with the popping block
                var popGrow = LeftClick.IsBreaking ? LeftClick.BlockPopGrowNow : 0f;

                // The chunk mesh still holds the block at rest size, so the popped cube is only
                // needed (and only hides it) while it is grown; a trunk showing its eaten face keeps the
                // mesh on screen, since the popped whole cube would cover the bite
                if (popGrow > 0f && !LeftClick.ShowsEatenFace)
                    renderManager.Selection.RenderBlockPop(Camera, LeftClick.BreakingBlockPosition, LeftClick.BreakingBlockId, popGrow,
                        HitboxesAt(LeftClick.BreakingBlockPosition, LeftClick.BreakingBlockId));

                var selectedId = Edits.BlockIdAt(LeftClick.SelectedBlockPosition);
                renderManager.Selection.RenderBlockSelection(Camera, LeftClick.SelectedBlockPosition, LeftClick.SelectedFaceNormal, popGrow,
                    selectedId, LeftClick.SelectedBoxIndex, HitboxesAt(LeftClick.SelectedBlockPosition, selectedId), mining: LeftClick.IsBreaking);

                // A chopped natural trunk shows its progress as the eaten face instead of the crack overlay
                if (LeftClick.IsBreaking && !LeftClick.ShowsEatenFace)
                    renderManager.Selection.RenderBlockBreaking(Camera, LeftClick.BreakingBlockPosition, LeftClick.BreakProgress, popGrow, LeftClick.BreakingBlockId,
                        HitboxesAt(LeftClick.BreakingBlockPosition, LeftClick.BreakingBlockId));
            }

            // A just-placed block pops in; skipped while that same cell is the one being mined,
            // whose own out-and-back pop is already playing there
            if (placePopGrow > 0f && !(LeftClick.IsBreaking && LeftClick.BreakingBlockPosition == Placer.PlacePopPosition))
                renderManager.Selection.RenderBlockPop(Camera, Placer.PlacePopPosition, Placer.PlacePopId, placePopGrow,
                    HitboxesAt(Placer.PlacePopPosition, Placer.PlacePopId));

            renderManager.GpuTimer.EndGpuPass(ProfileSection.SelectPass);
            FrameProfiler.End(ProfileSection.SelectPass);
        }

        // Water draws after the world and entities: it blends, so everything it can cover has to be
        // in the depth buffer already
        FrameProfiler.Begin(ProfileSection.WaterPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.WaterPass);
        renderManager.Terrain.RenderWorldTranslucent(Camera);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.WaterPass);
        FrameProfiler.End(ProfileSection.WaterPass);

        // Clouds blend too, so they go after water has written its depth
        FrameProfiler.Begin(ProfileSection.CloudPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.CloudPass);
        renderManager.Sky.RenderClouds(Camera, Camera.IsEyeInWater);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.CloudPass);
        FrameProfiler.End(ProfileSection.CloudPass);

        // The depthless blended particles draw last, after the clouds, because none of them writes
        // depth: a cloud painted after them would cover every leaf and ember in front of it, since
        // the depth buffer still holds whatever is behind the particle rather than the particle.
        // Drawing them here lets the clouds' own depth pre-pass do the occluding - a particle in
        // front of a cloud passes the test and blends over it, one behind it is hidden - while the
        // terrain and water depth already in the buffer still hides particles behind them.
        // Bubbles ride along: they need the water pass' depth (they only exist submerged, where
        // the clouds are skipped anyway) and used to be the only pass drawn at this point.
        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.EntityPass);
        // Healthbars, damage numbers and notice pips are depthless blended billboards too: drawn
        // before the water and clouds, those would paint over them even when the bar is in front.
        renderManager.Billboards.RenderEntityHealthbars(Camera, Mobs);
        renderManager.Billboards.RenderDamageNumbers(Camera, DamageNumbers);
        if (_goblins != null)
            renderManager.Billboards.RenderNoticePips(Camera, _goblins.Goblins);
        renderManager.Billboards.RenderLeafParticles(Camera, _leaves.Particles);
        renderManager.Billboards.RenderFireParticles(Camera, _fire.Particles);
        // Lava embers are a second batch; the renderer re-uploads with BufferSubData, so two
        // calls a frame are safe. The batches are not depth-sorted against each other, which
        // only matters where campfire and lava smoke overlap (cosmetic).
        renderManager.Billboards.RenderFireParticles(Camera, _lavaFire.Particles);
        renderManager.Billboards.RenderFireParticles(Camera, _torchFire.Particles);
        renderManager.Billboards.RenderBubbleParticles(Camera, _bubbles.Particles);
        // Slice trails: additive and depthless like the embers, so they go here after the clouds;
        // never drawn in the mirror or the shadow pass
        renderManager.Billboards.RenderSliceTrails(Camera, _sliceTrails.Active, Now);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // Additive sun shafts on top of the finished underwater view; it re-reads the depth
        // buffer, so it has to follow everything that writes depth
        FrameProfiler.Begin(ProfileSection.RaysPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.RaysPass);
        renderManager.Screen.RenderUnderwaterRays(Camera);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.RaysPass);
        FrameProfiler.End(ProfileSection.RaysPass);

        // Godrays: the same march over dry air when the sun is in view; also after everything that
        // writes depth, since it reads the scene depth
        FrameProfiler.Begin(ProfileSection.GodraysPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.GodraysPass);
        renderManager.Screen.RenderGodrays(Camera);
        // The sun dazzle shares the godrays' row: one more full-screen draw toward the sun, over
        // the shafts and everything else
        renderManager.Screen.RenderSunDazzle(Camera);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.GodraysPass);
        FrameProfiler.End(ProfileSection.GodraysPass);

        // Last world pass: tonemap the linear HDR scene target into the default framebuffer.
        // The UI draws after this, straight onto that gamma framebuffer.
        FrameProfiler.Begin(ProfileSection.PostPass);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.PostPass);
        renderManager.Screen.RenderPostProcess(_health.DamageFlash);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.PostPass);
        FrameProfiler.End(ProfileSection.PostPass);
    }

    /// <summary>
    /// Draws the HUD. <paramref name="showProfiler"/> adds the frame-timing table under the FPS
    /// counter (the same flag that turns the debug console logging on). <paramref name="builder"/> is the
    /// spell builder panel, <paramref name="waystone"/> the waystone screen and <paramref name="forge"/> the anvil's recipe
    /// list, each drawn over the HUD while open.
    /// </summary>
    internal void RenderUI(RenderManager renderManager, InventoryView view, float time,
        int fps, bool showProfiler, bool showCoordinates = true, PauseMenuView? menu = null,
        SettingsMenuView? settings = null, SpellBuilderView? builder = null, WaystoneView? waystone = null,
        ForgeView? forge = null)
    {
        renderManager.UiPasses.RenderUI(_window!, Inventory, view, time, fps, showProfiler, Camera, showCoordinates, menu, settings,
            _health.Health, _health.Breath.Fraction, _health.Mana.Fraction, _health.DamageFlash, _hotbarName, _hotbarPops, LightingTime, Toast, LeftClick.Crosshair, LeftClick.LookAt,
            _staffSlot.Label(Inventory), _health.Effects.Mask, builder, _health.Max, _world.ChestPanel, waystone, BossSelection.Nearest(Mobs, Camera.Position), forge);
    }

    /// <summary>
    /// Hitboxes for the outline, break overlay and mining pop: the cell's drawn shape resolved
    /// from the live world exactly as the raycast aims at it (see <see cref="BlockShape"/>).
    /// </summary>
    private BlockHitbox[] HitboxesAt(Vector3 blockPos, ushort blockId) =>
        BlockShape.BoxesAt(blockId, _chunkManager, (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
}
