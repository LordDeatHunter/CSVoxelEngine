#!/usr/bin/env python3
"""Move-diff residue check for behavior-preserving refactors in CSVoxelEngine.

A god-class split or a folder move should remove lines from one place and add the same lines
somewhere else. This script pairs every removed line with an added one and prints only what
does NOT pair up - the residue - so a reviewer reads tens of lines instead of a 4,000-line diff.

Lines are compared after normalizing whitespace and dropping leading visibility/static/readonly/
sealed modifiers (a move may change those). Every remaining difference is reported in one of:

  qualification  the line only gained or lost owner prefixes (`_world.`, `this.`, `Program.`)
  requalified    an owner prefix was swapped for another (`UIRenderer.X` -> `HudRenderer.X`):
                 usually a call site following its member, but check it names the same object
  changed        a removed and an added line that are similar but differ otherwise
  added          new lines with no removed counterpart (new wiring, new code)
  removed        removed lines with no added counterpart (deleted code)
  reordered      lines that were moved verbatim but no longer follow the source order
                 (a whole method moved as a block shows as one run starting at its signature;
                 a reordered statement inside a method is a behavior risk)

Usage (from the repo root):
  python -I .claude/skills/code-cleanup/move_residue.py                 # working tree vs HEAD,
                                                                        # untracked files included
  python -I .claude/skills/code-cleanup/move_residue.py --range A..B    # two commits
  options: --glob '*.cs' (repeatable), --rename Old=New (repeatable: a class rename applied to
           removed lines before comparing), --max N (items listed per section, default 200)

The report also checks the line-ending, BOM and final-newline state of every changed file.
Always exits 0: it is a report, not a gate.
"""
import argparse
import collections
import difflib
import os
import re
import subprocess
import sys

JUNK_PREFIXES = ('.claude/worktrees/', '.sisyphus/', '.run/', 'visual/')
MODIFIERS = re.compile(r'^(?:(?:public|private|protected|internal|static|readonly|sealed)\s+)+')
TRIVIAL = re.compile(r'^(?:[{}()\[\];,]*|///\s*</?\w+>|#(?:region|endregion).*|using\s+[\w.=\s]+;|global\s+using\s+.*;|namespace\s+[\w.]+;?)$')
TOKEN = re.compile(r'[A-Za-z_][A-Za-z0-9_]*|\d+(?:\.\d+)?[fFdDmMuUlL]*|\?\.|=>|==|!=|<=|>=|&&|\|\||\+\+|--|\S')
IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def git(*args, binary=False):
    out = subprocess.run(['git', '-c', 'core.quotepath=off', *args], capture_output=True, check=True).stdout
    return out if binary else out.decode('utf-8', errors='replace')


def is_junk(path):
    return path.startswith(JUNK_PREFIXES)


def parse_diff(text):
    """Yields (kind, path, line_no, text) for every removed ('-') and added ('+') line."""
    old_path = new_path = None
    old_no = new_no = 0
    for raw in text.split('\n'):
        line = raw[:-1] if raw.endswith('\r') else raw
        if line.startswith('--- '):
            old_path = None if line[4:] == '/dev/null' else line[6:]
            continue
        if line.startswith('+++ '):
            new_path = None if line[4:] == '/dev/null' else line[6:]
            continue
        if line.startswith('@@'):
            m = re.match(r'@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@', line)
            old_no, new_no = int(m.group(1)), int(m.group(2))
            continue
        if line.startswith('-') and old_path is not None:
            yield ('-', old_path, old_no, line[1:].rstrip('\r'))
            old_no += 1
        elif line.startswith('+') and new_path is not None:
            yield ('+', new_path, new_no, line[1:].rstrip('\r'))
            new_no += 1


def normalize(text, renames):
    s = re.sub(r'\s+', ' ', text.strip())
    s = MODIFIERS.sub('', s)
    for old, new in renames:
        s = re.sub(r'\b' + re.escape(old) + r'\b', new, s)
    return s


def qualifier_run(tokens):
    """`a.b.` or `.a.b`: owner prefixes, in whichever phase the token alignment cut them."""
    if not tokens or len(tokens) % 2:
        return False
    dot = lambda t: t in ('.', '?.')
    ident_first = all(IDENT.fullmatch(tokens[k]) and dot(tokens[k + 1]) for k in range(0, len(tokens), 2))
    dot_first = all(dot(tokens[k]) and IDENT.fullmatch(tokens[k + 1]) for k in range(0, len(tokens), 2))
    return ident_first or dot_first


def field_to_accessor(field_seg, accessor_seg):
    """`_countScale` read as `_ui.CountScale` (or `CountScale`): a private field that stayed with
    its owner and is now read through the owner's live accessor of the same name."""
    if len(field_seg) != 1 or not accessor_seg or not field_seg[0].startswith('_') or len(field_seg[0]) < 2:
        return False
    pascal = field_seg[0][1].upper() + field_seg[0][2:]
    return accessor_seg[-1] == pascal and (len(accessor_seg) == 1 or qualifier_run(accessor_seg[:-1]))


def classify(a, b):
    """'qualification' when the lines differ only by owner prefixes gained or lost, 'requalified'
    when an owner prefix was also swapped for another (`A.X` -> `B.X`), else 'changed'."""
    ta, tb = TOKEN.findall(a), TOKEN.findall(b)
    kind = 'qualification'
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, ta, tb, autojunk=False).get_opcodes():
        if tag == 'equal':
            continue
        if tag == 'insert' and qualifier_run(tb[j1:j2]):
            continue
        if tag == 'delete' and qualifier_run(ta[i1:i2]):
            continue
        if tag == 'replace' and (field_to_accessor(ta[i1:i2], tb[j1:j2]) or field_to_accessor(tb[j1:j2], ta[i1:i2])):
            continue
        swapped_owner = (tag == 'replace' and i2 - i1 == 1 and j2 - j1 == 1
                         and IDENT.fullmatch(ta[i1]) and IDENT.fullmatch(tb[j1])
                         and i2 < len(ta) and j2 < len(tb) and ta[i2] in ('.', '?.') and tb[j2] in ('.', '?.'))
        if swapped_owner:
            kind = 'requalified'
            continue
        return 'changed'
    return kind


def collect(args, renames):
    globs = args.glob or ['*.cs']
    if args.range:
        base, head = args.range.split('..', 1)
        diff = git('diff', '-U0', '--no-color', '--no-renames', base, head, '--', *globs)
    else:
        base, head = 'HEAD', None
        diff = git('diff', '-U0', '--no-color', '--no-renames', 'HEAD', '--', *globs)
    removed, added = [], []
    for kind, path, no, text in parse_diff(diff):
        if is_junk(path):
            continue
        (removed if kind == '-' else added).append((path, no, text))
    untracked = []
    if not args.range:
        for path in git('ls-files', '--others', '--exclude-standard', '--', *globs).splitlines():
            if not path or is_junk(path):
                continue
            untracked.append(path)
            with open(path, 'rb') as f:
                content = f.read().decode('utf-8-sig', errors='replace')
            for no, text in enumerate(content.replace('\r\n', '\n').split('\n'), 1):
                added.append((path, no, text))
    keep = lambda rows: [(p, n, t, normalize(t, renames)) for p, n, t in rows if not TRIVIAL.match(normalize(t, renames))]
    return base, head, keep(removed), keep(added), untracked


def match(removed, added):
    """Marks which removed/added occurrences pair up exactly (multiset match, first come first served)."""
    pool = collections.defaultdict(collections.deque)
    for i, row in enumerate(removed):
        pool[row[3]].append(i)
    removed_hit, added_hit = [False] * len(removed), [False] * len(added)
    for j, row in enumerate(added):
        if pool[row[3]]:
            removed_hit[pool[row[3]].popleft()] = True
            added_hit[j] = True
    return removed_hit, added_hit


def pair_residue(rem, add):
    """Pairs leftover lines by similarity; returns ({kind: [(removed, added)]}, added, removed)."""
    pairs = {'qualification': [], 'requalified': [], 'changed': []}
    if len(rem) * len(add) > 4_000_000:
        return pairs, add, rem
    used = set()
    lone_add = []
    for a in add:
        best, best_ratio = None, 0.6
        for k, r in enumerate(rem):
            if k in used:
                continue
            sm = difflib.SequenceMatcher(None, r[3], a[3], autojunk=False)
            if sm.real_quick_ratio() <= best_ratio or sm.quick_ratio() <= best_ratio:
                continue
            ratio = sm.ratio() + (0.05 if r[0] == a[0] else 0)  # same file wins a near tie
            if ratio > best_ratio:
                best, best_ratio = k, ratio
        if best is None:
            lone_add.append(a)
            continue
        used.add(best)
        pairs[classify(rem[best][3], a[3])].append((rem[best], a))
    lone_rem = [r for k, r in enumerate(rem) if k not in used]
    return pairs, lone_add, lone_rem


def reordered_runs(removed, added, added_hit):
    """Per destination file: runs of exactly-matched lines that do not follow the removed order."""
    source = [row[3] for row in removed]
    by_file = collections.OrderedDict()
    for j, row in enumerate(added):
        if added_hit[j]:
            by_file.setdefault(row[0], []).append(row)
    runs = []
    for path, rows in by_file.items():
        seq = [row[3] for row in rows]
        in_order = [False] * len(seq)
        for block in difflib.SequenceMatcher(None, source, seq, autojunk=False).get_matching_blocks():
            for k in range(block.b, block.b + block.size):
                in_order[k] = True
        run = []
        for k, row in enumerate(rows):
            if not in_order[k]:
                run.append(row)
            elif run:
                runs.append(run)
                run = []
        if run:
            runs.append(run)
    return runs


def eol_report(base, head, paths):
    lines = []
    for path in sorted(paths):
        if head is None:
            if not os.path.exists(path):
                continue
            with open(path, 'rb') as f:
                data = f.read()
        else:
            try:
                data = git('show', f'{head}:{path}', binary=True)
            except subprocess.CalledProcessError:
                continue
        crlf = data.count(b'\r\n')
        bare_lf = data.count(b'\n') - crlf
        style = 'CRLF' if crlf and not bare_lf else 'LF' if bare_lf and not crlf else 'mixed' if crlf else 'none'
        bom = data.startswith(b'\xef\xbb\xbf')
        final_nl = data.endswith(b'\n')
        notes = []
        if style == 'mixed':
            notes.append(f'MIXED endings ({crlf} CRLF, {bare_lf} LF)')
        try:
            old = git('show', f'{base}:{path}', binary=True)
            if old.startswith(b'\xef\xbb\xbf') != bom:
                notes.append('BOM changed')
            if old.endswith(b'\n') != final_nl:
                notes.append('final newline changed')
        except subprocess.CalledProcessError:
            folder = os.path.dirname(path) or '.'
            siblings = [os.path.join(folder, n) for n in os.listdir(folder)] if head is None and os.path.isdir(folder) else []
            crlf_siblings = 0
            for sib in siblings:
                if sib.endswith('.cs') and os.path.normpath(sib) != os.path.normpath(path):
                    with open(sib, 'rb') as f:
                        crlf_siblings += b'\r\n' in f.read(4096)
            if style == 'LF' and crlf_siblings:
                notes.append(f'new LF file beside {crlf_siblings} CRLF sibling(s)')
        lines.append(f'  {path}: {style}, bom={"yes" if bom else "no"}, final-newline={"yes" if final_nl else "no"}'
                     + (f'  <-- {"; ".join(notes)}' if notes else ''))
    return lines


def listing(title, items, fmt, limit):
    print(f'\n== {title}: {len(items)}')
    for item in items[:limit]:
        print(fmt(item))
    if len(items) > limit:
        print(f'  ... {len(items) - limit} more (raise --max to list them)')


def main():
    ap = argparse.ArgumentParser(description='Move-diff residue check')
    ap.add_argument('--range', help='A..B: compare two commits instead of the working tree vs HEAD')
    ap.add_argument('--glob', action='append', help="pathspec glob, repeatable (default '*.cs')")
    ap.add_argument('--rename', action='append', default=[], help='Old=New class rename applied to removed lines')
    ap.add_argument('--max', type=int, default=200, help='items listed per section')
    args = ap.parse_args()
    renames = [tuple(r.split('=', 1)) for r in args.rename]

    base, head, removed, added, untracked = collect(args, renames)
    removed_hit, added_hit = match(removed, added)
    rem_left = [r for r, hit in zip(removed, removed_hit) if not hit]
    add_left = [a for a, hit in zip(added, added_hit) if not hit]
    pairs, lone_add, lone_rem = pair_residue(rem_left, add_left)
    runs = reordered_runs(removed, added, added_hit)

    files = collections.Counter()
    for p, *_ in removed:
        files[(p, '-')] += 1
    for p, *_ in added:
        files[(p, '+')] += 1
    paths = sorted({p for p, _ in files})

    where = args.range or 'working tree vs HEAD (untracked files included)'
    print(f'Move-diff residue: {where}')
    print(f'Non-trivial lines: {len(removed)} removed, {len(added)} added; '
          f'{sum(removed_hit)} paired verbatim (after whitespace/modifier normalization)')
    print(f'Residue: {len(pairs["changed"])} changed, {len(pairs["requalified"])} requalified, '
          f'{len(pairs["qualification"])} qualification-only, {len(lone_add)} added, {len(lone_rem)} removed, '
          f'{sum(len(r) for r in runs)} reordered line(s) in {len(runs)} run(s)')
    if untracked:
        print(f'Untracked files counted as added: {", ".join(untracked)}')
    print('\n== Per file (non-trivial lines)')
    for p in paths:
        print(f'  {p}: -{files[(p, "-")]} +{files[(p, "+")]}')

    show = lambda row: f'{row[0]}:{row[1]}: {row[2].strip()}'
    pair = lambda pr: f'  - {show(pr[0])}\n  + {show(pr[1])}'
    listing('Changed (old -> new)', pairs['changed'], pair, args.max)
    listing('Added with no removed counterpart', lone_add, lambda a: f'  + {show(a)}', args.max)
    listing('Removed with no added counterpart', lone_rem, lambda r: f'  - {show(r)}', args.max)
    listing('Reordered runs (first line, length)', runs,
            lambda run: f'  ~ {show(run[0])}  [{len(run)} line(s)]', args.max)
    listing('Requalified (an owner prefix swapped: check it names the same object)', pairs['requalified'], pair, args.max)
    listing('Qualification-only (owner prefixes gained or lost)', pairs['qualification'], pair, min(args.max, 40))

    print('\n== Line endings, BOM, final newline of changed files')
    for line in eol_report(base, head, paths):
        print(line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
