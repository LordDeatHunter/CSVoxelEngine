# Developer docs

CSVoxelEngine is a Minecraft-style voxel engine in C# on .NET 9, using Silk.NET (windowing, input, OpenGL bindings) and StbImageSharp. It renders an infinite procedural world with chunk streaming, binary greedy meshing, far-terrain LOD, a hell below and sky islands above.

Content (blocks, items, recipes, entities, trees, spells) is **data**: add JSON, not C#. See [Adding content](/dev/adding-content).

## Build and run

Always build and run **Release**; Debug is several times slower in the voxel loops.

```powershell
dotnet build VoxelEngine.sln -c Release
dotnet run --project OpenGLTest/VoxelEngine.csproj -c Release
```

The code uses C# 14 features, so you need the **.NET 10 SDK** (the projects still target net9.0). The renderer needs an **OpenGL 4.6 core** context (it uses `gl_DrawID`, persistent buffer mapping, multi-draw indirect and compute shaders), so macOS is not supported.

## Pages

- [Architecture](/dev/architecture): projects, folders and code-organization rules.
- [Threading model](/dev/threading): what runs where, and the rules that follow.
- [Content schemas](/dev/content-schemas): block, item, recipe, loot, tree and spell JSON.
- [Worldgen](/dev/worldgen): determinism rules, biomes, layers and caches.
- [Saves and multiplayer](/dev/saves-and-multiplayer)
- [Testing and visual checks](/dev/testing): xUnit, scenarios and the item debugger.
- [Adding content](/dev/adding-content)
- [This wiki](/dev/wiki): how the site is generated.
