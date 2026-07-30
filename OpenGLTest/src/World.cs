using System;
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
    // How far the player can target blocks (10 m)
    public const float InteractionReach = 10f * WorldScale.BlocksPerMeter;

    // Seconds of holding left-click per mining hit. Each hit removes 1 HP from the
    // targeted block; time short of a full interval deals nothing (noclip breaks instantly).
    public const float BlockHitSeconds = 0.2f;

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly IWindow _window;
    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;
    private bool _isBreaking;
    private Vector3 _breakingBlockPosition;
    private int _breakDamage;
    private int _breakMaxHealth;
    private float _hitTimer;
    public Camera Camera { get; }

    // Break overlay strength. Quantized to whole hits, so it steps with the damage dealt
    // instead of sweeping continuously.
    private float BreakProgress => _breakMaxHealth > 0 ? _breakDamage / (float)_breakMaxHealth : 0f;

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
        _lodManager.Update();

        _hasSelectedBlock = Camera.Raycast(InteractionReach, _chunkManager, out _selectedBlockPosition, out _selectedFaceNormal);
    }

    public void Render(RenderManager renderManager)
    {
        renderManager.RenderLod(Camera, _lodManager.Regions.Values);
        renderManager.RenderWorld(Camera, _chunkManager.Chunks.Values, _window.Time);

        if (!_hasSelectedBlock) return;
        renderManager.RenderBlockSelection(Camera, _selectedBlockPosition, _selectedFaceNormal);

        if (_isBreaking)
            renderManager.RenderBlockBreaking(Camera, _breakingBlockPosition, BreakProgress);
    }

    public void RenderUI(RenderManager renderManager)
    {
        renderManager.RenderUI(_window);
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
            _breakMaxHealth = BlockHealthAt(_selectedBlockPosition);
            _breakDamage = 0;
            _hitTimer = 0f;
        }

        // Air or an unloaded chunk: nothing to mine
        if (_breakMaxHealth <= 0)
        {
            StopBreaking();
            return;
        }

        _hitTimer += (float)deltaTime;

        // A long frame can carry more than one hit; the leftover time rolls into the next
        while (_hitTimer >= BlockHitSeconds)
        {
            _hitTimer -= BlockHitSeconds;
            _breakDamage++;

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
    }

    /// <summary>
    /// HP of the block at a world position, or 0 for air and unloaded chunks.
    /// </summary>
    private int BlockHealthAt(Vector3 blockPos)
    {
        // blockPos components are floored floats, so the int casts are exact
        var id = _chunkManager.GetBlockGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
        return BlockType.ById[id]?.MaxHealth ?? 0;
    }

    private void RemoveBlock(Vector3 blockPos)
    {
        var chunkPos = new Vector3(
            MathF.Floor(blockPos.X / Chunk.Size),
            MathF.Floor(blockPos.Y / Chunk.Size),
            MathF.Floor(blockPos.Z / Chunk.Size)
        );

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return;

        chunk.RemoveBlockAt(blockPos, _chunkManager);

        // Keep the buried LOD mesh below the newly exposed hole
        // (blockPos components are floored floats, so the int casts are exact)
        if (LodRegion.LowerColumnBelow((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z))
            _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);
    }

    public void TryPlaceBlockFromRaycast(float maxDistance, byte blockId)
    {
        if (!Camera.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
            return;

        var placePos = hitPos + hitNormal;
        var chunkPos = new Vector3(
            MathF.Floor(placePos.X / Chunk.Size),
            MathF.Floor(placePos.Y / Chunk.Size),
            MathF.Floor(placePos.Z / Chunk.Size)
        );

        if (_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk.PlaceBlockAt(placePos, blockId, _chunkManager);
        }
    }
    
    public ChunkManager GetChunkManager() => _chunkManager;
}
