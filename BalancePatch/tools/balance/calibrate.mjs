// Fits the stand-in player's behaviour (sim.mjs DEFAULT_POLICY) to real games: per-minute
// income, extractors, factories, engineers and combat units built, read from replays by the
// dev-only BalanceCal. The economy is the ruleset each game ran (a dump); only behaviour is
// fitted, so a fitted player can then be run on other rulesets.
import { simulate, DEFAULT_POLICY } from './sim.mjs';
import { readMap } from './maps.mjs';

const FACTION = { EDA: 'e', Chosen: 'c', Guardians: 'g' };

// Behaviour parameters and their search ranges.
export const FIT = {
  react: [0.5, 6],
  eMargin: [0.6, 1.6],
  expandFrac0: [0.05, 0.6],
  expandRate: [0, 0.2],
  engPerSpot: [0, 1],
  engFirst: [1, 5, 'int'],
  engRate: [0.1, 2.5],
  engMax: [4, 20, 'int'],
  facBank: [50, 500],
  facRatio: [0.3, 1.5],
  adjPgen: [0, 6, 'int'],
  facFirstAt: [0, 400],
};

// One player of one game, ready to simulate.
export function cases(calibration, rulesets) {
  const out = [];
  for (const game of calibration) {
    const rs = rulesets[game.replay];
    if (!rs) continue;
    const map = readMap(game.map);
    for (const p of game.players) {
      const other = game.players.find(q => q !== p);
      out.push({ label: `${game.replay.slice(0, 19)} army ${p.army} (${p.faction})`, map, spawn: p.spawn, enemies: [other.spawn],
        faction: FACTION[p.faction], dump: rs.dump, rules: rs.rules || {}, real: p.minutes, until: Math.min(10, Math.floor(game.minutes)) * 60 });
    }
  }
  return out;
}

const W = { alloy: 2, energy: 1, mex: 1, fac: 1, eng: 0.5, combat: 2 };
export function error(sim, real) {
  let e = 0, n = 0;
  for (const s of sim) {
    const r = real.find(x => x.minute === s.minute);
    if (!r || r.alloyIncome == null) continue;
    const d = (a, b, floor) => (a - b) / Math.max(floor, Math.abs(b));
    e += W.alloy * d(s.alloyIncome, r.alloyIncome, 5) ** 2
      + W.energy * d(s.energyIncome, r.energyIncome, 50) ** 2
      + W.mex * d(s.mex.reduce((a, b) => a + b, 0), r.mex.reduce((a, b) => a + b, 0), 2) ** 2
      + W.fac * d(s.factories.reduce((a, b) => a + b, 0), r.factories.reduce((a, b) => a + b, 0), 1.5) ** 2
      + W.eng * d(s.engineers, r.engineers, 2) ** 2
      + W.combat * d(s.combatBuilt, r.combatBuilt, 10) ** 2;
    n++;
  }
  return n ? e / n : Infinity;
}

export function runCase(c, policy) {
  return simulate({ T: c.dump.units, adjacency: c.dump.adjacency, faction: c.faction, map: c.map, spawn: c.spawn,
    enemies: c.enemies, rules: c.rules, policy, until: c.until }).minutes;
}

const clampParam = (k, v) => {
  const [lo, hi, kind] = FIT[k];
  v = Math.min(hi, Math.max(lo, v));
  return kind === 'int' ? Math.round(v) : v;
};

// Random search, then shrinking perturbations around the best. Deterministic (seeded).
export function fit(caseList, { iterations = 600, seed = 1, start = {} } = {}) {
  let s = seed;
  const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647);
  const score = p => caseList.reduce((sum, c) => sum + error(runCase(c, p), c.real), 0) / caseList.length;
  let best = { ...DEFAULT_POLICY, ...start }, bestScore = score(best);
  for (let i = 0; i < iterations; i++) {
    const p = { ...best };
    const scale = i < iterations / 3 ? 1 : 0.25 * (1 - i / iterations) + 0.05;
    for (const k of Object.keys(FIT)) {
      if (i < iterations / 3 ? rnd() < 0.7 : rnd() < 0.3) {
        const [lo, hi] = FIT[k];
        p[k] = clampParam(k, i < iterations / 3 ? lo + rnd() * (hi - lo) : p[k] + (rnd() - 0.5) * (hi - lo) * scale);
      }
    }
    const sc = score(p);
    if (sc < bestScore) { best = p; bestScore = sc; }
  }
  return { policy: Object.fromEntries(Object.keys(FIT).map(k => [k, +(+best[k]).toFixed(3)])), score: bestScore };
}
