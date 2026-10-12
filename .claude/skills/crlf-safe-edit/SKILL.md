---
name: crlf-safe-edit
description: Edit CSVoxelEngine files without corrupting line endings. The working tree is CRLF (core.autocrlf=true) and sed -i or text-mode scripts silently rewrite files as LF. Use before any scripted or bulk edit of repo files, and to verify after one.
---

# CRLF-safe editing

Every source and doc file in this working tree is CRLF while git blobs are LF (`core.autocrlf=true`). A clean-looking `git diff` can hide a file that drifted to LF, and a later exact-string patch then stops matching.

## Rules
- Prefer the Edit tool for ordinary changes. It preserves endings.
- **Never `sed -i` a source file.** Never rewrite a file with Python text mode (`open(p).write(...)`).
- Scripted edits: read **bytes**, normalize to `\n` for matching, write back with `\r\n` if the original had `\r\n`. Template:

```python
import sys
p = sys.argv[1]
raw = open(p, 'rb').read()
crlf = b'\r\n' in raw
text = raw.replace(b'\r\n', b'\n').decode('utf-8')
assert text.count(OLD) == 1, "old string must match exactly once"
text = text.replace(OLD, NEW)
out = text.encode('utf-8')
if crlf: out = out.replace(b'\n', b'\r\n')
open(p, 'wb').write(out)
```
- Long scripts: write them with the Write tool and run `python -I script.py <args>`, not a long bash heredoc (quoting broke before). Keep scripts in the scratchpad.
- `FastNoiseLite.cs` is vendored: never reformat it.
- New files written by the Write tool or a heredoc are LF. If the neighboring files are CRLF, either convert the new file or leave it for git to normalize; match the directory.

## Verify after every scripted edit
```
f=path/to/file
echo "working CRs: $(grep -c $'\r' $f)  lines: $(wc -l < $f)  HEAD CRs: $(git show HEAD:$f | grep -c $'\r')"
git diff --stat -- $f
```
For a file that was CRLF, the working CR count must equal its line count (every line CRLF). HEAD blobs are LF, so the HEAD count is normally 0. `git diff --stat` must show only the lines you meant to change: a whole-file diff means the endings drifted. Restore with `git checkout -- <file>` if the file had no other changes, otherwise re-apply the edit in byte mode.
Whole-tree check: `git diff --stat | tail -1` should list only the files you touched.
