// Loot table normalization: what a table can drop, how likely a kill or break yields it, and how many.

const intRange = (v, dflt) => {
  if (v == null) return [dflt, dflt]
  if (typeof v === 'number') return [v, v]
  if (typeof v.min === 'number' && typeof v.max === 'number') return [v.min, v.max]
  return null
}

/** Chance that at least one of `rolls` independent rolls (an int or a {min,max} range) picks an entry of probability p. */
export function chanceOverRolls(p, rolls) {
  const [lo, hi] = rolls
  let sum = 0
  for (let r = lo; r <= hi; r++) sum += 1 - Math.pow(1 - p, r)
  return sum / (hi - lo + 1)
}

/**
 * Flattens a loot table's pools into one row per item: the chance of getting at least one, and the count
 * range when it drops. A pool with a computed roll count (a provider) is reported with rolls = 1, since
 * the real count depends on the cell it rolls at. A zero count in a range is a miss, not a drop.
 * `resolve(name)` maps an entry's item name to its wiki key (or null to skip the entry).
 */
export function lootRows(pools, resolve) {
  const rows = new Map()
  for (const pool of pools ?? []) {
    const rolls = intRange(pool.rolls, 1) ?? [1, 1]
    const poolChance = pool.chance ?? 1
    const entries = pool.entries ?? []
    const total = entries.reduce((a, e) => a + (e.weight ?? 1), 0)
    if (total <= 0) continue
    for (const e of entries) {
      if (!e.item) continue
      const key = resolve(e.item)
      if (!key) continue
      const [cmin, cmax] = intRange(e.count, 1) ?? [1, 1]
      const nonZero = (cmax - Math.max(cmin, 1) + 1) / (cmax - cmin + 1)
      if (cmax < 1) continue
      const p = ((e.weight ?? 1) / total) * nonZero
      const chance = poolChance * chanceOverRolls(p, rolls)
      const prev = rows.get(key)
      if (prev) {
        prev.chance = 1 - (1 - prev.chance) * (1 - chance)
        prev.min = Math.min(prev.min, Math.max(cmin, 1))
        prev.max = Math.max(prev.max, cmax)
      } else rows.set(key, { item: key, chance, min: Math.max(cmin, 1), max: cmax })
    }
  }
  return [...rows.values()].sort((a, b) => b.chance - a.chance)
}
