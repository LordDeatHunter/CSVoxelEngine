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

    // Seconds of holding left-click to break a block (noclip breaks instantly)
    public const float BlockBreakSeconds = 1f;

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly IWindow _window;
    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;
    private bool _isBreaking;
    private Vector3 _breakingBlockPosition;
    private float _breakProgress;
    public Camera Camera { get; }

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
            renderManager.RenderBlockBreaking(Camera, _breakingBlockPosition, _breakProgress);
    }

    public void RenderUI(RenderManager renderManager)
    {
        renderManager.RenderUI(_window);
    }
    
    /// <summary>
    /// Drives hold-to-break: progress accumulates while left-click is held on the same
    /// block, resets when the target changes or the button is released, and completes
    /// after BlockBreakSeconds. Noclip instamines (one block per frame, as before).
    /// Call after Update so the block selection is current.
    /// </summary>
    public void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        if (!isHolding || !_hasSelectedBlock)
        {
            _isBreaking = false;
            _breakProgress = 0f;
            return;
        }

        if (Camera.IsNoclip)
        {
            _isBreaking = false;
            _breakProgress = 0f;
            RemoveBlock(_selectedBlockPosition);
            return;
        }

        if (!_isBreaking || _selectedBlockPosition != _breakingBlockPosition)
        {
            _isBreaking = true;
            _breakingBlockPosition = _selectedBlockPosition;
            _breakProgress = 0f;
        }

        _breakProgress += (float)deltaTime / BlockBreakSeconds;
        if (_breakProgress < 1f) return;

        RemoveBlock(_breakingBlockPosition);
        _isBreaking = false;
        _breakProgress = 0f;
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
