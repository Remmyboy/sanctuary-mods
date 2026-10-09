// Stage 1 (economy) levers for sweep.mjs. Each lever is a list of round-number choices and an
// apply(units, value) that edits a copy of the base ruleset's templates. Exploration only: the
// numbers that win go into proposed/changes.lua and are re-checked through the real patch engine.
const each = (units, re, fn) => { for (const [id, t] of Object.entries(units)) if (re.test(id)) fn(t, id); };
const tags = t => t.tags || [];
const isLandCombat = t => tags(t).includes('MOBILE') && !['AIR', 'COMMAND', 'CONSTRUCTION', 'COMBAT_ENGINEER', 'ENGINEER'].some(x => tags(t).includes(x));

export const base = 'stage1-base';

export const levers = {
  // Commander: whole numbers per second; energy at ten per alloy.
  cmdAlloys: { choices: [1, 2, 3], apply: (U, v) => each(U, /^u.l0000$/, t => { t.economy.production.alloys = v; }) },
  cmdEnergy: { choices: [20, 30, 40, 50], apply: (U, v) => each(U, /^u.l0000$/, t => { t.economy.production.energy = v; }) },
  // Commander storage; the game starts an army with half of it.
  cmdStorage: { choices: [500, 800, 1000, 1500], apply: (U, v) => each(U, /^u.l0000$/, t => { t.economy.storage = { alloys: v, energy: v * 10 }; }) },
  mex1Alloys: { choices: [1, 2], apply: (U, v) => each(U, /^u.s1601$/, t => { t.economy.production.alloys = v; }) },
  mex2Alloys: { choices: [3, 4, 5, 6], apply: (U, v) => each(U, /^u.s2601$/, t => { t.economy.production.alloys = v; }) },
  mex2Cost: { choices: [300, 450, 600, 750, 900, 1200], apply: (U, v) => each(U, /^u.s2601$/, t => { t.economy.cost = { alloys: v, energy: v * 10 }; t.economy.buildTime = v; }) },
  mex3Alloys: { choices: [8, 10, 12, 16], apply: (U, v) => each(U, /^u.s3601$/, t => { t.economy.production.alloys = v; }) },
  pgen1Energy: { choices: [10, 15, 20], apply: (U, v) => each(U, /^u.s1611$/, t => { t.economy.production.energy = v; }) },
  // Land and naval units: alloys x k, energy at 6 per alloy (land alloy-heavy, as decided).
  landCost: { choices: [1, 1.25, 1.5, 1.75, 2], apply: (U, v) => { for (const t of Object.values(U)) if (isLandCombat(t) && t.economy?.cost) { const a = t.economy.cost.alloys; t.economy.cost = { alloys: Math.round(a * v), energy: Math.round(a * v * 6) }; } } },
  fac1Cost: { choices: [150, 200, 250, 300], apply: (U, v) => each(U, /^u.s151[123]$/, t => { t.economy.cost = { alloys: v, energy: v * 10 }; }) },
  // Upgrading a factory to T2: cost and build time (the factory builds it at its own build power).
  fac2Cost: { choices: [1000, 1500, 2000, 2500, 3000], apply: (U, v) => each(U, /^u.s251[123]$/, t => { t.economy.cost = { alloys: v, energy: v * 10 }; }) },
  fac2Time: { choices: [800, 1100, 1500, 2000, 2500, 3000], apply: (U, v) => each(U, /^u.s251[123]$/, t => { t.economy.buildTime = v; }) },
};

// Design rules the sim can't enforce (its teching is scheduled, so it never rushes a cheap
// upgrade): extractor upgrades pay back their alloys in 150-300 s at T2 (FAF: 225 s) and
// 250-450 s at T3 (FAF: 383 s). T3 extractors cost 2000 alloys.
export function valid(s) {
  const p2 = s.mex2Cost / (s.mex2Alloys - s.mex1Alloys), p3 = 2000 / (s.mex3Alloys - s.mex2Alloys);
  return p2 >= 150 && p2 <= 300 && p3 >= 250 && p3 <= 450;
}
