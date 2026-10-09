// Searches lever settings (a levers module, e.g. proposed/stage1-levers.mjs) for the ones that
// meet targets.json best, by simulation. Random starts, then one-lever-at-a-time improvement.
//
//   node sweep.mjs <levers.mjs> [--evals N] [--seed S] [--top K] [--fix lever=value,...] [--dump all.json]
//
// Fast settings while searching (EDA only, the quick rush openings); the best few are then
// re-checked with all factions and every rush opening. Output: the best settings and their rows.
import fs from 'fs';
import path from 'path';
import { pathToFileURL } from 'url';
import { loadVariant } from './variants.mjs';
import { checkVariant, score, fmtT } from './check.mjs';

const argv = process.argv.slice(2);
const flag = (name, d) => { const i = argv.indexOf(name); if (i < 0) return d; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const evals = +flag('--evals', 200), seed0 = +flag('--seed', 7), top = +flag('--top', 3), dumpTo = flag('--dump');
const all = []; // every ruleset evaluated, for --dump
const fixed = Object.fromEntries((flag('--fix', '') || '').split(',').filter(Boolean).map(kv => { const [k, v] = kv.split('='); return [k, +v]; }));
const mod = await import(pathToFileURL(path.resolve(argv[0])).href);
const { levers } = mod;
const valid = mod.valid || (() => true);
const base = loadVariant(mod.base);
const names = Object.keys(levers);

const WEIGHTS = { 'fastest T2 factory': 2, 'units facing the rush': 1, alloyIncome: 1.5, combatBuilt: 1, combatAlive: 1, factories: 0.5 };
let s = seed0;
const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647);

function variantFor(settings) {
  const units = JSON.parse(JSON.stringify(base.dump.units));
  for (const k of names) levers[k].apply(units, settings[k]);
  return { name: JSON.stringify(settings), dump: { ...base.dump, units }, rules: base.rules };
}
const cache = new Map();
function evaluate(settings, full = false) {
  if (!valid(settings)) return { settings, rows: [], score: Infinity };
  const key = JSON.stringify(settings) + full;
  if (cache.has(key)) return cache.get(key);
  const rows = checkVariant(variantFor(settings), undefined, full ? ['e', 'c', 'g'] : ['e'], { quickRush: !full });
  const r = { settings, rows, score: score(rows, WEIGHTS) };
  cache.set(key, r);
  if (!full) all.push({ settings, score: r.score, metrics: Object.fromEntries(rows.filter(x => x.style === 'avg' || x.style === 'rush').map(x => [`${x.cls} ${x.what}`, x.value === Infinity ? null : +x.value.toFixed(2)])) });
  return r;
}

function randomSettings() {
  for (;;) {
    const st = Object.fromEntries(names.map(k => [k, fixed[k] ?? levers[k].choices[Math.floor(rnd() * levers[k].choices.length)]]));
    if (valid(st)) return st;
  }
}
let n = 0;
const t0 = Date.now();
const results = [];
while (n < evals) {
  // A random start, then improve one lever at a time until nothing helps.
  let cur = evaluate(randomSettings()); n++;
  for (let improved = true; improved && n < evals;) {
    improved = false;
    for (const k of names) {
      if (k in fixed) continue;
      for (const c of levers[k].choices) {
        if (c === cur.settings[k] || n >= evals) continue;
        const cand = evaluate({ ...cur.settings, [k]: c }); n++;
        if (cand.score < cur.score - 1e-9) { cur = cand; improved = true; }
      }
    }
  }
  results.push(cur);
  console.error(`${n}/${evals} evaluations, ${((Date.now() - t0) / 1000).toFixed(0)} s, best so far ${Math.min(...results.map(r => r.score)).toFixed(4)}`);
}
if (dumpTo) fs.writeFileSync(dumpTo, JSON.stringify({ levers: Object.fromEntries(names.map(k => [k, levers[k].choices])), evaluations: all }));
const uniq = [...new Map(results.map(r => [JSON.stringify(r.settings), r])).values()].sort((a, b) => a.score - b.score).slice(0, top);
for (const r of uniq) {
  const full = evaluate(r.settings, true);
  console.log(`\nscore ${full.score.toFixed(4)} (EDA-only search score ${r.score.toFixed(4)})  ${JSON.stringify(r.settings)}`);
  for (const row of full.rows) console.log(`  ${row.ok ? 'ok  ' : 'MISS'} ${row.cls} ${row.style.padEnd(6)} ${row.what.padEnd(24)} ${(row.what === 'fastest T2 factory' ? `${fmtT(row.lo)}-${fmtT(row.hi)}` : `${row.lo ?? ''}-${row.hi ?? ''}`).padEnd(9)} ${row.got}`);
}
