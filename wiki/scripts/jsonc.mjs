// Comment- and trailing-comma-tolerant JSON parser (the game's content files are JSONC).
const BOM = String.fromCharCode(0xfeff)

export function stripJsonc(text) {
  let out = ''
  let inString = false
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    const n = text[i + 1]
    if (inString) {
      out += c
      if (c === '\\') { out += n ?? ''; i++ } else if (c === '"') inString = false
      continue
    }
    if (c === '"') { inString = true; out += c; continue }
    if (c === '/' && n === '/') { while (i < text.length && text[i] !== '\n') i++; out += '\n'; continue }
    if (c === '/' && n === '*') { i += 2; while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++; i++; continue }
    out += c
  }
  return out.replace(/,(\s*[}\]])/g, '$1')
}

export function parseJsonc(text, what = 'json') {
  try {
    return JSON.parse(stripJsonc(text.startsWith(BOM) ? text.slice(1) : text))
  } catch (e) {
    throw new Error(`${what}: ${e.message}`)
  }
}
