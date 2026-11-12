# Workflows

Four tiers for getting work done. Pick the cheapest one that fits.

| Tier | Use when | Agents | Rough cost | Invoke |
|---|---|---|---|---|
| **Direct** | Small or well-scoped: single-area fix, a few files, a tweak, a quick investigation | none (or one pinned `sonnet` helper) | minutes | just do it in the main session |
| **Quick** (`quick`) | Most 1-6 file changes with a testable or visible result: a feature slice, a fix, a tuning change, a content batch | 1 Opus does (plans, implements, updates its docs, writes the test and the scenario), 1 Sonnet builds, runs the targeted tests and films the change; on failure one Opus fix round and a re-verify | 2 agents happy path, 3-4 on failure | `args`: string or `{task, extraContext?, fullTests?}`; it stops with `too-big` if the task needs lite or full |
| **Lite** (`plan-and-implement-lite`) | Mid-sized: a feature or fix over roughly 2-6 files in one or two areas, design mostly clear but worth a short plan | 4 Sonnet scouts, 1 Opus planner, 1-2 Opus implementers | a fraction of full | `args`: string or `{task, extraContext?, effort?: "medium"\|"high", dryRun?}` |
| **Full** (`plan-and-implement`) | Large: cross-cutting, unclear design, needs adversarial review of the plan and an independent acceptance review | 4 Sonnet scouts + Sonnet question answerers, Opus draft / resolve / red-team / final, one Opus agent per step, Opus verifier and reviewer | ~20 min, ~1M tokens | `args`: string or `{task, extraContext?, maxFixRounds?, dryRun?}` |

`cleanup-campaign` is separate: it runs the next task of `plans/CLEANUP.md`.

## The rule that holds in every tier
**Codebase scouting is always Sonnet.** Whatever model the main session or the planner or implementers use, anything that only reads the code to map it (scouts, question answerers, surveys, fact-checks) is pinned to `model: 'sonnet'`. Any ad-hoc agent you spawn must also pass `model` explicitly; unpinned agents inherit the session model.

## Lite in detail
1. **Scout** - four read-only Sonnet lenses in parallel (where the code lives, conventions, tests, risks).
2. **Plan** - exactly one Opus agent, one pass. No red-team and no resolve round: it reads the code itself to settle open questions and records assumptions. It splits the steps into workstreams. If it judges the task bigger than medium it says so in the plan overview; rerun with the full workflow then.
3. **Implement** - one Opus agent per workstream, run **sequentially** (they share one working tree and one `obj/`, so parallel builds would collide). `effort: "medium"` (default) = one workstream, `"high"` = up to two, split on a file or area boundary. The last implementer also runs the full Release build and test suite, fixes failures within the plan's intent and reviews `git diff` against the plan, listing anything left in `gaps`.

No commits are made by either workflow. There is no separate verifier or reviewer in lite; that is what the full workflow adds.

## Tips
- Run with `dryRun: true` first when the scope is unclear: it stops after the plan. Then resume or rerun with the plan's gaps and amendments in `extraContext`.
- Build with `DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release`; tests with `DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`.
- Phased plans (one phase per prompt): use the full workflow or direct work per phase, not lite across phases.
