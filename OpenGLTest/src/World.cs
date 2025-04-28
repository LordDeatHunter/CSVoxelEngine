using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

public class World
{
    private readonly Camera camera;
    private readonly ChunkManager chunkManager;
    private readonly IWindow window;

    public World(GL gl, IWindow window, IInputContext input)
    {
        this.window = window;
        camera = new Camera(window, input);
        chunkManager = new ChunkManager(gl);

        StartChunkGeneration();
    }

    public void Update(double deltaTime, IKeyboard? keyboard)
    {
        camera.Update((float)deltaTime, keyboard);
        chunkManager.Update(camera);
    }

    public void Render(RenderManager renderManager)
    {
        renderManager.RenderWorld(camera, chunkManager.Chunks.Values, window.Time);
    }

    public void RenderUI(RenderManager renderManager)
    {
        renderManager.RenderUI(window);
    }

    private async void StartChunkGeneration()
    {
        await Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var camChunk = (camera.Position / Chunk.Size).Floor();
                    var candidates = new List<Vector3>();
                    const int radius = 12;
                    const int radiusSq = radius * radius;

                    for (int dx = -radius; dx <= radius; dx++)
                    for (int dy = -radius; dy <= radius; dy++)
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        var offset = new Vector3(dx, dy, dz);
                        var distSq = dx * dx + dy * dy + dz * dz;

                        if (distSq > radiusSq)
                            continue;

                        var pos = camChunk + offset;

                        candidates.Add(pos);
                    }

                    candidates.Sort((a, b) =>
                    {
                        var da = Vector3.DistanceSquared(a, camChunk);
                        var db = Vector3.DistanceSquared(b, camChunk);
                        return da.CompareTo(db);
                    });

                    foreach (var pos in candidates)
                    {
                        chunkManager.QueueChunk(pos);
                    }

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error generating chunks: {ex}");
                }
            }
        });
    }
    
    public void TryRemoveBlockFromRaycast(float maxDistance)
    {
        if (!camera.Raycast(maxDistance, chunkManager, out var hitPos, out var hitNormal))
            return;

        var chunkPos = new Vector3(
            MathF.Floor(hitPos.X / Chunk.Size),
            MathF.Floor(hitPos.Y / Chunk.Size),
            MathF.Floor(hitPos.Z / Chunk.Size)
        );

        if (chunkManager.Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk.RemoveBlockAt(hitPos, chunkManager);
        }
    }
    
    public ChunkManager GetChunkManager() => chunkManager;
}
