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
  catalogu|analys|defence|licence|travell|cancell|favour|honour|neighbour|bevell|spirall|
  funnell|rasteris|memois|quantis|artefact|millilitre` (word-ish, case insensitive, minus
  `Color`-legit hits like `oncologist`; check each). The stems are British on purpose: never
  convert this list. Substring false positives to skip: `TranslucentRenderable`,
  `ModelLoader`/`BbmodelLoader`, `FuelLocal`, `Silk.NET.Maths`, `optimistic`.
- Dead code: the plain build reports none of it. Measure with `DOTNET_ROLL_FORWARD=LatestMajor
  dotnet build VoxelEngine.sln -c Release -p:GenerateDocumentationFile=true
  -p:EnforceCodeStyleInBuild=true -p:AnalysisModeStyle=All -p:NoWarn=CS1591 --no-incremental
  -clp:NoSummary > <scratchpad>/stylebuild.log 2>&1`, on the command line only (never in a
  csproj, Directory.Build.props, .editorconfig or .globalconfig). Grep only IDE0005 (unused
  using), IDE0051 (unused private member), IDE0052 (written, never read), IDE0059 (dead store),
  IDE0060 (unused parameter) and the doc codes CS1574/CS0419/CS1570/CS1734 (CS1573 is a
  separate doc chore) and ignore every other IDE code. Count unique sites by stripping the
  trailing ` [project]` and running `sort -u`, since each project prints its own copy. IDE0005
  reports once per file, so read the whole using block and re-run until 0 (a using that only a
  cref needs counts as used under GenerateDocumentationFile). Removing a parameter can
  cascade into its callee's parameters, so re-measure. Never drop or reorder an expression with
  side effects (one that advances Random/SplitMix64/a noise or hash stream, or calls
  Interlocked/Volatile, a GL call or a registry call): keep it as a statement or skip. A store
  kept alive by a const config flag (ChunkTerrainFill `hasWater`) stays. Still grep for internal
  members with zero references in all four projects, tests counting as uses.
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

Cleanup tasks run through the **`cleanup-campaign`** workflow
(`.claude/workflows/cleanup-campaign.js`), not `plan-and-implement`. Launch it with the Workflow
tool and `scriptPath` set to that file (a workflow launched by `name` is the copy cached when the
session started, so an edit to the script would be ignored). One run at a time: every task shares
the one working tree.

- **No args**: the next task in `CLEANUP_PLAN.md` (the in-progress one first, then "Still to do"
  in order). **A string** (or `{task}`): that task. Other options: `tasks: n` runs n plan tasks
  back to back, stopping at the first one not accepted; `dryRun: true` stops after the first plan;
  `commit: false` leaves the accepted work uncommitted; `maxFixRounds` (default 1);
  `implementer: 'sonnet' | 'opus'` forces one implementer for every step; `extraContext`.
- **Roles.** Opus calls the shots: it picks the task and briefs the scouts, writes the plan and
  assigns each step to Sonnet or to itself, reviews the result and decides. Sonnet does the
  reading and most of the writing: it surveys the plan, scouts the code, answers Opus' questions,
  fact-checks the plan, implements the steps, runs the build and tests, audits the diff and
  commits. Opus takes the wheel for the steps it reserved (judgment it cannot write down in
  advance) and for any step Sonnet leaves blocked, partial or red.
- **The gate** is baked into the script: the cleanup rules above, the SDK 10 toolchain, no new
  tests or scenario runs, a `--no-incremental` zero-warning Release build and the existing suite
  (the baseline and the flaky list are read from `CLEANUP_PLAN.md`, so keep them current there).
- **The move-diff residue check** is `move_residue.py` beside this file:
  `python -I .claude/skills/code-cleanup/move_residue.py` (working tree vs HEAD, untracked files
  included) or `--range A..B`. It pairs every removed line with an added one after normalizing
  whitespace and visibility modifiers, and prints only the residue: changed lines, owner prefixes
  swapped, new and deleted lines, verbatim lines out of source order, plus the line-ending, BOM
  and final-newline state of every changed file. A split that moved verbatim leaves a residue of
  wiring and doc rewording; the auditor lists it and the reviewer reads only that.
- **Afterwards** the workflow stages the task's paths explicitly (never the junk folders), commits
  with a one-line subject plus the trailer, records the commit in `CLEANUP_PLAN.md`, and ends the
  run with one "Update cleanup plan" commit. A task that is not accepted is left uncommitted in
  the tree with its gaps written into the plan's "In progress", and the next run refuses to start
  until the tree is clean again.

Task order: folder moves, namespaces, American English, one god class per run, then dead code /
duplication / early returns slice by slice.

## Reporting

When a cleanup run ends (or hands off), leave a note of what remains: the next agent needs the
list of un-split god files / folders and the namespace state. `AGENTS.md`'s Layout section is
the record of record - update the paths you moved.
