using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

/// <summary>
/// Main world class that manages the camera, chunks, and world generation.
/// </summary>
public class World
{
    // How far the player can target blocks (10 blocks)
    public const float InteractionReach = 10f;

    // Seconds of holding left-click per mining hit. Each hit removes 1 HP from the
    // targeted block; time short of a full interval deals nothing (noclip breaks instantly).
    public const float BlockHitSeconds = 0.2f;

    // A hit makes the block jump out and settle back. Kept below BlockHitSeconds so the
    // pop always finishes before the next hit restarts it.
    public const float BlockPopSeconds = 0.15f;

    // Uniform scale the block reaches at the peak of a hit
    public const float BlockPopScale = 1.3f;

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly DropManager _dropManager = new();
    private readonly TntManager _tntManager = new();
    // Trees currently toppling over after their base log was mined
    private readonly List<TopplingTree> _toppling = new();
    private readonly IWindow _window;
    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;
    private bool _isBreaking;
    private Vector3 _breakingBlockPosition;
    private byte _breakingBlockId;
    private int _breakDamage;
    private int _breakMaxHealth;
    private float _hitTimer;
    private float _popElapsed = BlockPopSeconds;
    public Camera Camera { get; }

    /// <summary>The player's hotbar; its selected slot decides what block placing puts down.</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>Live primed TNT, feeding the renderer's dynamic point lights.</summary>
    public IReadOnlyList<PrimedTnt> PrimedTnt => _tntManager.Primed;

    // Break overlay strength. Quantized to whole hits, so it steps with the damage dealt
    // instead of sweeping continuously.
    private float BreakProgress => _breakMaxHealth > 0 ? _breakDamage / (float)_breakMaxHealth : 0f;

    // Scale of the block being mined: 1 at rest, eased up to BlockPopScale and back down
    // over BlockPopSeconds after each hit lands
    private float BlockPopScaleNow =>
        MathUtils.Lerp(1f, BlockPopScale, MathUtils.Pulse(_popElapsed / BlockPopSeconds));

    public World(GL gl, IWindow window, IInputContext input)
    {
        _window = window;
        Camera = new Camera(window, input);
        _chunkManager = new ChunkManager(gl);
        _generationWorker = new ChunkGenerationWorker(_chunkManager, Camera);
        _lodManager = new LodManager(gl);

        _generationWorker.Start();
        _lodManager.Start(Camera);
    }

    public void Update(double deltaTime, IKeyboard? keyboard)
    {
        Camera.Update((float)deltaTime, keyboard, _chunkManager);
        _chunkManager.Update(Camera);
        _lodManager.Update(_chunkManager);

        // Drops collected this tick land in the hotbar, which pops the matching icon. A block
        // with no slot is simply discarded.
        _dropManager.Update((float)deltaTime, Camera.Position, _chunkManager);
        foreach (var blockId in _dropManager.Collected)
            Inventory.Add(blockId);

        _tntManager.Update((float)deltaTime, _chunkManager, _lodManager, _dropManager);

        UpdateToppling((float)deltaTime);

        Inventory.Update((float)deltaTime);

        _hasSelectedBlock = Camera.Raycast(InteractionReach, _chunkManager, out _selectedBlockPosition, out _selectedFaceNormal);
    }

    public void Render(RenderManager renderManager)
    {
        renderManager.RenderLod(Camera, _lodManager.Regions.Values);
        renderManager.RenderWorld(Camera, _chunkManager.Chunks.Values, _window.Time);
        renderManager.RenderItemDrops(Camera, _dropManager.Drops);
        renderManager.RenderPrimedTnt(Camera, _tntManager.Primed);
        foreach (var tree in _toppling)
            renderManager.RenderTopplingTree(Camera, tree);

        if (!_hasSelectedBlock) return;

        // While mining, the outline and the break overlay ride along with the popping block
        var popScale = _isBreaking ? BlockPopScaleNow : 1f;

        // The chunk mesh still holds the block at rest size, so the popped cube is only
        // needed (and only hides it) while it is scaled up
        if (popScale > 1f)
            renderManager.RenderBlockPop(Camera, _breakingBlockPosition, _breakingBlockId, popScale);

        renderManager.RenderBlockSelection(Camera, _selectedBlockPosition, _selectedFaceNormal, popScale);

        if (_isBreaking)
            renderManager.RenderBlockBreaking(Camera, _breakingBlockPosition, BreakProgress, popScale);
    }

    public void RenderUI(RenderManager renderManager)
    {
        renderManager.RenderUI(_window, Inventory);
    }
    
    /// <summary>
    /// Drives hit-based mining: holding left-click on a block lands one hit per
    /// BlockHitSeconds, each removing 1 HP, and the block breaks when its HP runs out
    /// (see BlockType.MaxHealth). A partial interval deals no damage, and damage is
    /// discarded when the target changes or the button is released. Noclip instamines
    /// (one block per frame, as before). Call after Update so the selection is current.
    /// </summary>
    public void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        if (!isHolding || !_hasSelectedBlock)
        {
            StopBreaking();
            return;
        }

        if (Camera.IsNoclip)
        {
            StopBreaking();
            RemoveBlock(_selectedBlockPosition);
            return;
        }

        if (!_isBreaking || _selectedBlockPosition != _breakingBlockPosition)
        {
            _isBreaking = true;
            _breakingBlockPosition = _selectedBlockPosition;
            _breakingBlockId = BlockIdAt(_selectedBlockPosition);
            _breakMaxHealth = BlockType.ById[_breakingBlockId]?.MaxHealth ?? 0;
            _breakDamage = 0;
            _hitTimer = 0f;
            _popElapsed = BlockPopSeconds;
        }

        // Pebbles pop on first contact: one punch breaks them, no hold-to-mine
        if (_breakingBlockId == BlockType.Pebble)
        {
            RemoveBlock(_selectedBlockPosition);
            StopBreaking();
            return;
        }

        // Air or an unloaded chunk: nothing to mine
        if (_breakMaxHealth <= 0)
        {
            StopBreaking();
            return;
        }

        _hitTimer += (float)deltaTime;
        _popElapsed += (float)deltaTime;

        // A long frame can carry more than one hit; the leftover time rolls into the next
        while (_hitTimer >= BlockHitSeconds)
        {
            _hitTimer -= BlockHitSeconds;
            _breakDamage++;

            // Start the pop from where the hit actually landed inside this frame, not from
            // the frame boundary, so the animation stays in step with the hit rhythm
            _popElapsed = _hitTimer;

            if (_breakDamage < _breakMaxHealth) continue;

            RemoveBlock(_breakingBlockPosition);
            StopBreaking();
            return;
        }
    }

    private void StopBreaking()
    {
        _isBreaking = false;
        _breakDamage = 0;
        _breakMaxHealth = 0;
        _hitTimer = 0f;
        _popElapsed = BlockPopSeconds;
    }

    /// <summary>
    /// Block id at a world position; 0 (air) for unloaded chunks.
    /// </summary>
    private byte BlockIdAt(Vector3 blockPos)
    {
        // blockPos components are floored floats, so the int casts are exact
        return _chunkManager.GetBlockGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
    }

    private void RemoveBlock(Vector3 blockPos)
    {
        var chunkPos = new Vector3(
            MathF.Floor(blockPos.X / Chunk.Size),
            MathF.Floor(blockPos.Y / Chunk.Size),
            MathF.Floor(blockPos.Z / Chunk.Size)
        );

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return;

        // Read the id before the block goes away, so the drop knows what it is
        var brokenId = BlockIdAt(blockPos);

        // Mined TNT never drops — once its HP runs out it primes on the spot instead
        if (brokenId == BlockType.Tnt)
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager))
                _tntManager.Spawn(blockPos);
        }
        // A mined wood block fells whatever it was holding up instead of dropping one
        // block (see FellWood): a trunk cut drops the crown, a branch cut only its children
        else if (TreeGenerator.WoodLevel(brokenId) >= 0)
            FellWood(blockPos, brokenId, chunk);
        // Mined leaves roll item drops instead of a block drop (same odds as every other
        // destruction path; see DropManager.SpawnLeafDrops)
        else if (brokenId == BlockType.Leaves && chunk.RemoveBlockAt(blockPos, _chunkManager))
            _dropManager.SpawnLeafDrops(blockPos);
        // Pebbles drop one flint per visible stone (see PebbleGenerator.CountAt)
        else if (brokenId == BlockType.Pebble && chunk.RemoveBlockAt(blockPos, _chunkManager))
        {
            var flints = PebbleGenerator.CountAt((int)blockPos.X, (int)blockPos.Z);
            for (var i = 0; i < flints; i++)
                _dropManager.Spawn(ItemType.Flint, blockPos);
        }
        // Only drop something if a block was actually cleared
        else if (chunk.RemoveBlockAt(blockPos, _chunkManager))
            _dropManager.Spawn(BlockType.DropFor(brokenId), blockPos);

        // Keep the buried LOD mesh below the newly exposed hole
        // (blockPos components are floored floats, so the int casts are exact)
        if (LodRegion.LowerColumnBelow((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z))
            _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);

        // Wood and leaves live above the heightmap caps, so the gate above never fires for
        // them — yet the far LOD draws their canopy. Disturb it and rebuild regardless.
        if (TreeGenerator.WoodLevel(brokenId) >= 0 || brokenId == BlockType.Leaves)
        {
            TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z);
            _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);
        }

        // Pebbles need the block under them: pop whatever sits right above the break. This
        // routes through the normal pebble path, so it drops its flint; its own check above
        // then no-ops on air, so the recursion always terminates.
        var above = blockPos + Vector3.UnitY;
        if (BlockIdAt(above) == BlockType.Pebble)
            RemoveBlock(above);
    }

    /// <summary>
    /// Fells the wood a mined wood block was holding up: collects the severed part (the break
    /// plus its children — a branch cut leaves the trunk standing), clears it in one batch
    /// (same pattern as a TNT blast), and launches a toppling entity that drops its blocks
    /// once it has lain still. Player-placed logs are not trees: they pop out as a plain
    /// drop with no entity and no animation. A lone block with no children just drops as-is.
    /// </summary>
    private void FellWood(Vector3 blockPos, byte brokenId, Chunk chunk)
    {
        var x = (int)blockPos.X;
        var y = (int)blockPos.Y;
        var z = (int)blockPos.Z;

        // Player-built logs never topple: no entity, no animation, just a drop.
        // RemoveBlockAt remeshes, so unlike the batch paths below nothing lingers.
        if (_chunkManager.IsPlayerPlaced(x, y, z))
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager))
                _dropManager.Spawn(BlockType.DropFor(brokenId), blockPos);
            return;
        }

        var collected = TopplingTree.Collect(_chunkManager, blockPos, brokenId);
        if (collected.Count < 2)
        {
            // Nothing was held up by this block: drop it as-is. ClearBlockAt skips the
            // remesh, so remesh the batch of one — otherwise the mined log visibly lingers.
            if (chunk.ClearBlockAt(blockPos) != 0)
            {
                chunk.RemeshAfterBatchEdit(_chunkManager);
                _dropManager.Spawn(BlockType.DropFor(brokenId), blockPos);
            }
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        var lowestY = new Dictionary<(int X, int Z), int>();
        foreach (var block in collected)
        {
            var chunkPos = new Vector3(
                MathF.Floor(block.Cell.X / Chunk.Size),
                MathF.Floor(block.Cell.Y / Chunk.Size),
                MathF.Floor(block.Cell.Z / Chunk.Size)
            );
            if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var target)) continue;
            if (target.ClearBlockAt(block.Cell) == 0) continue;

            // A player-built extension felled with the tree stops counting as placed
            _chunkManager.UnmarkPlayerPlaced((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z);
            touchedChunks.Add(chunkPos);

            var key = ((int)block.Cell.X, (int)block.Cell.Z);
            if (!lowestY.TryGetValue(key, out var lowest) || (int)block.Cell.Y < lowest)
                lowestY[key] = (int)block.Cell.Y;
        }

        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        foreach (var ((lx, lz), ly) in lowestY)
        {
            if (LodRegion.LowerColumnBelow(lx, ly, lz))
                _lodManager.MarkEditedColumn(lx, lz);
        }

        // The whole canopy came down above the caps: disturb it once at the break (its reach
        // covers the crown) and rebuild every touched column so no ghost lingers at range
        TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z);
        foreach (var ((lx, lz), _) in lowestY)
            _lodManager.MarkEditedColumn(lx, lz);

        // The tree tips away from whoever felled it, pivoting on the mined cell's base
        var away = new Vector3(
            blockPos.X + 0.5f - Camera.Position.X, 0f,
            blockPos.Z + 0.5f - Camera.Position.Z);
        _toppling.Add(new TopplingTree(collected,
            new Vector3(blockPos.X + 0.5f, blockPos.Y, blockPos.Z + 0.5f), away));
    }

    /// <summary>Swings falling trees, then pops their wood out as item drops once rested.</summary>
    private void UpdateToppling(float deltaTime)
    {
        for (var i = _toppling.Count - 1; i >= 0; i--)
        {
            var tree = _toppling[i];
            tree.Update(deltaTime);
            if (!tree.IsDone) continue;

            foreach (var (id, cell) in tree.TakeDrops())
                _dropManager.Spawn(id, cell);

            tree.Dispose();
            _toppling.RemoveAt(i);
        }
    }

    /// <summary>
    /// Throws the selected slot onto the ground in front of the player: one unit, or the whole
    /// stack when <paramref name="wholeStack"/> is set. Works for blocks and pure items alike;
    /// does nothing when the slot is empty. Each unit spawns its own drop, so a thrown stack
    /// scatters (every drop rolls its own random pop) and picks back up one unit at a time.
    /// </summary>
    public void ThrowSelected(bool wholeStack = false)
    {
        var id = Inventory.SelectedBlockId;
        if (id == 0 || Inventory.SelectedCount <= 0) return;

        var units = wholeStack ? Inventory.SelectedCount : 1;
        var forward = Camera.Forward;
        for (var i = 0; i < units; i++)
        {
            // Spend first so a failed spend never duplicates the drop
            if (!Inventory.ConsumeSelected()) break;

            _dropManager.SpawnAt(id, Camera.Position + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
        }
    }

    /// <summary>
    /// Places the hotbar's selected block against the face the player is looking at and spends
    /// one from that slot. Does nothing when the slot is empty or holds no blocks.
    /// </summary>
    public void TryPlaceSelectedBlock(float maxDistance)
    {
        if (!Inventory.CanPlaceSelected) return;

        // Only spend a block if one actually went down
        if (TryPlaceBlockFromRaycast(maxDistance, Inventory.SelectedBlockId))
            Inventory.ConsumeSelected();
    }

    /// <summary>Places a block against the targeted face. Returns whether one was placed.</summary>
    public bool TryPlaceBlockFromRaycast(float maxDistance, byte blockId)
    {
        if (blockId == 0) return false;

        if (!Camera.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
            return false;

        var placePos = hitPos + hitNormal;
        var chunkPos = new Vector3(
            MathF.Floor(placePos.X / Chunk.Size),
            MathF.Floor(placePos.Y / Chunk.Size),
            MathF.Floor(placePos.Z / Chunk.Size)
        );

        return _chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)
               && chunk.PlaceBlockAt(placePos, blockId, _chunkManager);
    }
    
    public ChunkManager GetChunkManager() => _chunkManager;
}
