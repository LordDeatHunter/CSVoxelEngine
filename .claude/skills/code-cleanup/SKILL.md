---
name: code-cleanup
description: Procedure for cleaning up CSVoxelEngine safely - splitting god files, dissolving god directories, renames and namespace moves, dead-code and duplication removal, early-return rewrites. Use for any refactoring, file move/split/rename, or structural cleanup in this repo, and any commit series that tidies code.
---

# Code cleanup in CSVoxelEngine

The standing rules live in `AGENTS.md` ("Code organization (standing rules)") - read them first.
This skill is the procedure for applying them without breaking the world.

## Non-negotiables

- **Release only.** `DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release` and
  `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (~50 s, 2400+ tests).
  Never verify in Debug; the game's behavior claims are Release measurements.
- **The build and tests stay green between commits.** A cleanup commit that leaves the tree
  red is worse than no cleanup. Run the full test suite before every commit that touches code.
- **Commits**: short imperative messages, no body ("Split World into focused classes").
  One concern per commit. Never change git config, never rewrite history.
- **Never commit** the working-tree junk: `.claude/worktrees/`, `.sisyphus/`, `.run/`,
  `visual/`, build outputs. Stage explicitly by path; never `git add -A`.
- **Mechanical sweeps are verified by the compiler + tests, not by eyeballing** - but renames of
  *content* (a block id, a JSON file name, a shader uniform) are API changes and need a game
  scenario run, not just unit tests.

## Where the bodies are buried

Prefer `rg`/Grep over guesswork. Useful recon commands (PowerShell):

- Largest files: `Get-ChildItem OpenGLTest/src -Recurse -Filter *.cs | Sort-Object Length -Descending`
- Files per folder: group the .cs files by `DirectoryName`, flag anything past ~30.
- British spellings: grep `colour|behaviour|initialis|centre|fibre|grey|normalis|serialis|
  catalogu|analys|defence|licence|travell|cancell|favour|honour|neighbour` (word-ish, case
  insensitive, minus `Color`-legit hits like `oncologist`; check each).
- Dead code: the compiler only flags unused usings via IDE; grep for private members and
  confirm zero references, and treat `private` fields with no reads as deletions.
- Duplicates: two near-identical methods are easiest found while editing a file; search the
  extracted helper name first before writing a new one.

## Order of operations for a structural pass

1. **Folder moves before namespace edits** - a file's namespace must match where it finally
   lives. Move with `git mv`, then fix the `namespace` line in the same commit.
2. **Folder moves before class splits** when both touch the same area, so the split lands in
   its final home.
3. **Class splits last** - they are behavior-adjacent and deserve the test suite's full
   attention. Extract one coherent responsibility at a time; never rewrite logic and move it in
   the same step if you can move it verbatim first.
4. Structural renames that cross the assembly boundary (csproj, `InternalsVisibleTo`, assembly
   names) are one commit each with a build in between.

## Namespace scheme

Root `VoxelEngine`; one namespace per folder below `OpenGLTest/src`, mirroring the tree
(`src/Chunks/` -> `VoxelEngine.Chunks`, `src/WorldGen/Trees/` -> `VoxelEngine.WorldGen.Trees`);
only the six files directly in `src/` are in `VoxelEngine` itself. The tests mirror their own
tree: `OpenGLTest.Tests/Chunks/` -> `VoxelEngine.Tests.Chunks`, the shared fixtures in
`Support/` -> `VoxelEngine.Tests.Support` (nothing sits at the test project root). The server
and the item debugger are flat (`VoxelEngine.Server`, `VoxelEngine.ItemDebugger`; assembly names
follow the csproj). Moving a file means changing its namespace line and letting the compiler
name the usings to add; a using that only a `cref` needs stays (a doc build counts it as used).
Each project keeps a `GlobalUsings.cs` at its root beside the csproj listing only the namespaces
most of its files need (`VoxelEngine.Core` everywhere; the tests add `VoxelEngine.Chunks`,
`VoxelEngine.Content` and `VoxelEngine.Tests.Support`; the game and the item debugger add
`global using Shader = VoxelEngine.Graphics.Shader;` because Silk.NET.OpenGL has a `Shader`
struct); keep that list short and only grow it when a namespace is genuinely used across most
of its project. A new folder is a new namespace; never add a type to a namespace its folder
does not map to.

## Splitting a god class - recipe

1. Write down the class's jobs from its public surface; pick the one that is least entangled.
2. Move it **verbatim** to a new file (same code, new class name) behind the same call sites -
   don't rename members while moving.
3. Compile; fix visibility (`internal` instead of `public` where the new class need not be
   public - most extracted helpers can be).
4. Run the full suite; commit.
5. Only then tighten naming/structure inside the extracted class.

State-bearing sims (`FluidManager`, `LeafDecayManager`, `TntManager`, stations) share invariants
documented in `AGENTS.md` (edit-path hooks, threading); read that section before touching them
and keep every hook call on the same path.

## Early returns / nesting rewrites

Do these **per file while already editing it**, not as a blind repo-wide sweep - a controller
loop refactored without understanding its invariants is how frame-order bugs ship. Convert one
method at a time, keep the suite green, prefer:
- guard clauses for the failure/empty cases,
- `continue` for a wrapped loop body,
- an extracted method when the body still nests past ~3 levels,
- `MathUtils` helpers for any curve you are tempted to inline.

## Running a cleanup task through the workflow

One cleanup task is one `plan-and-implement` run (Workflow tool, `name: 'plan-and-implement'`),
never several in parallel: they share one working tree. Pass the task plus this `extraContext`,
and `maxFixRounds: 1`:

> Cleanup run; read AGENTS.md "Code organization (standing rules)" and the code-cleanup skill.
> Do NOT add or edit tests except what a move/rename/namespace change forces; no visual scenarios,
> no in-game verification. The only gate is a zero-warning Release build plus the existing xUnit
> suite, all passing. Toolchain: the code needs the .NET 10 SDK. Ignore the `~/.dotnet/dotnet.exe`
> and `OpenGLTest.sln` in the preamble; use `DOTNET_ROLL_FORWARD=LatestMajor dotnet build
> VoxelEngine.sln -c Release` and `DOTNET_ROLL_FORWARD=LatestMajor dotnet test
> OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (system dotnet; set
> `$env:DOTNET_ROLL_FORWARD` first in PowerShell). Do not commit, stash or branch. The tree is
> CRLF: follow the crlf-safe-edit skill. Behavior-preserving only: move code verbatim before
> touching it. Update AGENTS.md Layout paths for anything moved. Leave `.claude/worktrees`,
> `.sisyphus`, `.run` alone. The reviewer checks the diff against the rules and the plan, and does
> not ask for new tests or scenario runs.

After a green run, stage the touched paths explicitly and commit with a one-line subject (no body
beyond the trailer). Task order: folder moves, namespaces, American English, one god class per
run, then dead code / duplication / early returns slice by slice.

## Reporting

When a cleanup run ends (or hands off), leave a note of what remains: the next agent needs the
list of un-split god files / folders and the namespace state. `AGENTS.md`'s Layout section is
the record of record - update the paths you moved.
