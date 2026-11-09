export const meta = {
  name: 'quick',
  description: 'Lean workflow: one Opus agent plans and implements in a single context, one Sonnet agent builds, runs targeted tests and films the change, one Opus fix round only on failure',
  whenToUse: 'Most CSVoxelEngine changes touching roughly 1-6 files with a testable or visible result: a feature slice, a fix, a tuning change, a new content batch. 2 agents on the happy path (3-4 on failure). If the task is bigger or the design is unclear the do agent stops and says so: then use plan-and-implement-lite or plan-and-implement. Pass the task as args: a string, or {task, extraContext?, fullTests?}. fullTests runs the whole suite instead of only the touched areas. No commits are made.',
  phases: [
    { title: 'Do', detail: 'One Opus agent plans and implements, updates docs, writes the test and the scenario', model: 'opus' },
    { title: 'Verify', detail: 'Sonnet builds, runs the targeted tests and films the visual check', model: 'sonnet' },
    { title: 'Fix', detail: 'Only on failure: Opus fixes from the failure report, Sonnet re-verifies once', model: 'opus' },
  ],
}

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------
const task = typeof args === 'string' ? args : args && args.task
if (!task) throw new Error('Pass the task as args: a string, or {task, extraContext?, fullTests?}')
const extraContext = (args && args.extraContext) || ''
const fullTests = !!(args && args.fullTests)

const REPO = `
Repository: C# / .NET 9 Minecraft-style voxel engine using Silk.NET. Working directory is the repo root.
AGENTS.md is already in your context. It is small: it holds the non-negotiable rules, the standing code-organization rules and a topic index into docs/agents/.
TOKEN RULE: never read a whole docs file. Find the section with  grep -n "^#\\|^\\*\\*" docs/agents/<file>.md  and Read only that range (offset/limit).
To find where a type or file lives:  grep -rn "<name>" docs/agents/  (the layout-*.md files list every source file with its purpose), then read the source.
Skills (.claude/skills/*/SKILL.md) are short procedures: read only the ones your task needs (add-content, edit-path-checklist, shader-sync, functional-tests, visual-verify, item-debugger, biome-worldgen, crlf-safe-edit).
The code is C# 14 and needs the .NET 10 SDK, which is the system "dotnet" (~/.dotnet is a 9.0.100 SDK that cannot compile it); prefix with DOTNET_ROLL_FORWARD=LatestMajor:
  build: DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release
  tests: DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --filter "<filter>"
The working tree is CRLF: edit with the Edit tool or byte-mode scripts, never sed -i or text-mode rewrites.
`.trim()

const TASK = `TASK:\n${task}${extraContext ? `\n\nADDITIONAL CONTEXT FROM THE USER:\n${extraContext}` : ''}`

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const DO_SCHEMA = {
  type: 'object',
  properties: {
    tooBig: { type: 'boolean', description: 'true if the task needs more than ~6 files, touches several areas, or has an unclear design: you changed NOTHING and the user should run plan-and-implement-lite or plan-and-implement' },
    tooBigReason: { type: 'string', description: 'with tooBig: why, and a one-paragraph sketch of how to split or approach it' },
    summary: { type: 'string', description: 'what you changed and why, 3-8 sentences' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    docsUpdated: { type: 'array', items: { type: 'string' }, description: 'docs/agents files, USER_TODO.md and plans/ files you edited' },
    testFilter: { type: 'string', description: 'a dotnet test --filter expression covering the tests for the touched areas and any test you added, e.g. "FullyQualifiedName~VoxelEngine.Tests.Lod|FullyQualifiedName~VisualScenarioTests". Empty only if nothing testable changed' },
    visualCheck: {
      type: 'object',
      description: 'omit when the change has no visible result',
      properties: {
        scenario: { type: 'string', description: 'repo-relative path of the scenario JSON (new or existing), 3-5 screenshots' },
        voxelTime: { type: 'string', description: 'VOXEL_TIME value, or empty' },
        expect: { type: 'array', items: { type: 'string' }, description: 'one concrete observable per screenshot, in order, e.g. "shot 2: the lava surface shows darker patches"' },
      },
      required: ['scenario', 'voxelTime', 'expect'],
    },
    commitMessage: { type: 'string', description: 'short imperative sentence in the repo style' },
    caveats: { type: 'array', items: { type: 'string' } },
  },
  required: ['tooBig', 'summary', 'filesChanged', 'docsUpdated', 'testFilter', 'commitMessage', 'caveats'],
}

const VERIFY_SCHEMA = {
  type: 'object',
  properties: {
    buildPassed: { type: 'boolean' },
    warnings: { type: 'number', description: 'build warning count; must be 0' },
    testsPassed: { type: 'boolean', description: 'true only if the filtered tests ran and all passed' },
    testSummary: { type: 'string', description: 'e.g. "31 passed, 0 failed" or the failing test names' },
    visual: { type: 'string', enum: ['pass', 'fail', 'not-run', 'not-needed'] },
    visualNotes: { type: 'string', description: 'what you actually saw in each screenshot you read, one line each; the verdict against each expectation' },
    failures: { type: 'array', items: { type: 'string' }, description: 'ONLY failing output: compiler errors, failing test names with the first assertion lines, a screenshot that does not match. Max ~40 lines total; never full logs' },
  },
  required: ['buildPassed', 'warnings', 'testsPassed', 'testSummary', 'visual', 'visualNotes', 'failures'],
}

// ---------------------------------------------------------------------------
// Phase 1 — Do. One Opus agent, one context: plan in its head, implement, document.
// ---------------------------------------------------------------------------
const DO_ROLE = `You are the single engineer on this task. You plan and implement it yourself in this one context; there is no separate planner and no reviewer after you, so be careful and be complete.

Process:
1. Orient cheaply: use the topic index in AGENTS.md, grep the docs for the section that owns the area, read that section (ranges only) and the 1-3 source files involved. Do not survey the codebase.
2. Decide the design. If the task needs more than about 6 files, spans several areas, or its design is genuinely unclear, change NOTHING: return tooBig=true with the reason and a sketch (the user will run plan-and-implement-lite or plan-and-implement).
3. Implement. Follow AGENTS.md's non-negotiables and standing rules (namespaces mirror folders, American English, no dead code, early returns, constants in Config/, new content as JSON). Use the relevant skill for block-edit paths, shaders and content.
4. Tests: add a test only where there is a rule to lock down (functional-tests skill), never a constant or layout pin.
5. Docs, in the same change: edit the docs/agents/ section that owns what you changed (and its layout-*.md line if a file was added, moved or removed); record any placeholder art in USER_TODO.md; tick or add the plans/ item if one exists for this work. Keep the edits minimal and in the surrounding style. Do not touch AGENTS.md unless the index, a non-negotiable or a standing rule changed.
6. Visible result? Write or extend ONE small VOXEL_SCRIPT scenario in OpenGLTest/visual-examples/ (3-5 screenshots, reuse the VOXEL_TIME of the nearest existing scenario; see the visual-verify skill and grep docs/agents/dev-tools.md for the ops). Give each screenshot one concrete expectation in visualCheck.expect. Animations, held items and models: also add or check the item debugger mode (item-debugger skill).
7. Do NOT build, run tests or launch the game: the verify agent does that. You may run a quick compile-only check if you are unsure about an API, but keep it to that.
No branches, no commits, no stashes. Work in the current working tree.`

phase('Do')
const done = await agent(`${REPO}\n\n${DO_ROLE}\n\n${TASK}`,
  { label: 'do', phase: 'Do', model: 'opus', schema: DO_SCHEMA })

if (!done) throw new Error('The do agent returned nothing')
if (done.tooBig) {
  log(`Too big for quick: ${done.tooBigReason}`)
  return { status: 'too-big', reason: done.tooBigReason, nextStep: 'run plan-and-implement-lite (mid-sized) or plan-and-implement (large or unclear design) with this task and the sketch as extraContext' }
}

// ---------------------------------------------------------------------------
// Phase 2 — Verify. Sonnet absorbs the build, test and screenshot volume.
// ---------------------------------------------------------------------------
const testClause = fullTests
  ? 'Run the FULL test suite: DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build'
  : (done.testFilter
    ? `Run ONLY the targeted tests: DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build --filter "${done.testFilter}". If the filter matches nothing, say so and set testsPassed=false.`
    : 'No tests are targeted for this change: skip the test run and set testsPassed=true with testSummary "none targeted".')

const visualClause = done.visualCheck
  ? `VISUAL CHECK. Follow the visual-verify skill's run recipe (PowerShell, Release; working directory OpenGLTest/ so assets/ resolves; do not run dotnet build while the game is running).
Scenario: ${done.visualCheck.scenario}${done.visualCheck.voxelTime ? `, VOXEL_TIME=${done.visualCheck.voxelTime}` : ''}. Write screenshots to a folder under your scratchpad directory (the scenario "output" key accepts an absolute path) and write the game log there too.
Wait for the run to finish (the window may not close itself: poll for the last numbered PNG, then stop the process by PID). Then Read AT MOST 5 PNGs, one per expectation, and judge each honestly:
${done.visualCheck.expect.map((e, i) => `  ${i + 1}. ${e}`).join('\n')}
If the game crashes or a scenario op fails, put the relevant log lines (last ~20) in failures. Never claim a visual result you did not look at: if you could not run it, set visual to "not-run".`
  : 'No visual check for this change: set visual to "not-needed".'

const VERIFY_PROMPT = (changeText) => `${REPO}

You are the verifier. You change NO source files. You build, test and film the change, then report briefly.

CHANGE UNDER TEST:
${changeText}

1. Build: DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release. Report the warning count (must be 0) and, on failure, only the error lines.
2. ${testClause} On failures, report test names and the first assertion lines only.
3. ${visualClause}
Return the structured report. failures must hold only what a fixer needs, never full logs.`

const changeText = `${done.summary}\nFiles: ${done.filesChanged.join(', ')}`

phase('Verify')
let report = await agent(VERIFY_PROMPT(changeText), { label: 'verify', phase: 'Verify', model: 'sonnet', schema: VERIFY_SCHEMA })
const okOf = r => !!r && r.buildPassed && r.warnings === 0 && r.testsPassed && (r.visual === 'pass' || r.visual === 'not-needed')

// ---------------------------------------------------------------------------
// Phase 3 — Fix, once, only on failure.
// ---------------------------------------------------------------------------
let fixed = null
if (!okOf(report)) {
  log(`Verification failed (build ${report && report.buildPassed}, tests ${report && report.testsPassed}, visual ${report && report.visual}); one fix round`)
  phase('Fix')
  fixed = await agent(`${REPO}

You are the fixer. A change was just implemented and verification failed. Fix the failures with the smallest correct change that stays within the original intent (no redesign, no unrelated cleanup).
Read the failing code and the diff (git diff) yourself; the verifier's report is below. Do not build, run tests or launch the game: the verifier re-runs everything after you.
If a failure means the visual expectation itself was wrong (the scenario or the expectation, not the code), fix the scenario or say so in the summary.

${TASK}

WHAT WAS DONE:
${done.summary}
Files: ${done.filesChanged.join(', ')}

VERIFICATION REPORT:
build passed: ${report && report.buildPassed}, warnings: ${report && report.warnings}
tests: ${report && report.testsPassed} (${report && report.testSummary})
visual: ${report && report.visual}
${report ? report.visualNotes : ''}
FAILURES:
${report ? report.failures.join('\n') : 'the verifier returned nothing'}`,
    { label: 'fix', phase: 'Fix', model: 'opus', schema: {
      type: 'object',
      properties: {
        summary: { type: 'string' },
        filesChanged: { type: 'array', items: { type: 'string' } },
        unresolved: { type: 'array', items: { type: 'string' } },
      },
      required: ['summary', 'filesChanged', 'unresolved'],
    } })

  if (fixed) {
    phase('Verify')
    report = await agent(VERIFY_PROMPT(`${done.summary}\nFiles: ${done.filesChanged.join(', ')}\nA fix round then changed: ${fixed.filesChanged.join(', ')} (${fixed.summary})`),
      { label: 'verify:after-fix', phase: 'Verify', model: 'sonnet', schema: VERIFY_SCHEMA })
  }
}

const green = okOf(report)
log(green ? `Done and verified. ${report.testSummary}` : 'Finished NOT green: see failures in the result')

return {
  status: green ? 'green' : 'not-green',
  summary: done.summary,
  filesChanged: [...done.filesChanged, ...(fixed ? fixed.filesChanged : [])],
  docsUpdated: done.docsUpdated,
  verification: report,
  fixRound: fixed,
  caveats: done.caveats,
  commitMessage: done.commitMessage,
}
