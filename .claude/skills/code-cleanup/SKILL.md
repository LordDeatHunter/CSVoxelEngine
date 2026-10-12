---
name: code-cleanup
description: Procedure for cleaning up CSVoxelEngine safely - splitting god files, dissolving god directories, renames and namespace moves, dead-code and duplication removal, early-return rewrites. Use for any refactoring, file move/split/rename, or structural cleanup in this repo, and any commit series that tidies code.
---

# Code cleanup in CSVoxelEngine

The standing rules live in `AGENTS.md` ("Code organization (standing rules)") - read them first.
This skill is the procedure for applying them without breaking the world.

## Non-negotiables

- **Release only.** `DOTNET_ROLL_FORWARD=LatestMajor dotnet build OpenGLTest.sln -c Release` and
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

Root `VoxelEngine`; one namespace per folder, mirroring the tree (`VoxelEngine.Systems.Chunks`).
Each project has a `GlobalUsings.cs` listing its own sub-namespaces; keep that list short and
only grow it when a namespace is genuinely used across most of its project. The test project's
namespace is `VoxelEngine.Tests` (assembly name follows the csproj).

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

## Reporting

When a cleanup run ends (or hands off), leave a note of what remains: the next agent needs the
list of un-split god files / folders and the namespace state. `AGENTS.md`'s Layout section is
the record of record - update the paths you moved.
