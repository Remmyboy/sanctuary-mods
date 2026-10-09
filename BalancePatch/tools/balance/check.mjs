// Running rulesets against the player styles and targets: shared by balance.mjs and sweep.mjs.
// A "variant" here is { name, dump, rules } (a dump.lua output plus rules it can't show).
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { simulate, DEFAULT_POLICY } from './sim.mjs';
import { readMap } from './maps.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const readJson = f => JSON.parse(fs.readFileSync(f, 'utf8'));

export const playersPath = path.join(here, 'players.json');
export const players = fs.existsSync(playersPath) ? readJson(playersPath) : { styles: { default: { policy: {}, survival10: 0.6 } } };
export const targets = readJson(path.join(here, 'targets.json')).classes;
export const styleNames = s => (s && s !== true ? s.split(',') : Object.keys(players.styles));
export const fmtT = s => (s == null ? '-' : `${Math.floor(s / 60)}:${String(Math.round(s % 60)).padStart(2, '0')}`);
export const sum = a => a.reduce((x, y) => x + y, 0);

export function setup(variant, mapName, spawnNames, faction) {
  const map = readMap(mapName);
  const names = spawnNames || Object.keys(map.spawns).sort().slice(0, 2);
  const [a, b] = names.map(n => { const s = map.spawns[n.toUpperCase()]; if (!s) throw new Error(`${mapName} has no ${n} (has ${Object.keys(map.spawns).join(' ')})`); return s; });
  return { T: variant.dump.units, adjacency: variant.dump.adjacency, faction, map, spawn: a, enemies: [b], rules: variant.rules };
}

export function runStyle(variant, mapName, spawns, style, faction, minutes) {
  const st = players.styles[style];
  const r = simulate({ ...setup(variant, mapName, spawns, faction), policy: { ...DEFAULT_POLICY, ...st.policy }, until: minutes * 60 });
  for (const m of r.minutes) m.combatAlive = Math.round(m.combatBuilt * (st.survival10 ?? 1));
  return r;
}

// Fastest T2 factory: the commander opens with k extractors and m generators, then a factory
// that upgrades at once, with the commander assisting it or not.
export function fastestT2(variant, mapName, spawns, faction, { quick = false } = {}) {
  let best = null;
  const ks = quick ? [0, 2] : [0, 1, 2, 3], ms = quick ? [0, 2] : [0, 1, 2, 3, 4], assists = quick ? [true] : [true, false];
  for (const k of ks) for (const m of ms) for (const assist of assists) {
    const policy = { ...DEFAULT_POLICY, opening: [...Array(k).fill('mex'), ...Array(m).fill('pgen'), 'factory'], cmdRadius: 40,
      engFirst: 0, engRate: 0, engPerSpot: 0, engMax: 0, facMax: 1, t2FacAt: 0, t2FacIncome: 0, t2MexAt: Infinity, assist, eMargin: 1.2, react: 0.5 };
    const r = simulate({ ...setup(variant, mapName, spawns, faction), policy, until: 600 });
    if (r.firstT2Factory != null && (!best || r.firstT2Factory < best.t)) best = { t: r.firstT2Factory, opening: `${k} mex, ${m} gen, factory${assist ? ', commander assists' : ''}` };
  }
  return best;
}

// Every target row for one variant: mean over the class's maps, both spawn orders, the factions.
export function checkVariant(variant, styles = styleNames(), factions = ['e', 'c', 'g'], { quickRush = false } = {}) {
  const rows = [];
  for (const [cls, tc] of Object.entries(targets)) {
    const maxMin = Math.max(...Object.keys(tc.at).map(Number));
    const spamByMap = {};
    for (const style of styles) {
      const agg = {};
      for (const { map, spawns } of tc.maps) for (const faction of factions) for (const order of [spawns, [...spawns].reverse()]) {
        const r = runStyle(variant, map, order, style, faction, maxMin);
        if (style === 'spam') (spamByMap[map] ||= []).push(r);
        for (const [min, want] of Object.entries(tc.at)) for (const k of Object.keys(want)) {
          const m = r.minutes.find(x => x.minute === +min);
          (agg[`${min}|${k}`] ||= []).push(k === 'factories' ? sum(m.factories) : m[k]);
        }
      }
      for (const [min, want] of Object.entries(tc.at)) for (const [k, [lo, hi]] of Object.entries(want)) {
        const vs = agg[`${min}|${k}`], avg = sum(vs) / vs.length;
        rows.push({ cls, style, what: `${k} at ${min}:00`, lo, hi, value: avg, got: `${avg.toFixed(avg < 100 ? 1 : 0)} (${Math.min(...vs).toFixed(0)}-${Math.max(...vs).toFixed(0)})` });
      }
    }
    // The average player: the mean of the styles' means, which is what the score uses.
    for (const [min, want] of Object.entries(tc.at)) for (const [k, [lo, hi]] of Object.entries(want)) {
      const per = rows.filter(r => r.cls === cls && r.what === `${k} at ${min}:00` && r.style !== 'avg').map(r => r.value);
      const avg = sum(per) / per.length;
      rows.push({ cls, style: 'avg', what: `${k} at ${min}:00`, lo, hi, value: avg, got: avg.toFixed(avg < 100 ? 1 : 0) });
    }
    // The fastest T2 factory on any map of the class, and what a factory-spamming opponent
    // has alive at that moment (interpolated by minute).
    let fast = null;
    for (const { map, spawns } of tc.maps) for (const faction of factions) {
      const b = fastestT2(variant, map, spawns, faction, { quick: quickRush });
      if (b && (!fast || b.t < fast.t)) fast = { ...b, map };
    }
    const [lo, hi] = tc.fastestT2Factory;
    let facing = null;
    if (fast && spamByMap[fast.map]) {
      const at = fast.t / 60, vals = spamByMap[fast.map].map(r => {
        const i = Math.floor(at), a = r.minutes.find(x => x.minute === i)?.combatAlive ?? 0, b = r.minutes.find(x => x.minute === i + 1)?.combatAlive ?? a;
        return a + (b - a) * (at - i);
      });
      facing = sum(vals) / vals.length;
    }
    rows.push({ cls, style: 'rush', what: 'fastest T2 factory', lo, hi, value: fast ? fast.t : Infinity,
      got: fast ? `${fmtT(fast.t)} (${fast.opening})` : 'none' });
    if (tc.rushFacing) rows.push({ cls, style: 'rush', what: 'units facing the rush', lo: tc.rushFacing[0], hi: tc.rushFacing[1], value: facing ?? Infinity,
      got: facing == null ? '-' : `${Math.round(facing)} spam units alive` });
  }
  for (const r of rows) r.ok = (r.lo == null || r.value >= r.lo) && (r.hi == null || r.value <= r.hi);
  return rows;
}

// How far a variant is from the targets: sum over rows of the squared relative miss.
export function score(rows, weights = {}) {
  let s = 0;
  for (const r of rows) {
    if (r.style !== 'avg' && r.style !== 'rush') continue;
    const w = weights[r.what.replace(/ at .*/, '')] ?? 1;
    if (r.lo != null && r.value < r.lo) s += w * ((r.lo - r.value) / r.lo) ** 2;
    if (r.hi != null && r.value > r.hi) s += w * ((r.value - r.hi) / r.hi) ** 2;
  }
  return s;
}
