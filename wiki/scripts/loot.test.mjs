import test from 'node:test'
import assert from 'node:assert/strict'
import { lootRows, chanceOverRolls } from './loot.mjs'

const id = (n) => 'item/' + n
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-9, `${a} vs ${b}`)

test('weights split one roll between the entries, empty entries included', () => {
  const rows = lootRows([{ entries: [{ item: 'a', weight: 1 }, { item: 'b', weight: 3 }, { empty: true, weight: 4 }] }], id)
  near(rows.find((r) => r.item === 'item/a').chance, 1 / 8)
  near(rows.find((r) => r.item === 'item/b').chance, 3 / 8)
})

test('a pool chance scales every entry of it', () => {
  const [r] = lootRows([{ chance: 0.5, entries: [{ item: 'a' }] }], id)
  near(r.chance, 0.5)
})

test('more rolls make an item likelier, never certain unless it is the only entry', () => {
  assert.ok(chanceOverRolls(0.2, [3, 3]) > chanceOverRolls(0.2, [1, 1]))
  assert.ok(chanceOverRolls(0.2, [3, 3]) < 1)
  near(chanceOverRolls(1, [2, 4]), 1)
})

test('a count range with zero counts that as a miss and reports the dropping range', () => {
  const [r] = lootRows([{ entries: [{ item: 'a', count: { min: 0, max: 1 } }] }], id)
  near(r.chance, 0.5)
  assert.deepEqual([r.min, r.max], [1, 1])
})

test('the same item in two entries merges and unresolved items are skipped', () => {
  const rows = lootRows([{ entries: [{ item: 'a', weight: 1 }, { item: 'a', weight: 1 }, { item: 'zzz' }] }],
    (n) => (n === 'zzz' ? null : id(n)))
  assert.equal(rows.length, 1)
  near(rows[0].chance, 1 - (1 - 1 / 3) ** 2)
})
