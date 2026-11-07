# Testing and visual checks

## xUnit tests

```powershell
dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release
```

The tests are headless (no window). Add a test with every feature, but only when there is a **rule** to lock down: a gate, a formula, a state transition, an invariant that survives content tuning. A test must never assert a constant, a layout number, a content literal or a starting-stock count.

If only an older SDK is installed or only the .NET 10 runtime is present, prefix `dotnet` commands with `DOTNET_ROLL_FORWARD=LatestMajor`.

## Scripted visual scenarios

Set `VOXEL_SCRIPT` to a scenario JSON (see `OpenGLTest/visual-examples/`) to run a scripted visual test. Steps (`teleport`, `look`, `wait`, `walk`, `mine`, `place`, `fly`, `give`, `spawn`, `grow`, `cast`, `screenshot` and more) drive the real update functions and save numbered PNGs to `visual/<name>/`. Many scenarios want `VOXEL_TIME=147` (noon) for stable lighting.

Other developer variables: `VOXEL_SPAWN` (`x,y,z`), `VOXEL_LOOK` (`yaw,pitch`), `VOXEL_TIME` (seconds added to the lighting clock) and `VOXEL_DEBUG=1`. The spawn, look and script overrides skip the menu and run a storeless world.

## Debug overlay

`X` toggles the debug line and the frame profiler overlay, which shows each section's average and worst cost over the last second and the CPU against GPU time. Measure only in Release.

## Item debugger

```powershell
dotnet run --project OpenGLTest.ItemDebugger -c Release
```

Its ITEMS tab previews held items and every player animation on a timeline with scrubbing, its ENTITIES tab previews each mob's clips, and its TREES tab grows any species step by step. Use it first for any animation or model change. Scripted captures use `ITEM_DEBUGGER_SELECT`, `ITEM_DEBUGGER_ANIM`, `ITEM_DEBUGGER_SHOT` and related variables.
