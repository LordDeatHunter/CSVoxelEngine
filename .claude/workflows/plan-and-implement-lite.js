export const meta = {
  name: 'plan-and-implement-lite',
  description: 'Medium-effort workflow: Sonnet scouts the codebase, ONE Opus agent plans, then 1-2 Opus agents implement sequentially (the last one also builds, tests and self-reviews the diff)',
  whenToUse: 'Mid-sized work in CSVoxelEngine: a feature or fix spanning roughly 2-6 files in one or two areas, with a mostly clear design that still deserves a short plan. Cheaper than plan-and-implement (no red-team, no separate verifier or acceptance review); for cross-cutting or design-unclear work use plan-and-implement, and for small well-scoped changes do it directly. Pass the task as args: a string, or {task, extraContext?, effort?: "medium"|"high", dryRun?}. effort "medium" (default) = 1 Opus implementer, "high" = up to 2. dryRun stops after the plan. Scouting is always Sonnet.',
  phases: [
    { title: 'Scout', detail: 'Sonnet maps the relevant code from four angles', model: 'sonnet' },
    { title: 'Plan', detail: 'One Opus agent writes the plan and splits it into 1-2 workstreams', model: 'opus' },
    { title: 'Implement', detail: 'Opus implements each workstream in order; the last one builds, tests and self-reviews', model: 'opus' },
  ],
}

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------
const task = typeof args === 'string' ? args : args && args.task
if (!task) throw new Error('Pass the task as args: a string, or {task, extraContext?, effort?, dryRun?}')
const extraContext = (args && args.extraContext) || ''
const effort = (args && args.effort === 'high') ? 'high' : 'medium'
const maxWorkstreams = effort === 'high' ? 2 : 1
const dryRun = !!(args && args.dryRun)

const REPO = `
Repository: C# / .NET 9 Minecraft-style voxel engine using Silk.NET. Working directory is the repo root.
Read AGENTS.md before anything else — it is the authoritative guide (all code lives under the "VoxelEngine" root namespace with one sub-namespace per folder; Release builds only; GL calls only on the render thread;
chunk work happens on worker threads). plans/TODO.md and plans/PERF.md hold in-flight plans and known debt.
The system "dotnet" has no SDK — always use ~/.dotnet/dotnet.exe:
  build: ~/.dotnet/dotnet.exe build VoxelEngine.sln -c Release
  tests: ~/.dotnet/dotnet.exe test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release
`.trim()

const TASK = `TASK:\n${task}${extraContext ? `\n\nADDITIONAL CONTEXT FROM THE USER:\n${extraContext}` : ''}`

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const SCOUT_SCHEMA = {
  type: 'object',
  properties: {
    lens: { type: 'string' },
    summary: { type: 'string', description: '3-8 sentences: what this lens found that the planner must know' },
    files: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          path: { type: 'string' },
          why: { type: 'string' },
          keySymbols: { type: 'array', items: { type: 'string' }, description: 'types/methods with line refs, e.g. "ChunkMesher.Build (ChunkMesher.cs:120)"' },
        },
        required: ['path', 'why'],
      },
    },
    constraints: { type: 'array', items: { type: 'string' }, description: 'rules the implementation must respect (threading, perf, naming, data layout)' },
    openQuestions: { type: 'array', items: { type: 'string' }, description: 'things you could not settle from the code alone' },
  },
  required: ['lens', 'summary', 'files', 'constraints', 'openQuestions'],
}

const STEP_SCHEMA = {
  type: 'object',
  properties: {
    id: { type: 'string', description: 'short slug, e.g. "1-add-palette-type"' },
    title: { type: 'string' },
    files: { type: 'array', items: { type: 'string' }, description: 'files to create or edit' },
    description: { type: 'string', description: 'precise instructions: what changes, where, why; name the exact types/methods' },
    acceptance: { type: 'string', description: 'how the implementer proves this step is done (build, specific test, observable behavior)' },
    testsToAdd: { type: 'array', items: { type: 'string' } },
  },
  required: ['id', 'title', 'files', 'description', 'acceptance'],
}

const PLAN_SCHEMA = {
  type: 'object',
  properties: {
    overview: { type: 'string', description: 'the design in one or two paragraphs: approach chosen and why. If the task is clearly bigger or less clear than a medium task, say so here and recommend plan-and-implement' },
    assumptions: { type: 'array', items: { type: 'string' } },
    steps: { type: 'array', items: STEP_SCHEMA, description: 'ordered; each step must leave the solution building' },
    risks: { type: 'array', items: { type: 'string' } },
    workstreams: {
      type: 'array',
      description: `1 to ${maxWorkstreams} workstreams, run in order by one implementer each; together they must cover every step id exactly once, in plan order`,
      items: {
        type: 'object',
        properties: {
          name: { type: 'string' },
          stepIds: { type: 'array', items: { type: 'string' } },
        },
        required: ['name', 'stepIds'],
      },
    },
  },
  required: ['overview', 'assumptions', 'steps', 'risks', 'workstreams'],
}

const IMPL_SCHEMA = {
  type: 'object',
  properties: {
    workstream: { type: 'string' },
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    summary: { type: 'string', description: 'what was changed, in enough detail that the next implementer needs no re-reading' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    buildPassed: { type: 'boolean' },
    testsPassed: { type: 'boolean', description: 'true only if the full test suite was run and passed; false if not run' },
    testSummary: { type: 'string', description: 'e.g. "84 passed, 0 failed" or the failing test names; "not run" if not run' },
    deviations: { type: 'array', items: { type: 'string' }, description: 'anywhere you departed from the plan and why' },
    gaps: { type: 'array', items: { type: 'string' }, description: 'plan items still undone, defects you could not fix, or review findings left open' },
    notesForNextSteps: { type: 'array', items: { type: 'string' } },
  },
  required: ['workstream', 'status', 'summary', 'filesChanged', 'buildPassed', 'testsPassed', 'testSummary', 'deviations', 'gaps', 'notesForNextSteps'],
}

// ---------------------------------------------------------------------------
// Phase 1 — Scout. ALWAYS Sonnet, never configurable. Four lenses, each blind to the others.
// ---------------------------------------------------------------------------
const LENSES = [
  {
    key: 'where',
    prompt: `Lens: WHERE THE CODE LIVES. Find every file, type and method that implements or touches the behaviour this task concerns.
Trace the call graph both ways (who calls it, what it calls). Give line references. Note the data structures involved and where they are allocated/owned.`,
  },
  {
    key: 'conventions',
    prompt: `Lens: CONVENTIONS AND PRECEDENT. Find how comparable features were built in this codebase so the new work matches. Look at naming,
file placement, how work is split between the render thread and worker threads, how state is passed around, how config/constants are exposed,
and how similar features were tested. Check recent git log (git log --oneline -30, and git log -p on the most relevant files) for patterns in flight.`,
  },
  {
    key: 'tests',
    prompt: `Lens: TESTS AND VERIFICATION. Map OpenGLTest.Tests/: fixtures (Support/TestContent.cs), how headless world/chunk/player state is constructed,
which existing tests cover the area this task touches, and what a new test for this task would need. Run the test suite once and report the baseline count/pass state.`,
  },
  {
    key: 'risks',
    prompt: `Lens: RISKS AND DEBT. Identify perf-hot paths this task would touch (per-frame loops, mesh builds, light propagation, chunk streaming),
threading hazards (GL-thread-only calls, shared mutable state, worker-thread queues), and anything in plans/TODO.md or plans/PERF.md
that overlaps or conflicts with this task. Flag places where a naive implementation would regress framerate or introduce races.`,
  },
]

phase('Scout')
log(`Scouting the codebase for: ${task.slice(0, 120)}${task.length > 120 ? '...' : ''}`)
const scoutReports = (await parallel(LENSES.map(l => () =>
  agent(`${REPO}\n\n${TASK}\n\nYou are a read-only scout. Do NOT modify files. ${l.prompt}
Be concrete: paths, symbols, line numbers. Prefer facts you verified by reading over inference.`,
    { label: `scout:${l.key}`, phase: 'Scout', model: 'sonnet', schema: SCOUT_SCHEMA })
))).filter(Boolean)

if (scoutReports.length < LENSES.length) log(`Warning: ${LENSES.length - scoutReports.length} scout(s) returned nothing; planning with partial context`)

const scoutDigest = scoutReports.map(r => `### Lens: ${r.lens}
${r.summary}

Files:
${r.files.map(f => `- ${f.path} — ${f.why}${f.keySymbols && f.keySymbols.length ? `\n    ${f.keySymbols.join('\n    ')}` : ''}`).join('\n')}

Constraints:
${r.constraints.map(c => `- ${c}`).join('\n') || '- (none)'}

Open questions:
${r.openQuestions.map(q => `- ${q}`).join('\n') || '- (none)'}`).join('\n\n')

// ---------------------------------------------------------------------------
// Phase 2 — Plan. One Opus agent, one pass: no red-team, no resolve round.
// ---------------------------------------------------------------------------
phase('Plan')

const plan = await agent(`${REPO}

You are the software architect for this change. You design; you do not write the implementation. Do NOT modify files.
The scouts' open questions are yours to settle: read the code yourself, and where it does not settle them, record an assumption. There is no second planning round.
Produce a plan an implementer can execute without asking questions:
- Steps are ordered and each must leave the solution building (~/.dotnet/dotnet.exe build VoxelEngine.sln -c Release).
- Name exact files, types and methods. Say what to add, change or delete, and why.
- Respect the constraints the scouts found (threading, perf, namespace "VoxelEngine", Release-only).
- Every step has an acceptance check. Non-trivial behaviour gets an xUnit test in OpenGLTest.Tests/ (logic only; never assert constants or content literals).
- Group the steps into 1 to ${maxWorkstreams} workstream(s), each run by one Opus implementer in order. ${maxWorkstreams === 1 ? 'Use exactly one workstream holding every step.' : 'Use two only when the task splits cleanly on a file or area boundary and each half is substantial; otherwise use one. The second workstream builds on the first and does the final verification.'}
- This is the medium-effort workflow. If the task is clearly cross-cutting or its design is unclear enough to need adversarial review, plan it anyway but say so at the top of the overview and recommend plan-and-implement.

${TASK}

## Scout reports
${scoutDigest}`,
  { label: 'plan', phase: 'Plan', model: 'opus', schema: PLAN_SCHEMA })
if (!plan) throw new Error('Planner returned no plan')

// Normalize workstreams: every step exactly once, in plan order, at most maxWorkstreams of them.
const stepIds = plan.steps.map(s => s.id)
let workstreams = (plan.workstreams || []).slice(0, maxWorkstreams)
const assigned = new Set(workstreams.flatMap(w => w.stepIds))
const missing = stepIds.filter(id => !assigned.has(id))
if (!workstreams.length) workstreams = [{ name: 'all', stepIds }]
else if (missing.length) workstreams[workstreams.length - 1] = { ...workstreams[workstreams.length - 1], stepIds: [...workstreams[workstreams.length - 1].stepIds, ...missing] }
log(`Plan: ${plan.steps.length} step(s) in ${workstreams.length} workstream(s) — ${plan.steps.map(s => s.title).join(' → ')}`)

if (dryRun) {
  log('dryRun set: stopping after the plan')
  return { plan, workstreams, implementation: null, verification: null }
}

// ---------------------------------------------------------------------------
// Phase 3 — Implement. Sequential: workstreams share one working tree and one obj/ directory.
// The last workstream also runs the full build + tests and reviews the diff against the plan.
// ---------------------------------------------------------------------------
phase('Implement')

const stepText = s => `[${s.id}] ${s.title}
   files: ${s.files.join(', ')}
   ${s.description}
   acceptance: ${s.acceptance}${s.testsToAdd && s.testsToAdd.length ? `\n   tests: ${s.testsToAdd.join('; ')}` : ''}`

const planText = `## Plan overview
${plan.overview}

## Assumptions
${plan.assumptions.map(a => `- ${a}`).join('\n') || '- (none)'}

## Risks
${plan.risks.map(r => `- ${r}`).join('\n') || '- (none)'}

## All steps
${plan.steps.map((s, i) => `${i + 1}. ${stepText(s)}`).join('\n\n')}`

const IMPLEMENTER_ROLE = `${REPO}

You are an implementer. The design is decided — execute your assigned workstream faithfully, step by step. Do not redesign, do not do other workstreams,
do not refactor unrelated code. If the plan is wrong in a way that blocks you, make the smallest local deviation that unblocks you and record it in "deviations".
Work in the current working tree (no branches, no commits, no stashes).
Run ~/.dotnet/dotnet.exe build VoxelEngine.sln -c Release after each step and fix every error and every new warning you introduced. Set buildPassed honestly.`

const implemented = []
for (let w = 0; w < workstreams.length; w++) {
  const ws = workstreams[w]
  const isLast = w === workstreams.length - 1
  const steps = plan.steps.filter(s => ws.stepIds.includes(s.id))
  log(`Implementing workstream ${w + 1}/${workstreams.length}: ${ws.name} (${steps.length} step(s))`)

  const finalDuty = isLast ? `

## Final duty (you are the last implementer)
After your steps: run the FULL Release build and the FULL test suite. Fix failures with the smallest correct change that stays within the plan's intent (no redesign); re-run until green.
Then run "git status" and "git diff" and check every plan step's acceptance criterion across ALL workstreams: steps skipped or half-done, deviations that break the design,
threading or perf regressions, missing tests the plan called for, dead code or leftovers. Fix what you can; list anything left in "gaps". Report the test summary verbatim.`
    : `

Do not run the full test suite; run only tests you add. The last workstream does the final verification.`

  const result = await agent(`${IMPLEMENTER_ROLE}

${TASK}

${planText}

## Completed so far
${implemented.length ? implemented.map(r => `- [${r.workstream}] ${r.status}: ${r.summary}${r.deviations.length ? `\n  deviations: ${r.deviations.join('; ')}` : ''}${r.notesForNextSteps.length ? `\n  notes: ${r.notesForNextSteps.join('; ')}` : ''}`).join('\n') : '- (nothing yet — you are first)'}

## YOUR WORKSTREAM ${w + 1}/${workstreams.length}: ${ws.name}
${steps.map(stepText).join('\n\n')}${finalDuty}`,
    { label: `implement:${ws.name}`, phase: 'Implement', model: 'opus', schema: IMPL_SCHEMA })

  if (!result) {
    log(`Workstream ${ws.name} returned nothing; stopping implementation here`)
    implemented.push({ workstream: ws.name, status: 'blocked', summary: 'agent returned no result', filesChanged: [], buildPassed: false, testsPassed: false, testSummary: 'not run', deviations: [], gaps: ['agent returned no result'], notesForNextSteps: [] })
    break
  }
  implemented.push(result)
  if (result.status === 'blocked') {
    log(`Workstream ${ws.name} is blocked: ${result.summary}`)
    break
  }
}

const last = implemented[implemented.length - 1]
const green = !!last && last.buildPassed && last.testsPassed
log(green ? `Done: build and tests green. ${last.testSummary}` : 'Finished NOT green or incomplete — see gaps in the result')

return {
  plan,
  workstreams,
  implementation: implemented,
  verification: last ? { buildPassed: last.buildPassed, testsPassed: last.testsPassed, testSummary: last.testSummary } : null,
  remainingGaps: implemented.flatMap(r => r.gaps),
}
