// Recipe normalization and the reverse (made-from / used-in) indexes.
export function refKey(ref, hasBlock, hasItem) {
  if (ref.startsWith('block/') || ref.startsWith('item/')) return ref
  if (hasBlock(ref)) return 'block/' + ref
  if (hasItem(ref)) return 'item/' + ref
  return null
}

export function buildRecipeIndex(recipes) {
  const madeFrom = {}
  const usedIn = {}
  const push = (m, k, r) => { (m[k] ??= []).push(r) }
  for (const r of recipes) {
    for (const out of r.outputs) push(madeFrom, out, r)
    for (const inp of r.inputs) push(usedIn, inp, r)
  }
  return { madeFrom, usedIn }
}
