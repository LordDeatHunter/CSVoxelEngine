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
    private readonly Camera _camera;
    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly IWindow _window;
    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;

    public World(GL gl, IWindow window, IInputContext input)
    {
        _window = window;
        _camera = new Camera(window, input);
        _chunkManager = new ChunkManager(gl);
        _generationWorker = new ChunkGenerationWorker(_chunkManager, _camera);
        
        _generationWorker.Start();
    }

    public void Update(double deltaTime, IKeyboard? keyboard)
    {
        _camera.Update((float)deltaTime, keyboard, _chunkManager);
        _chunkManager.Update(_camera);

        _hasSelectedBlock = _camera.Raycast(10f, _chunkManager, out _selectedBlockPosition, out _selectedFaceNormal);
    }

    public Camera Camera => _camera;

    public void Render(RenderManager renderManager)
    {
        renderManager.RenderWorld(_camera, _chunkManager.Chunks.Values, _window.Time);

        if (_hasSelectedBlock)
        {
            renderManager.RenderBlockSelection(_camera, _selectedBlockPosition, _selectedFaceNormal);
        }
    }

    public void RenderUI(RenderManager renderManager)
    {
        renderManager.RenderUI(_window);
    }
    
    public void TryRemoveBlockFromRaycast(float maxDistance)
    {
        if (!_camera.Raycast(maxDistance, _chunkManager, out var hitPos, out _))
            return;

        var chunkPos = new Vector3(
            MathF.Floor(hitPos.X / Chunk.Size),
            MathF.Floor(hitPos.Y / Chunk.Size),
            MathF.Floor(hitPos.Z / Chunk.Size)
        );

        if (_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk.RemoveBlockAt(hitPos, _chunkManager);
        }
    }

    public void TryPlaceBlockFromRaycast(float maxDistance, byte blockId)
    {
        if (!_camera.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
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
