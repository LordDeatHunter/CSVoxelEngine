export const meta = {
  name: 'plan-and-implement',
  description: 'Sonnet scouts the codebase, Fable designs the plan, Opus implements it step by step, then build/test + Fable acceptance review',
  whenToUse: 'A feature, refactor, or bug fix in CSVoxelEngine that deserves a plan before code. Pass the task as args: a string, or {task, extraContext?, maxFixRounds?, dryRun?}. dryRun stops after the plan.',
  phases: [
    { title: 'Scout', detail: 'Sonnet maps the relevant code from four angles', model: 'sonnet' },
    { title: 'Architect', detail: 'Fable drafts the plan, Sonnet answers its open questions, Fable red-teams and finalizes', model: 'fable' },
    { title: 'Implement', detail: 'Opus implements each plan step in order, building after each', model: 'opus' },
    { title: 'Verify', detail: 'Opus runs build + tests and fixes failures; Fable checks the diff against the plan', model: 'fable' },
  ],
}

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------
const task = typeof args === 'string' ? args : args && args.task
if (!task) throw new Error('Pass the task as args: a string, or {task, extraContext?, maxFixRounds?, dryRun?}')
const extraContext = (args && args.extraContext) || ''
const maxFixRounds = (args && typeof args.maxFixRounds === 'number') ? args.maxFixRounds : 2
const dryRun = !!(args && args.dryRun)

const REPO = `
Repository: C# / .NET 9 Minecraft-style voxel engine using Silk.NET. Working directory is the repo root.
Read AGENTS.md before anything else — it is the authoritative guide (all code lives in the "silkgltriangle"
namespace even though the solution is OpenGLTest.sln; Release builds only; GL calls only on the render thread;
chunk work happens on worker threads). MESHER_PLAN.md, TODO.md and PERF_TODO.md hold in-flight plans and known debt.
The system "dotnet" has no SDK — always use ~/.dotnet/dotnet.exe:
  build: ~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release
  tests: ~/.dotnet/dotnet.exe test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release
`.trim()

const TASK = `TASK:\n${task}${extraContext ? `\n\nADDITIONAL CONTEXT FROM THE USER:\n${extraContext}` : ''}`

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const SCOUT_SCHEMA = {
  type: 'object',
  properties: {
    lens: { type: 'string' },
    summary: { type: 'string', description: '3-8 sentences: what this lens found that the architect must know' },
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
    overview: { type: 'string', description: 'the design in one or two paragraphs: approach chosen and why, alternatives rejected' },
    assumptions: { type: 'array', items: { type: 'string' } },
    steps: { type: 'array', items: STEP_SCHEMA, description: 'ordered; each step must leave the solution building' },
    risks: { type: 'array', items: { type: 'string' } },
    openQuestions: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          question: { type: 'string', description: 'a concrete question answerable by reading the code' },
          whyItMatters: { type: 'string' },
        },
        required: ['question', 'whyItMatters'],
      },
      description: 'only questions whose answer would change the plan; empty if none',
    },
  },
  required: ['overview', 'assumptions', 'steps', 'risks', 'openQuestions'],
}

const ANSWER_SCHEMA = {
  type: 'object',
  properties: {
    question: { type: 'string' },
    answer: { type: 'string' },
    evidence: { type: 'array', items: { type: 'string' }, description: 'file:line references backing the answer' },
    confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
  },
  required: ['question', 'answer', 'evidence', 'confidence'],
}

const CRITIQUE_SCHEMA = {
  type: 'object',
  properties: {
    verdict: { type: 'string', enum: ['sound', 'needs-revision'] },
    issues: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          stepId: { type: 'string', description: 'step this concerns, or "plan" for cross-cutting' },
          issue: { type: 'string' },
          suggestedFix: { type: 'string' },
        },
        required: ['severity', 'stepId', 'issue', 'suggestedFix'],
      },
    },
  },
  required: ['verdict', 'issues'],
}

const IMPL_SCHEMA = {
  type: 'object',
  properties: {
    stepId: { type: 'string' },
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    summary: { type: 'string', description: 'what was changed, in enough detail that the next implementer needs no re-reading' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    buildPassed: { type: 'boolean' },
    deviations: { type: 'array', items: { type: 'string' }, description: 'anywhere you departed from the plan step and why' },
    notesForNextSteps: { type: 'array', items: { type: 'string' } },
  },
  required: ['stepId', 'status', 'summary', 'filesChanged', 'buildPassed', 'deviations', 'notesForNextSteps'],
}

const VERIFY_SCHEMA = {
  type: 'object',
  properties: {
    buildPassed: { type: 'boolean' },
    testsPassed: { type: 'boolean' },
    testSummary: { type: 'string', description: 'e.g. "84 passed, 0 failed" or the failing test names' },
    fixesApplied: { type: 'array', items: { type: 'string' } },
    unresolved: { type: 'array', items: { type: 'string' } },
  },
  required: ['buildPassed', 'testsPassed', 'testSummary', 'fixesApplied', 'unresolved'],
}

const REVIEW_SCHEMA = {
  type: 'object',
  properties: {
    accepted: { type: 'boolean' },
    summary: { type: 'string' },
    gaps: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          stepId: { type: 'string' },
          gap: { type: 'string', description: 'what the plan asked for that the code does not do, or a defect introduced' },
          fix: { type: 'string', description: 'concrete instruction for the implementer' },
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
        },
        required: ['stepId', 'gap', 'fix', 'severity'],
      },
    },
  },
  required: ['accepted', 'summary', 'gaps'],
}

// ---------------------------------------------------------------------------
// Phase 1 — Scout (Sonnet). Four lenses, each blind to the others.
// Barrier is correct here: the architect needs every report at once.
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
    prompt: `Lens: TESTS AND VERIFICATION. Map OpenGLTest.Tests/: fixtures (TestContent.cs), how headless world/chunk/player state is constructed,
which existing tests cover the area this task touches, and what a new test for this task would need. Run the test suite once and report the baseline count/pass state.`,
  },
  {
    key: 'risks',
    prompt: `Lens: RISKS AND DEBT. Identify perf-hot paths this task would touch (per-frame loops, mesh builds, light propagation, chunk streaming),
threading hazards (GL-thread-only calls, shared mutable state, worker-thread queues), and anything in TODO.md, PERF_TODO.md or MESHER_PLAN.md
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
// Phase 2 — Architect (Fable). Draft → resolve open questions via Sonnet → red-team → finalize.
// ---------------------------------------------------------------------------
phase('Architect')

const ARCHITECT_ROLE = `${REPO}

You are the software architect for this change. You design; you do not write the implementation.
You may read any file to check the scouts' claims, but do NOT modify files.
Produce a plan an implementer can execute step by step without asking questions:
- Steps are ordered and each must leave the solution building (~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release).
- Name exact files, types and methods. Say what to add, change or delete, and why.
- Respect the constraints the scouts found (threading, perf, namespace "silkgltriangle", Release-only).
- Every step has an acceptance check. Non-trivial behaviour gets an xUnit test in OpenGLTest.Tests/.
- Keep steps small enough for one focused agent each (roughly one to four files per step).
- Put only questions whose answer would CHANGE the plan in openQuestions; otherwise state an assumption.`

const planPrompt = `${ARCHITECT_ROLE}

${TASK}

## Scout reports
${scoutDigest}`

let plan = await agent(planPrompt, { label: 'architect:draft', phase: 'Architect', model: 'fable', schema: PLAN_SCHEMA })
if (!plan) throw new Error('Architect returned no plan')
log(`Draft plan: ${plan.steps.length} step(s), ${plan.openQuestions.length} open question(s)`)

// Resolve open questions with Sonnet scouts, then let Fable fold the answers in.
if (plan.openQuestions.length) {
  const answers = (await parallel(plan.openQuestions.map((q, i) => () =>
    agent(`${REPO}\n\n${TASK}\n\nYou are a read-only scout answering one precise question for the architect. Do NOT modify files.
Answer from the code — read it, cite file:line. If the code does not settle it, say so and give the most likely answer with your reasoning.

QUESTION: ${q.question}
WHY IT MATTERS: ${q.whyItMatters}`,
      { label: `scout:q${i + 1}`, phase: 'Architect', model: 'sonnet', schema: ANSWER_SCHEMA })
  ))).filter(Boolean)

  plan = await agent(`${ARCHITECT_ROLE}

${TASK}

## Scout reports
${scoutDigest}

## Your draft plan
${JSON.stringify(plan, null, 2)}

## Answers to your open questions
${answers.map(a => `Q: ${a.question}\nA (${a.confidence} confidence): ${a.answer}\nEvidence: ${a.evidence.join('; ')}`).join('\n\n')}

Revise the plan in light of the answers. Convert each answered question into either a concrete step change or an explicit assumption.
Leave openQuestions empty unless an answer was low-confidence AND the choice materially changes the design.`,
    { label: 'architect:resolve', phase: 'Architect', model: 'fable', schema: PLAN_SCHEMA }) || plan
}

// Red-team the plan with an independent Fable critic, then revise once if needed.
const critique = await agent(`${REPO}

You are an adversarial reviewer of an implementation plan. Your job is to find where it FAILS: missing steps, a step that will not build on its own,
wrong file or symbol names (verify them by reading the code), thread-safety violations (GL calls off the render thread, unsynchronised shared state),
perf regressions in hot loops, untestable acceptance criteria, contradictions with AGENTS.md or MESHER_PLAN.md, and scope creep beyond the task.
Do NOT modify files. Be specific and cite evidence. Mark severity honestly: "blocker" only if executing the plan as written would produce broken or wrong code.

${TASK}

## Scout reports
${scoutDigest}

## Plan under review
${JSON.stringify(plan, null, 2)}`,
  { label: 'architect:red-team', phase: 'Architect', model: 'fable', effort: 'high', schema: CRITIQUE_SCHEMA })

const seriousIssues = critique ? critique.issues.filter(i => i.severity !== 'minor') : []
if (critique && (critique.verdict === 'needs-revision' || seriousIssues.length)) {
  log(`Red-team found ${critique.issues.length} issue(s) (${seriousIssues.length} blocker/major); revising plan`)
  plan = await agent(`${ARCHITECT_ROLE}

${TASK}

## Scout reports
${scoutDigest}

## Current plan
${JSON.stringify(plan, null, 2)}

## Independent review of the plan
${critique.issues.map(i => `[${i.severity}] step ${i.stepId}: ${i.issue}\n  suggested fix: ${i.suggestedFix}`).join('\n')}

Produce the final plan. Address every blocker and major issue (verify the reviewer's claims against the code before accepting them —
if a claim is wrong, keep your design and record why in assumptions). Fold in minor issues where cheap.`,
    { label: 'architect:final', phase: 'Architect', model: 'fable', schema: PLAN_SCHEMA }) || plan
} else {
  log('Red-team: plan is sound')
}

log(`Final plan: ${plan.steps.length} step(s) — ${plan.steps.map(s => s.title).join(' → ')}`)

if (dryRun) {
  log('dryRun set: stopping after the plan')
  return { plan, critique, implementation: null, verification: null, review: null }
}

// ---------------------------------------------------------------------------
// Phase 3 — Implement (Opus). Strictly sequential: steps share one working tree
// and one obj/ directory, and each step must build before the next starts.
// ---------------------------------------------------------------------------
phase('Implement')

const planText = `## Plan overview
${plan.overview}

## Assumptions
${plan.assumptions.map(a => `- ${a}`).join('\n') || '- (none)'}

## Risks
${plan.risks.map(r => `- ${r}`).join('\n') || '- (none)'}

## Steps
${plan.steps.map((s, i) => `${i + 1}. [${s.id}] ${s.title}
   files: ${s.files.join(', ')}
   ${s.description}
   acceptance: ${s.acceptance}${s.testsToAdd && s.testsToAdd.length ? `\n   tests: ${s.testsToAdd.join('; ')}` : ''}`).join('\n\n')}`

const IMPLEMENTER_ROLE = `${REPO}

You are the implementer. The architecture is decided — execute your assigned step faithfully. Do not redesign, do not start other steps,
do not refactor unrelated code. If the plan is wrong in a way that blocks you, make the smallest local deviation that unblocks the step
and record it in "deviations". Work in the current working tree (no branches, no commits, no stashes).
Before returning: run ~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release and fix every error and every new warning you introduced.
If the step adds tests, run them. Set buildPassed honestly.`

const implemented = []
for (let i = 0; i < plan.steps.length; i++) {
  const step = plan.steps[i]
  log(`Implementing step ${i + 1}/${plan.steps.length}: ${step.title}`)
  const result = await agent(`${IMPLEMENTER_ROLE}

${TASK}

${planText}

## Completed so far
${implemented.length ? implemented.map(r => `- [${r.stepId}] ${r.status}: ${r.summary}${r.deviations.length ? `\n  deviations: ${r.deviations.join('; ')}` : ''}${r.notesForNextSteps.length ? `\n  notes: ${r.notesForNextSteps.join('; ')}` : ''}`).join('\n') : '- (nothing yet — you are first)'}

## YOUR STEP: ${i + 1}. [${step.id}] ${step.title}
Files: ${step.files.join(', ')}

${step.description}

Acceptance: ${step.acceptance}
${step.testsToAdd && step.testsToAdd.length ? `Tests to add: ${step.testsToAdd.join('; ')}` : ''}`,
    { label: `implement:${step.id}`, phase: 'Implement', model: 'opus', schema: IMPL_SCHEMA })

  if (!result) {
    log(`Step ${step.id} returned nothing; stopping implementation here`)
    implemented.push({ stepId: step.id, status: 'blocked', summary: 'agent returned no result', filesChanged: [], buildPassed: false, deviations: [], notesForNextSteps: [] })
    break
  }
  implemented.push(result)
  if (result.status === 'blocked') {
    log(`Step ${step.id} is blocked: ${result.summary}`)
    break
  }
  if (!result.buildPassed) log(`Warning: step ${step.id} reports the build is not passing`)
}

// ---------------------------------------------------------------------------
// Phase 4 — Verify. Opus makes build + tests green; Fable checks the diff against the plan.
// Up to maxFixRounds gap-fix rounds.
// ---------------------------------------------------------------------------
phase('Verify')

let verification = null
let review = null
for (let round = 0; round <= maxFixRounds; round++) {
  verification = await agent(`${REPO}

You are the verifier. Run the Release build and the full test suite. If anything fails, fix it with the smallest correct change that stays within
the intent of the plan below (no redesign). Re-run until green or until you are confident the failure needs a design decision — then list it in "unresolved".
Do not commit. Report the final test summary verbatim.

${TASK}

${planText}

## What the implementers report
${implemented.map(r => `- [${r.stepId}] ${r.status}: ${r.summary}`).join('\n')}`,
    { label: `verify:build-test${round ? `:r${round}` : ''}`, phase: 'Verify', model: 'opus', schema: VERIFY_SCHEMA })

  review = await agent(`${REPO}

You are the architect reviewing whether the implementation matches your plan. Run "git status" and "git diff" (plus "git diff --stat") to see
every change, read the touched files in full where needed, and check each step's acceptance criterion. Look for: plan steps skipped or half-done,
deviations that break the design, threading or perf regressions, missing tests the plan called for, dead code or leftovers, and behaviour the task
asked for that is still absent. Do NOT modify files. Accept only if the task is genuinely complete; "minor" gaps alone do not block acceptance.

${TASK}

${planText}

## Implementer reports
${implemented.map(r => `- [${r.stepId}] ${r.status}: ${r.summary}${r.deviations.length ? `\n  deviations: ${r.deviations.join('; ')}` : ''}`).join('\n')}

## Verifier report
build: ${verification ? verification.buildPassed : 'unknown'}, tests: ${verification ? verification.testsPassed : 'unknown'} — ${verification ? verification.testSummary : 'no report'}
${verification && verification.unresolved.length ? `unresolved: ${verification.unresolved.join('; ')}` : ''}`,
    { label: `review:acceptance${round ? `:r${round}` : ''}`, phase: 'Verify', model: 'fable', schema: REVIEW_SCHEMA })

  const blocking = review ? review.gaps.filter(g => g.severity !== 'minor') : []
  const green = verification && verification.buildPassed && verification.testsPassed
  if (green && review && review.accepted && !blocking.length) {
    log(`Accepted after ${round} fix round(s): ${review.summary}`)
    break
  }
  if (round === maxFixRounds) {
    log(`Stopping after ${maxFixRounds} fix round(s) with ${blocking.length} unresolved gap(s) — see result`)
    break
  }

  log(`Round ${round + 1}: fixing ${blocking.length} gap(s)${green ? '' : ' and a red build/test run'}`)
  const fix = await agent(`${IMPLEMENTER_ROLE}

${TASK}

${planText}

## Gaps the architect found in the current implementation — fix all of them
${blocking.map(g => `[${g.severity}] step ${g.stepId}: ${g.gap}\n  fix: ${g.fix}`).join('\n') || '- (none)'}

## Verifier's unresolved items
${verification && verification.unresolved.length ? verification.unresolved.map(u => `- ${u}`).join('\n') : '- (none)'}`,
    { label: `implement:fix-r${round + 1}`, phase: 'Verify', model: 'opus', schema: IMPL_SCHEMA })
  if (fix) implemented.push(fix)
}

return {
  plan,
  critique,
  implementation: implemented,
  verification,
  review,
  remainingGaps: review ? review.gaps : [],
}
