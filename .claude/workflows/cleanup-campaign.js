export const meta = {
  name: 'cleanup-campaign',
  description: 'Runs the next CSVoxelEngine cleanup task(s) from plans/CLEANUP.md: Opus directs, plans, assigns and reviews; Sonnet surveys, scouts, fact-checks, implements, verifies, audits the move-diff residue and commits; Opus takes the wheel for steps too complex for Sonnet',
  whenToUse: 'Continuing the codebase cleanup campaign tracked in plans/CLEANUP.md (god-class splits, dead code, duplication, nesting, doc follow-ups). No args = the next task in the plan. Args: a task string, or {task?, tasks?, extraContext?, maxFixRounds?, dryRun?, commit?, implementer?}. Commits each accepted task and records it in plans/CLEANUP.md unless commit is false; stops at the first task that is not accepted.',
  phases: [
    { title: 'Survey', detail: 'Sonnet reads plans/CLEANUP.md, measures the queue and checks the tree is clean', model: 'sonnet' },
    { title: 'Direct', detail: 'Opus picks the task, writes its brief and the scout assignments', model: 'opus' },
    { title: 'Scout', detail: 'Sonnet maps the code from the angles Opus asked for', model: 'sonnet' },
    { title: 'Plan', detail: 'Opus plans and assigns every step to Sonnet or itself; Sonnet answers its questions and fact-checks the plan', model: 'opus' },
    { title: 'Implement', detail: 'Sonnet implements step by step; Opus takes the steps it reserved and any step Sonnet could not finish', model: 'sonnet' },
    { title: 'Verify', detail: 'Sonnet builds, tests and audits the move-diff residue; Opus accepts or sends gaps back', model: 'opus' },
    { title: 'Record', detail: 'Sonnet commits the task and records it in plans/CLEANUP.md', model: 'sonnet' },
  ],
}

// ---------------------------------------------------------------------------
// Roles. Opus calls the shots: it picks the task, plans it, decides who implements each step,
// takes over the steps that need judgment and accepts or rejects the result. Sonnet does the
// reading and most of the writing: it surveys the plan, scouts the code, answers Opus' questions,
// fact-checks the plan, implements the steps, runs the build and tests, audits the diff and
// commits. Opus reads code itself only to settle a point a decision hinges on.
// ---------------------------------------------------------------------------
const SONNET = 'sonnet'
const OPUS = 'opus'
const TRAILER = 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------
const input = typeof args === 'string' ? { task: args } : (args || {})
const requestedTask = (input.task || '').trim()
const taskLimit = typeof input.tasks === 'number' && input.tasks >= 1 ? Math.floor(input.tasks) : 1
const extraContext = (input.extraContext || '').trim()
const maxFixRounds = typeof input.maxFixRounds === 'number' && input.maxFixRounds >= 0 ? Math.floor(input.maxFixRounds) : 1
const dryRun = !!input.dryRun
const commit = input.commit !== false
const forcedImplementer = input.implementer === SONNET || input.implementer === OPUS ? input.implementer : null

const IGNORED = '.claude/worktrees/, .sisyphus/, .run/, .gitattributes, visual/ and plans/CLEANUP.md'

// The standing context every agent gets. The run facts (baseline, flaky tests) come from the
// survey, so plans/CLEANUP.md stays their one source.
function context(survey) {
  return `Repository: CSVoxelEngine, a C# / .NET voxel engine (Silk.NET, OpenGL 4.6). Working directory = repo root.
AGENTS.md (already in your context) is the authoritative guide; its "Code organization (standing rules)" section and
.claude/skills/code-cleanup/SKILL.md define what clean means. plans/CLEANUP.md (repo root) is the campaign's handoff.

Toolchain: the code is C# 14 and needs the .NET 10 SDK, which is the system "dotnet" (~/.dotnet is a 9.0.100 SDK that cannot compile it).
  build: DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release
  tests: DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release
  (In PowerShell run $env:DOTNET_ROLL_FORWARD='LatestMajor' first.) Release only, never Debug.
Baseline: ${survey.baseline || '0 warnings, every test passing'}.
Known flaky tests (re-run a failure alone, by class filter, before treating it as real; never edit a test to stabilize it):
${survey.flakyTests.length ? survey.flakyTests.map(t => `  - ${t}`).join('\n') : '  - (none recorded)'}

Cleanup rules for every agent in this run:
- Behavior-preserving only. Move code verbatim first (same statements, same order, same comments); never rewrite logic while moving.
  Early returns / nesting fixes only where the task asks for them.
- Do NOT add or edit tests except what a move or rename forces (call sites, qualified names, usings). No visual scenarios, no in-game runs.
  The only gate is a zero-warning Release build plus the existing xUnit suite.
- Namespaces mirror folders (VoxelEngine.*, one per folder); keep every folder at or under ~22 files; never name a folder, namespace or class
  after an existing type or namespace segment. American English in new code, comments and docs.
- The working tree is CRLF (core.autocrlf=true) but a few files are LF-only: keep each file's own line endings, BOM and final-newline state;
  make new .cs files CRLF like their neighbors. Never sed -i a source file; scripted edits follow the crlf-safe-edit skill (byte mode).
- Do not commit, stash, branch, reset or touch git config unless your role says so. Leave ${IGNORED} and .claude/workflows/ alone
  (plans/CLEANUP.md is edited only by the recorder).`
}

function taskBlock(brief) {
  return `## Cleanup task: ${brief.title} (${brief.kind})
${brief.taskText}
${brief.invariants.length ? `\nInvariants the director wants held:\n${brief.invariants.map(i => `- ${i}`).join('\n')}` : ''}${extraContext ? `\n\nAdditional context from the user:\n${extraContext}` : ''}`
}

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const SURVEY_SCHEMA = {
  type: 'object',
  properties: {
    head: { type: 'string', description: 'output of git log --oneline -1' },
    dirtyPaths: { type: 'array', items: { type: 'string' }, description: `every path git status --porcelain reports, EXCEPT ${IGNORED}` },
    planDirty: { type: 'boolean', description: 'plans/CLEANUP.md has uncommitted changes' },
    baseline: { type: 'string', description: 'the baseline the plan records, e.g. "0 warnings, 2441 tests"' },
    flakyTests: { type: 'array', items: { type: 'string' }, description: 'the known flaky tests or classes, names verbatim, with any note on why' },
    inProgress: {
      type: 'object',
      properties: {
        present: { type: 'boolean' },
        title: { type: 'string' },
        taskText: { type: 'string', description: 'the in-progress task text (the fenced block), copied VERBATIM and in full; empty if none' },
        status: { type: 'string', description: 'what the plan says about its state' },
      },
      required: ['present', 'title', 'taskText', 'status'],
    },
    queue: {
      type: 'array',
      description: 'every remaining entry of "Open", in the plan\'s order',
      items: {
        type: 'object',
        properties: {
          section: { type: 'string', enum: ['god-class', 'dead-code', 'duplication', 'nesting', 'follow-up'] },
          item: { type: 'string', description: 'the entry as the plan states it' },
          plannedLines: { type: 'number', description: 'the line count the plan lists, 0 if none' },
          currentLines: { type: 'number', description: 'the file\'s line count measured now, 0 if the entry is not one file' },
          notes: { type: 'string', description: 'the plan\'s notes, including "has a stated reason", "vendored", "borderline", "lower priority"' },
        },
        required: ['section', 'item', 'plannedLines', 'currentLines', 'notes'],
      },
    },
    excluded: { type: 'array', items: { type: 'string' }, description: 'what the plan says is NOT a cleanup task (the "Bugs found" section, vendored files)' },
    precedentCommits: { type: 'array', items: { type: 'string' }, description: 'always empty: the plan keeps no Done table any more' },
  },
  required: ['head', 'dirtyPaths', 'planDirty', 'baseline', 'flakyTests', 'inProgress', 'queue', 'excluded', 'precedentCommits'],
}

const BRIEF_SCHEMA = {
  type: 'object',
  properties: {
    done: { type: 'boolean', description: 'true only when no cleanup task worth a run is left' },
    title: { type: 'string', description: 'short, e.g. "Split Program.cs"' },
    kind: { type: 'string', enum: ['god-class-split', 'dead-code', 'duplication', 'nesting', 'follow-up', 'other'] },
    taskText: { type: 'string', description: 'the complete task for the planner (the plan\'s own text verbatim when it has one)' },
    targets: { type: 'array', items: { type: 'string' }, description: 'the main files or folders the task works on' },
    invariants: { type: 'array', items: { type: 'string' }, description: 'invariants every agent must hold that the task text does not already state; may be empty' },
    scouts: {
      type: 'array',
      description: '3-5 Sonnet scout assignments, one lens each',
      items: {
        type: 'object',
        properties: {
          key: { type: 'string', description: 'short slug, e.g. "jobs-map"' },
          brief: { type: 'string', description: 'the lens: exactly what to find and report' },
        },
        required: ['key', 'brief'],
      },
    },
    commitSubject: { type: 'string', description: 'draft one-line imperative commit subject' },
    rationale: { type: 'string', description: 'why this task now' },
  },
  required: ['done', 'title', 'kind', 'taskText', 'targets', 'invariants', 'scouts', 'commitSubject', 'rationale'],
}

const SCOUT_SCHEMA = {
  type: 'object',
  properties: {
    lens: { type: 'string' },
    summary: { type: 'string', description: 'what this lens found that the architect must know, 3-10 sentences' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          path: { type: 'string' },
          lines: { type: 'string', description: 'line range, e.g. "120-188"' },
          symbols: { type: 'array', items: { type: 'string' } },
          note: { type: 'string' },
        },
        required: ['path', 'lines', 'symbols', 'note'],
      },
    },
    constraints: { type: 'array', items: { type: 'string' }, description: 'rules the change must respect, with their source (AGENTS.md section or file:line)' },
    openQuestions: { type: 'array', items: { type: 'string' } },
  },
  required: ['lens', 'summary', 'findings', 'constraints', 'openQuestions'],
}

const STEP_SCHEMA = {
  type: 'object',
  properties: {
    id: { type: 'string', description: 'short slug, e.g. "3-extract-client-session"' },
    title: { type: 'string' },
    implementer: { type: 'string', enum: [SONNET, OPUS] },
    implementerWhy: { type: 'string', description: 'one line: why this step needs Opus, or why Sonnet can do it from the description alone' },
    files: { type: 'array', items: { type: 'string' }, description: 'files to create or edit' },
    description: { type: 'string', description: 'exact instructions: the members to move (names and current line ranges), the destination file, class and namespace, and what the move forces (visibility, owner qualification, accessors, usings, call sites)' },
    acceptance: { type: 'string', description: 'how the implementer proves the step is done' },
  },
  required: ['id', 'title', 'implementer', 'implementerWhy', 'files', 'description', 'acceptance'],
}

const PLAN_SCHEMA = {
  type: 'object',
  properties: {
    overview: { type: 'string', description: 'the design: the responsibilities and their new homes, the wiring style, what stays and why' },
    assumptions: { type: 'array', items: { type: 'string' } },
    steps: { type: 'array', items: STEP_SCHEMA, description: 'ordered; each leaves the build green with zero warnings' },
    risks: { type: 'array', items: { type: 'string' } },
    openQuestions: {
      type: 'array',
      description: 'only questions whose answer would change the plan; empty if none',
      items: {
        type: 'object',
        properties: {
          question: { type: 'string', description: 'concrete, answerable by reading the code' },
          whyItMatters: { type: 'string' },
        },
        required: ['question', 'whyItMatters'],
      },
    },
  },
  required: ['overview', 'assumptions', 'steps', 'risks', 'openQuestions'],
}

const ANSWER_SCHEMA = {
  type: 'object',
  properties: {
    question: { type: 'string' },
    answer: { type: 'string' },
    evidence: { type: 'array', items: { type: 'string' }, description: 'file:line references' },
    confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
  },
  required: ['question', 'answer', 'evidence', 'confidence'],
}

const FACTCHECK_SCHEMA = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    issues: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          stepId: { type: 'string', description: 'the step, or "plan" for cross-cutting' },
          kind: { type: 'string', enum: ['missing-symbol', 'wrong-location', 'uncovered-dependency', 'missed-caller', 'name-collision', 'folder-limit', 'order-or-thread-risk', 'wrong-implementer', 'other'] },
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          detail: { type: 'string' },
          evidence: { type: 'string', description: 'file:line' },
        },
        required: ['stepId', 'kind', 'severity', 'detail', 'evidence'],
      },
    },
  },
  required: ['summary', 'issues'],
}

const IMPL_SCHEMA = {
  type: 'object',
  properties: {
    stepId: { type: 'string' },
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    summary: { type: 'string', description: 'what changed, in enough detail that the next implementer needs no re-reading' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    newFiles: { type: 'array', items: { type: 'string' } },
    buildPassed: { type: 'boolean', description: 'what the last build actually printed' },
    warnings: { type: 'number', description: 'warning count of the last build' },
    deviations: { type: 'array', items: { type: 'string' }, description: 'every departure from the step and why' },
    blockedOn: { type: 'string', description: 'the decision or problem that stopped you; empty when done' },
    notesForNextSteps: { type: 'array', items: { type: 'string' } },
  },
  required: ['stepId', 'status', 'summary', 'filesChanged', 'newFiles', 'buildPassed', 'warnings', 'deviations', 'blockedOn', 'notesForNextSteps'],
}

const VERIFY_SCHEMA = {
  type: 'object',
  properties: {
    buildPassed: { type: 'boolean' },
    warnings: { type: 'number' },
    buildErrors: { type: 'array', items: { type: 'string' } },
    testsRan: { type: 'boolean' },
    total: { type: 'number' },
    testsPassed: { type: 'boolean', description: 'true only when every failure passed when re-run alone' },
    failures: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          test: { type: 'string' },
          rerunPassed: { type: 'boolean' },
          knownFlaky: { type: 'boolean' },
          message: { type: 'string' },
        },
        required: ['test', 'rerunPassed', 'knownFlaky', 'message'],
      },
    },
    summary: { type: 'string', description: 'e.g. "0 warnings; 2441 passed, 0 failed"' },
  },
  required: ['buildPassed', 'warnings', 'buildErrors', 'testsRan', 'total', 'testsPassed', 'failures', 'summary'],
}

const AUDIT_SCHEMA = {
  type: 'object',
  properties: {
    residueHeadline: { type: 'string', description: 'the residue script\'s first three lines, verbatim' },
    residue: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          category: { type: 'string', enum: ['changed', 'requalified', 'added', 'removed', 'reordered', 'qualification', 'eol'] },
          file: { type: 'string' },
          line: { type: 'number' },
          text: { type: 'string', description: 'the line (for a pair: "old -> new")' },
          assessment: { type: 'string', description: 'one line: what this is' },
          suspicious: { type: 'boolean', description: 'could change behavior' },
        },
        required: ['category', 'file', 'line', 'text', 'assessment', 'suspicious'],
      },
    },
    wiring: { type: 'array', items: { type: 'string' }, description: 'per file: the plain new-owner wiring summarized instead of listed' },
    omitted: { type: 'string', description: 'what was not listed item by item and why' },
    newFiles: {
      type: 'array',
      items: { type: 'object', properties: { path: { type: 'string' }, lines: { type: 'number' } }, required: ['path', 'lines'] },
    },
    targetLines: {
      type: 'array',
      items: { type: 'object', properties: { path: { type: 'string' }, lines: { type: 'number' } }, required: ['path', 'lines'] },
    },
    folderCounts: {
      type: 'array',
      items: { type: 'object', properties: { folder: { type: 'string' }, files: { type: 'number' } }, required: ['folder', 'files'] },
    },
    leftovers: { type: 'array', items: { type: 'string' }, description: 'unused fields, usings or helpers left behind, forwarders the plan did not ask for' },
    staleRefs: { type: 'array', items: { type: 'string' }, description: '"file:line: text" of docs or crefs still naming a member at its old owner' },
    diffStat: { type: 'string' },
  },
  required: ['residueHeadline', 'residue', 'wiring', 'omitted', 'newFiles', 'targetLines', 'folderCounts', 'leftovers', 'staleRefs', 'diffStat'],
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
          gap: { type: 'string' },
          fix: { type: 'string', description: 'a concrete instruction' },
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          implementer: { type: 'string', enum: [SONNET, OPUS] },
        },
        required: ['stepId', 'gap', 'fix', 'severity', 'implementer'],
      },
    },
    remaining: { type: 'string', description: 'what of the task is still undone (targets missed, extractions left), empty when complete' },
    followUps: { type: 'array', items: { type: 'string' }, description: 'for plans/CLEANUP.md: duplication or dead code noticed, bugs seen but not fixed, the next extraction' },
    commitSubject: { type: 'string', description: 'one short imperative line in the style of the precedent commits' },
    doneRow: { type: 'string', description: 'the "What" cell of the Done row, in the style of the existing rows' },
  },
  required: ['accepted', 'summary', 'gaps', 'remaining', 'followUps', 'commitSubject', 'doneRow'],
}

const COMMIT_SCHEMA = {
  type: 'object',
  properties: {
    committed: { type: 'boolean' },
    hash: { type: 'string', description: 'git rev-parse --short HEAD after the commit' },
    subject: { type: 'string' },
    staged: { type: 'array', items: { type: 'string' } },
    leftUnstaged: { type: 'array', items: { type: 'string' }, description: 'dirty or untracked paths deliberately not committed' },
    notes: { type: 'string' },
  },
  required: ['committed', 'hash', 'subject', 'staged', 'leftUnstaged', 'notes'],
}

const RECORD_SCHEMA = {
  type: 'object',
  properties: {
    updated: { type: 'boolean' },
    summary: { type: 'string', description: 'what changed in plans/CLEANUP.md' },
  },
  required: ['updated', 'summary'],
}

// ---------------------------------------------------------------------------
// Prompt pieces
// ---------------------------------------------------------------------------
const LENS_GUIDE = `Recommended scout lenses by kind (adapt them to the task; brief each one concretely):
- god-class-split:
  (a) JOBS MAP: every member of the target grouped into candidate responsibilities, with line ranges, the fields each group reads and
      writes, and a coupling table of which groups call which;
  (b) SURFACE: every reference to the target's members from other files (the game, OpenGLTest.ItemDebugger, OpenGLTest.Server, the tests)
      and every doc or cref mention (AGENTS.md, .claude/skills, plans/PERF.md, TODO.md, XML crefs);
  (c) INVARIANTS: the AGENTS.md rules and the code-level order, threading, GL-thread and allocation constraints the moved code carries,
      and which tests exercise it;
  (d) PRECEDENT AND PLACEMENT: how the done splits wired their components (git show --stat of the precedent commits and a sample of their
      owner/accessor code), destination folder file counts, and a collision check of candidate folder, namespace and class names
      against every type and namespace segment.
- dead-code: one scout per project or src area: run the doc+style build (append -p:GenerateDocumentationFile=true
  -p:EnforceCodeStyleInBuild=true to the build command), collect IDE0005 and CS1574 per file, and grep private members with zero references.
- duplication: one scout per known duplicate (both sides, every caller, every difference between them) plus one hunting for more.
- nesting: one scout per folder slice, listing every method that nests past ~3 levels with its line range and what makes it nest.
- follow-up: one scout per item, plus one checking every doc that cites it.`

function scoutDigest(reports) {
  return reports.map(r => `### Lens: ${r.lens}
${r.summary}
Findings:
${r.findings.map(f => `- ${f.path}:${f.lines} ${f.symbols.length ? `[${f.symbols.join(', ')}] ` : ''}- ${f.note}`).join('\n') || '- (none)'}
Constraints:
${r.constraints.map(c => `- ${c}`).join('\n') || '- (none)'}
Open questions:
${r.openQuestions.map(q => `- ${q}`).join('\n') || '- (none)'}`).join('\n\n')
}

function planText(plan) {
  return `## Plan overview
${plan.overview}

## Assumptions
${plan.assumptions.map(a => `- ${a}`).join('\n') || '- (none)'}

## Risks
${plan.risks.map(r => `- ${r}`).join('\n') || '- (none)'}

## Steps
${plan.steps.map((s, i) => `${i + 1}. [${s.id}] ${s.title}  (implementer: ${s.implementer})
   files: ${s.files.join(', ')}
   ${s.description}
   acceptance: ${s.acceptance}`).join('\n\n')}`
}

function reportList(reports) {
  return reports.length
    ? reports.map(r => `- [${r.stepId}] ${r.status} (by ${r.by}): ${r.summary}${r.deviations.length ? `\n  deviations: ${r.deviations.join('; ')}` : ''}${r.notesForNextSteps.length ? `\n  notes: ${r.notesForNextSteps.join('; ')}` : ''}`).join('\n')
    : '- (nothing yet: you are first)'
}

const IMPLEMENTER_RULES = `Execute the step exactly as the Opus architect wrote it:
- Move code verbatim: same statements, same order, same comments. Change only what the move forces: class name, visibility (internal where
  possible), owner qualification or a live expression-bodied accessor over the owner's state, usings, call sites.
- For any block longer than a few lines, cut and paste with a byte-mode Python script that copies exact line ranges (crlf-safe-edit skill),
  never retype it. Re-read the region first: earlier steps may have shifted line numbers.
- Do not start other steps, rename members, or tidy unrelated code.
- If the plan is wrong in a way that blocks you, make the smallest local deviation and record it. If it needs a design decision, stop:
  return status "blocked" and name the decision in blockedOn.
- Finish with the Release build; fix every error and every warning you introduced. Report buildPassed and warnings as the build printed them.
  Do not run the full test suite (the verifier does).`

// ---------------------------------------------------------------------------
// One task: survey -> direct -> scout -> plan -> implement -> verify -> record
// ---------------------------------------------------------------------------
const outcomes = []
let stopped = ''

for (let t = 0; t < taskLimit; t++) {
  const tag = taskLimit > 1 ? `t${t + 1}:` : ''

  // ---- Survey (Sonnet) ----------------------------------------------------
  phase('Survey')
  const survey = await agent(`You are the campaign surveyor for the CSVoxelEngine cleanup. Read-only: do NOT modify anything.
1. Read plans/CLEANUP.md (repo root) in full.
2. Run git status --porcelain and git log --oneline -1. Report every dirty or untracked path except ${IGNORED}.
3. For every god-class entry in "Open", measure the file's current line count (bash wc -l, or PowerShell (Get-Content <path>).Count;
   the plan's "src/" means OpenGLTest/src/).
4. Copy the "In progress" task text VERBATIM and in full when there is one.
Report what the plan says; do not judge or pick a task.`,
    { label: `${tag}survey`, phase: 'Survey', model: SONNET, effort: 'low', schema: SURVEY_SCHEMA })
  if (!survey) { stopped = 'the surveyor returned nothing'; break }
  if (survey.dirtyPaths.length) {
    stopped = `the working tree holds uncommitted changes (${survey.dirtyPaths.join(', ')}); finish and commit them, or discard them, first`
    log(`Stopping: ${stopped}`)
    break
  }
  const ctx = context(survey)

  // ---- Direct (Opus) ------------------------------------------------------
  phase('Direct')
  const userTask = t === 0 ? requestedTask : ''
  const brief = await agent(`${ctx}

You direct the CSVoxelEngine cleanup campaign. Sonnet agents do the reading and most of the implementing; you make the calls.
The surveyor's report on plans/CLEANUP.md and the tree is below; do not re-read the plan. Read a file only when a decision hinges on it.

${userTask
    ? `The user asked for this task. Use it; when it is terse, expand it into a complete task text in the style of the plan's god-class task:\n${userTask}`
    : `Pick the next task: the in-progress task when there is one (use its text VERBATIM), otherwise the first entry of "Open" in plan
order that is worth a run. Skip files the plan says have a stated reason (unless it names a concrete next extraction for them), vendored files,
borderline and lower-priority entries while better ones remain, and everything in the excluded list: bugs are separate normal tasks, never
cleanup runs. Set done=true only when nothing worth a run is left.`}
${outcomes.length ? `\nAlready committed in this run (the plan already records them): ${outcomes.map(o => o.title).join('; ')}` : ''}

Write the task text the way the plan's god-class task is written: the goal and the rule it serves; how to approach it (map the jobs first,
move verbatim, a composition root that builds and owns the components, placement and namespace rules); measurable targets; the hard
constraints that apply to THIS code (update and render order, threading and GL-thread rules, edit-path hooks, per-frame allocation, wire
formats, the projects and tests that must keep compiling); and the docs to update (AGENTS.md Layout and every reference to moved members,
the skills, plans/PERF.md, TODO.md, crefs; a file that stays oversized gets its reason recorded in Code organization).

Then brief 3-5 Sonnet scouts, one lens each.
${LENS_GUIDE}

## Survey
${JSON.stringify(survey, null, 2)}`,
    { label: `${tag}direct`, phase: 'Direct', model: OPUS, schema: BRIEF_SCHEMA })
  if (!brief) { stopped = 'the director returned nothing'; break }
  if (brief.done) {
    stopped = `the director found no cleanup task left worth a run: ${brief.rationale}`
    log(stopped)
    break
  }
  log(`${tag}Task: ${brief.title} (${brief.kind}) - ${brief.rationale}`)
  const task = taskBlock(brief)

  // ---- Scout (Sonnet) -----------------------------------------------------
  // A barrier on purpose: the architect needs every report at once.
  phase('Scout')
  const lenses = brief.scouts.length ? brief.scouts : [
    { key: 'map', brief: 'Map every file, type and member the task touches, with line ranges, and who calls each.' },
    { key: 'surface', brief: 'Find every reference to those members elsewhere (all projects, tests, docs, crefs) and the invariants they carry.' },
  ]
  const reports = (await parallel(lenses.map(l => () =>
    agent(`${ctx}

${task}

You are a read-only scout for the Opus architect. Do NOT modify files.
Your lens (${l.key}): ${l.brief}
The architect plans from your report without re-reading the code, so be complete within your lens, precise (paths, symbols, line ranges)
and terse. Report what you verified by reading, not what you infer. Put what you could not settle into openQuestions.`,
      { label: `${tag}scout:${l.key}`, phase: 'Scout', model: SONNET, schema: SCOUT_SCHEMA })
  ))).filter(Boolean)
  if (reports.length < lenses.length) log(`${tag}Warning: ${lenses.length - reports.length} scout(s) returned nothing`)
  const digest = scoutDigest(reports)

  // ---- Plan (Opus, with Sonnet answering and fact-checking) ---------------
  const ARCHITECT = `${ctx}

${task}

You are the architect. You plan; you do not edit files. The Sonnet scouts' reports are your eyes: read code yourself only to settle a
point a decision hinges on, and put anything else you need into openQuestions for a Sonnet scout to answer.

Write a plan an implementer executes step by step with no further design:
- Ordered steps, roughly one responsibility or one to four files each; every step leaves the build green with zero warnings.
- Each step names the exact members to move (names and current line ranges), the destination file, class and namespace, and what the move
  forces (visibility, owner qualification, accessors, usings, call sites), and nothing else.
- Assign each step an implementer. "sonnet" for everything you can specify completely: verbatim moves with an explicit member list,
  call-site and using updates, doc reference updates, mechanical renames. "opus" only where the step needs judgment you cannot write
  down in advance: untangling state shared between responsibilities, designing the owner wiring of a tangled group, a move that cuts
  across several groups at once, order- or thread-sensitive rewiring that cannot be specified line by line. Prefer specifying a step
  fully over reserving it; most steps should be sonnet.
- The docs (AGENTS.md Layout and member references, the skills, plans/PERF.md, TODO.md, crefs) are the final step(s). Never plans/CLEANUP.md:
  the recorder updates it after the commit.
- No new tests, no behavior change.
- openQuestions only for answers that would change the plan.`

  phase('Plan')
  let plan = await agent(`${ARCHITECT}

## Scout reports
${digest}`,
    { label: `${tag}plan:draft`, phase: 'Plan', model: OPUS, effort: 'high', schema: PLAN_SCHEMA })
  if (!plan || !plan.steps.length) { stopped = `the architect returned no plan for "${brief.title}"`; break }
  log(`${tag}Draft plan: ${plan.steps.length} step(s) (${plan.steps.filter(s => s.implementer === OPUS).length} for Opus), ${plan.openQuestions.length} open question(s)`)

  const [checked, ...answers] = await parallel([
    () => agent(`${ctx}

${task}

${planText(plan)}

You are fact-checking the architect's plan against the code before anyone edits. Read-only. For every step verify:
1. every named file, type and member exists where the step says (line ranges roughly right);
2. every member the step moves has its dependencies covered: the fields, helpers, nested types, constants and usings it needs are moved with
   it, reachable from the new owner the way the step says, or deliberately left behind; name each uncovered one;
3. no caller of a moved member is missing from the call-site updates (grep every project, the tests, the ItemDebugger and the Server);
4. no new class, folder or namespace name collides with an existing type or namespace segment;
5. every destination folder stays at or under ~22 files;
6. no step changes the order of statements or calls on an update or render path, or moves a GL call off the render thread;
7. a "sonnet" step is fully specified (flag one that actually needs a design decision).
Report only real problems, each with evidence (file:line). An empty issue list is a good answer.`,
      { label: `${tag}plan:fact-check`, phase: 'Plan', model: SONNET, schema: FACTCHECK_SCHEMA }),
    ...plan.openQuestions.map((q, i) => () => agent(`${ctx}

${task}

You are a read-only scout answering one question for the Opus architect. Do NOT modify files. Answer from the code and cite file:line;
when the code does not settle it, say so and give the most likely answer with your reasoning.

QUESTION: ${q.question}
WHY IT MATTERS: ${q.whyItMatters}`,
      { label: `${tag}plan:q${i + 1}`, phase: 'Plan', model: SONNET, schema: ANSWER_SCHEMA })),
  ])
  const issues = checked ? checked.issues : []
  const realAnswers = answers.filter(Boolean)
  if (issues.length || realAnswers.length) {
    log(`${tag}Fact-check: ${issues.length} issue(s); ${realAnswers.length} answer(s); Opus revises`)
    plan = await agent(`${ARCHITECT}

## Scout reports
${digest}

## Your draft plan
${JSON.stringify(plan, null, 2)}

## Answers to your open questions
${realAnswers.map(a => `Q: ${a.question}\nA (${a.confidence}): ${a.answer}\nEvidence: ${a.evidence.join('; ')}`).join('\n\n') || '(none)'}

## Sonnet's fact-check of the draft
${issues.map(i => `[${i.severity}] ${i.stepId} (${i.kind}): ${i.detail}  evidence: ${i.evidence}`).join('\n') || '(no issues)'}

Produce the final plan. Fix every blocker and major issue; when you doubt a claim, check its evidence before acting on it, and record a
rejected one in assumptions. Turn each answer into a step change or an assumption. Leave openQuestions empty.`,
      { label: `${tag}plan:final`, phase: 'Plan', model: OPUS, effort: 'high', schema: PLAN_SCHEMA }) || plan
  } else {
    log(`${tag}Fact-check: the plan holds`)
  }
  const pText = planText(plan)
  log(`${tag}Final plan: ${plan.steps.map(s => `${s.title}${s.implementer === OPUS ? ' [opus]' : ''}`).join(' -> ')}`)

  if (dryRun) {
    outcomes.push({ title: brief.title, kind: brief.kind, outcome: 'dry-run', brief, plan, factCheck: checked })
    stopped = 'dryRun: stopped after the plan'
    break
  }

  // ---- Implement (Sonnet by default, Opus where assigned or on takeover) --
  // Strictly sequential: every step shares the one working tree and obj/ directory.
  phase('Implement')
  const implemented = []
  const stepOk = r => r && r.status === 'done' && r.buildPassed && r.warnings === 0
  for (let i = 0; i < plan.steps.length; i++) {
    const step = plan.steps[i]
    const who = forcedImplementer || step.implementer
    const stepPrompt = `${ctx}

${task}

${pText}

## Completed so far
${reportList(implemented)}

## YOUR STEP: ${i + 1}. [${step.id}] ${step.title}
Files: ${step.files.join(', ')}

${step.description}

Acceptance: ${step.acceptance}`
    log(`${tag}Step ${i + 1}/${plan.steps.length} (${who}): ${step.title}`)
    let result = await agent(`${stepPrompt}

You are the implementer of this one step. ${IMPLEMENTER_RULES}`,
      { label: `${tag}implement:${step.id}`, phase: 'Implement', model: who === OPUS ? OPUS : SONNET, schema: IMPL_SCHEMA })
    let by = who

    if (!stepOk(result) && who === SONNET && forcedImplementer !== SONNET) {
      log(`${tag}${step.id}: Sonnet ${result ? `${result.status}, build ${result.buildPassed ? 'green' : 'red'}, ${result.warnings} warning(s)` : 'returned nothing'}; Opus takes the wheel`)
      const takeover = await agent(`${stepPrompt}

## TAKEOVER
Sonnet attempted this step and did not finish it. Its report:
${result ? JSON.stringify(result, null, 2) : '(it returned nothing)'}

You are now the implementer. The working tree holds whatever Sonnet changed: start with git status and git diff on this step's files.
Finish or correct the step. When Sonnet's edit to a file is beyond repair, restore that file with git checkout -- <file> ONLY if no earlier
step touched it (see "Completed so far"), then redo the step there. ${IMPLEMENTER_RULES}`,
        { label: `${tag}takeover:${step.id}`, phase: 'Implement', model: OPUS, schema: IMPL_SCHEMA })
      if (takeover) { result = takeover; by = OPUS }
    }

    if (!result) {
      implemented.push({ stepId: step.id, status: 'blocked', by, summary: 'no result', filesChanged: [], newFiles: [], buildPassed: false, warnings: 0, deviations: [], blockedOn: 'agent returned nothing', notesForNextSteps: [] })
      log(`${tag}${step.id} returned nothing; implementation stops here`)
      break
    }
    implemented.push({ ...result, by })
    if (result.status === 'blocked') {
      log(`${tag}${step.id} is blocked: ${result.blockedOn}`)
      break
    }
  }

  // ---- Verify (Sonnet build/test + audit, Opus review), with fix rounds ---
  phase('Verify')
  let verification = null
  let audit = null
  let review = null
  let accepted = false
  for (let round = 0; ; round++) {
    const r = round ? `:r${round}` : ''
    const newFiles = [...new Set(implemented.flatMap(x => x.newFiles))]
    ;[verification, audit] = await parallel([
      () => agent(`${ctx}

You are the verifier. Do NOT edit any file.
1. Build: DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release --no-incremental. Report the warning count (it must be 0)
   and every error.
2. If it built: DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build.
3. Re-run every failing test's class alone (--no-build --filter "FullyQualifiedName~<Class>"). testsPassed is true only when every
   failure passed on its re-run. Mark the known flaky ones.
Report the totals verbatim.`,
        { label: `${tag}verify:build-test${r}`, phase: 'Verify', model: SONNET, schema: VERIFY_SCHEMA }),
      () => agent(`${ctx}

${task}

${pText}

## Implementer reports
${reportList(implemented)}
New files reported: ${newFiles.join(', ') || '(none)'}

You are the auditor. Read-only: edit nothing. You produce the evidence the Opus reviewer decides from, so it does not read the whole diff.
1. Run python -I .claude/skills/code-cleanup/move_residue.py --max 400 (add --rename Old=New when the plan renames a class whose name
   appears inside the moved code). Read its whole output. It pairs every removed line with an added one and prints only what does not pair.
2. List EVERY changed, requalified, removed and reordered item, and every added line that is not plain new-owner wiring (class header,
   owner field, constructor, live accessor, doc summary); summarize the wiring per file in "wiring" instead. Give each listed item a
   one-line assessment and mark suspicious anything that could change behavior: a changed condition, value, call, argument or statement
   order, or a dropped statement. A reordered run that is a whole member moved as a unit is fine; a reordered statement inside a method
   is suspicious. Skim the qualification-only section and list only suspicious entries.
3. List the endings section's problems (MIXED, BOM changed, final newline changed, new LF file beside CRLF siblings) as "eol" items.
4. git diff --stat; the line counts of the task's targets (${brief.targets.join(', ')}) and of every new file; the file count of every
   folder that gained a file.
5. Leftovers: fields, usings or helpers left unused in the source, and forwarders the plan did not ask for.
6. Stale references: grep AGENTS.md, .claude/skills, plans/PERF.md, TODO.md and the XML crefs for moved members named at their old owner.`,
        { label: `${tag}verify:audit${r}`, phase: 'Verify', model: SONNET, schema: AUDIT_SCHEMA }),
    ])

    const green = !!(verification && verification.buildPassed && verification.warnings === 0 && verification.testsPassed)
    review = await agent(`${ctx}

${task}

${pText}

## Implementer reports
${reportList(implemented)}

## Verifier report (Sonnet)
${verification ? JSON.stringify(verification, null, 2) : '(no report: treat the build and tests as unverified)'}

## Audit report (Sonnet)
${audit ? JSON.stringify(audit, null, 2) : '(no report: inspect the diff yourself)'}

You are the architect deciding whether this task is done. The audit's residue is your view of the diff: everything outside it moved verbatim.
Read specific hunks (git diff -- <file>) only to settle a suspicious or unclear item; do not read the whole diff. Do NOT modify files.
Accept only when the build is green with zero warnings, every test passes (flaky re-runs allowed), no residue item changes behavior, every
plan step is done, the targets are met or the shortfall is honestly stated in "remaining", the docs are updated and nothing is left behind.
Minor gaps alone do not block acceptance: put them in followUps. Do not ask for new tests or scenario runs.
Give every gap a concrete fix and an implementer: sonnet for anything you can spell out, opus for a fix that needs judgment.`,
      { label: `${tag}review${r}`, phase: 'Verify', model: OPUS, effort: 'high', schema: REVIEW_SCHEMA })

    const blocking = review ? review.gaps.filter(g => g.severity !== 'minor') : []
    if (green && review && review.accepted && !blocking.length) {
      accepted = true
      log(`${tag}Accepted after ${round} fix round(s): ${review.summary}`)
      break
    }
    if (round >= maxFixRounds) {
      log(`${tag}Not accepted after ${round} fix round(s): ${review ? review.summary : 'no review'}`)
      break
    }

    // Fix round: Sonnet takes the gaps it can, Opus the ones it reserved and any Sonnet could not close.
    const gaps = blocking.length ? blocking : (review ? review.gaps : [])
    const redBuild = green ? '' : `\n## The build or tests are red\n${verification ? JSON.stringify({ buildErrors: verification.buildErrors, warnings: verification.warnings, failures: verification.failures.filter(f => !f.rerunPassed) }, null, 2) : 'no verifier report'}`
    const owner = g => forcedImplementer || g.implementer
    let sonnetGaps = gaps.filter(g => owner(g) !== OPUS)
    let opusGaps = gaps.filter(g => owner(g) === OPUS)
    if (!gaps.length) (forcedImplementer === OPUS ? opusGaps : sonnetGaps).push({ stepId: 'plan', gap: 'build or tests red', fix: 'make the build and the existing tests green within the plan', severity: 'blocker', implementer: SONNET })
    log(`${tag}Fix round ${round + 1}: ${sonnetGaps.length} gap(s) for Sonnet, ${opusGaps.length} for Opus`)
    const fixPrompt = list => `${ctx}

${task}

${pText}

## Implementer reports
${reportList(implemented)}
${redBuild}

## Gaps the Opus reviewer found: fix all of these
${list.map(g => `[${g.severity}] ${g.stepId}: ${g.gap}\n  fix: ${g.fix}`).join('\n')}

You are the implementer for this fix round. Report stepId "fix-r${round + 1}". ${IMPLEMENTER_RULES}`
    if (sonnetGaps.length) {
      const fix = await agent(fixPrompt(sonnetGaps), { label: `${tag}fix-r${round + 1}:sonnet`, phase: 'Verify', model: SONNET, schema: IMPL_SCHEMA })
      if (fix) implemented.push({ ...fix, by: SONNET })
      if (!stepOk(fix) && forcedImplementer !== SONNET) opusGaps = opusGaps.concat(sonnetGaps)
    }
    if (opusGaps.length) {
      const fix = await agent(fixPrompt(opusGaps), { label: `${tag}fix-r${round + 1}:opus`, phase: 'Verify', model: OPUS, schema: IMPL_SCHEMA })
      if (fix) implemented.push({ ...fix, by: OPUS })
    }
  }

  const outcome = {
    title: brief.title,
    kind: brief.kind,
    outcome: accepted ? (commit ? 'accepted' : 'accepted-uncommitted') : 'not-accepted',
    steps: implemented.map(x => `${x.stepId}: ${x.status} by ${x.by}`),
    verification: verification ? verification.summary : 'no report',
    residue: audit ? audit.residueHeadline : 'no audit',
    review: review ? { summary: review.summary, remaining: review.remaining, gaps: review.gaps, followUps: review.followUps } : null,
    commitSubject: review ? review.commitSubject : brief.commitSubject,
  }
  outcomes.push(outcome)

  // ---- Record (Sonnet) ----------------------------------------------------
  phase('Record')
  if (!accepted) {
    if (commit) {
      await agent(`${ctx}

You are the campaign recorder. Edit plans/CLEANUP.md only (the Edit tool; keep its CRLF endings, structure and American English).
The cleanup task "${brief.title}" was NOT accepted. Its uncommitted partial work stays in the working tree for a person to inspect.
Rewrite the "In progress" section: the task title; the task text verbatim in a fenced block:
${brief.taskText}
then what this run did (${implemented.map(x => `${x.stepId} ${x.status}`).join(', ') || 'no steps'}), that the working tree holds the
uncommitted partial work, the verifier's result (${outcome.verification}), the reviewer's open gaps:
${review ? review.gaps.map(g => `- [${g.severity}] ${g.gap}`).join('\n') : '- (no review)'}
and how to continue (finish the gaps and re-run the workflow with this task, or git checkout the touched files to start over).
Update "Last updated" to today's date (date +%F). Change nothing else.`,
        { label: `${tag}record:not-accepted`, phase: 'Record', model: SONNET, effort: 'low', schema: RECORD_SCHEMA })
    }
    stopped = `"${brief.title}" was not accepted; its partial work is uncommitted in the tree`
    break
  }
  if (!commit) {
    stopped = `"${brief.title}" was accepted and left uncommitted (commit: false); proposed subject: ${outcome.commitSubject}`
    break
  }

  const newFiles = [...new Set(implemented.flatMap(x => x.newFiles).concat(audit ? audit.newFiles.map(f => f.path) : []))]
  const committed = await agent(`${ctx}

You are the committer. The Opus reviewer accepted the cleanup task "${brief.title}"; commit it. You may run git add and git commit.
1. git status --porcelain.
2. Stage the task's changes explicitly: git add -u -- . ':(exclude)plans/CLEANUP.md' ':(exclude).claude/workflows' ':(exclude).claude/worktrees'
   then git add -- <path> for every new file the task created: ${newFiles.join(', ') || '(none reported: check git status for new files the build needs)'}.
   Never stage ${IGNORED}, bin/ or obj/. Never git add -A or git add . (the tree holds junk that must never be committed).
3. git diff --cached --stat: check nothing unrelated slipped in; unstage anything that did.
4. Commit with exactly this subject and the trailer as its only body:
   git commit -m "${outcome.commitSubject.replace(/"/g, '\\"')}" -m "${TRAILER}"
   Never amend, never change git config, never skip hooks; if a hook fails, report it and stop.
5. Report git rev-parse --short HEAD, the staged paths, and the dirty or untracked paths you left out.`,
    { label: `${tag}commit`, phase: 'Record', model: SONNET, effort: 'low', schema: COMMIT_SCHEMA })
  if (!committed || !committed.committed) {
    outcome.outcome = 'accepted-commit-failed'
    stopped = `"${brief.title}" was accepted but the commit failed: ${committed ? committed.notes : 'no committer result'}`
    break
  }
  outcome.outcome = 'committed'
  outcome.commit = { hash: committed.hash, subject: committed.subject }
  log(`${tag}Committed ${committed.hash} ${committed.subject}`)

  await agent(`${ctx}

You are the campaign recorder. Edit plans/CLEANUP.md only (the Edit tool; keep its CRLF endings, structure and American English).
The cleanup task "${brief.title}" was committed as ${committed.hash} "${committed.subject}".
- Do not add a Done row: the plan keeps no Done table, finished tasks are deleted and the commit (${committed.hash}) is the record.
- If it was the "In progress" task, replace that section's body with "Nothing." (the next task is the first unchecked entry of "Open").
- In "Open", delete the task's entry, or when work remains, keep it with what remains: ${review.remaining || '(nothing remains)'}.
  Refresh the line counts of the files this task touched (measure them: ${brief.targets.join(', ')}${audit ? `, ${audit.newFiles.map(f => f.path).join(', ')}` : ''}).
- Fold these follow-ups into the matching lists (duplication, dead code, small follow-ups, bugs), skipping ones already there:
${review.followUps.map(f => `  - ${f}`).join('\n') || '  - (none)'}
- If the verifier's test total differs from the recorded baseline (${verification.summary}), update the baseline.
- Update "Last updated" to today's date (date +%F). Do not commit.`,
    { label: `${tag}record`, phase: 'Record', model: SONNET, effort: 'low', schema: RECORD_SCHEMA })
}

// ---------------------------------------------------------------------------
// One plan commit for the whole run, when every task in it was committed.
// ---------------------------------------------------------------------------
let planCommit = null
const committedTasks = outcomes.filter(o => o.outcome === 'committed')
if (commit && committedTasks.length && committedTasks.length === outcomes.length) {
  phase('Record')
  planCommit = await agent(`You are the committer. Commit plans/CLEANUP.md alone; you may run git add and git commit.
git add -- plans/CLEANUP.md, check git diff --cached --stat shows only that file, then
git commit -m "Update cleanup plan" -m "${TRAILER}"
Never amend, never change git config, never skip hooks. If plans/CLEANUP.md has no changes, commit nothing and say so.
Report git rev-parse --short HEAD.`,
    { label: 'commit:plan', phase: 'Record', model: SONNET, effort: 'low', schema: COMMIT_SCHEMA })
}

return {
  stopped: stopped || (outcomes.length >= taskLimit ? `ran ${outcomes.length} task(s) as asked` : ''),
  tasks: outcomes,
  planCommit: planCommit ? { committed: planCommit.committed, hash: planCommit.hash, notes: planCommit.notes } : null,
}
