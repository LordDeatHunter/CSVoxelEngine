# Developer docs

CSVoxelEngine is a C# / .NET voxel engine on Silk.NET (OpenGL 4.6). Content (blocks, items, recipes, entities, trees, spells) is **data**: add JSON, not C#. See [Adding content](/dev/adding-content).

Build and run (always Release):

```powershell
dotnet build VoxelEngine.sln -c Release
dotnet run --project OpenGLTest/VoxelEngine.csproj -c Release
```
