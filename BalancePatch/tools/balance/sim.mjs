// Balance tool, step 2: one player's economy and build order over time, by the game's rules,
// with a parametrised player standing in for the human. No combat: units are built, not
// fought; losses are applied afterwards from replays (see balance.mjs).
//
// Game rules modelled (game 0.0.1.20, paths under the game's LJ\lua):
//   tick 0.1 s (host/generated/lua/constants.lua TickRate 10)
//   drain per builder = cost x buildPower x adjacency discount / buildTime, per resource
//     (host/systems/resourceEntity.lua AddBuilder, RecalculateBuildDrain)
//   progress per tick = buildPower/10 x productionMultiplier; done at buildTime
//     (host/interfaces/work/tasks/buildable.lua TickBuildProgress)
//   satisfaction per resource = min((stored + income) / request, 1), measured against the full
//     request at the end of a tick and used the next tick; a build runs at the lower of its
//     resources' satisfactions; spending more than is stored empties the store
//     (host/systems/economy.lua Update)
//   income from extractors, generators and the commander is never throttled ("generation")
//   storage: the commander's, plus factory energy storage; start with startShare of it
//     (host/units/unitsClasses/unitsDefault.lua HostCommander:OnStopBeingBuilt gives half)
//   adjacency discount = 1 + sum of extras, per resource (host/systems/buffs.lua calculateValue)
//   upgrades charge the target's full cost and build time; the old structure keeps producing
//     until replaced (assumed: nothing in UpgradeBehaviorThread disables it)
// Not modelled: combat, raids, reclaim, storage structures, terrain and pathing (straight
// lines x pathFactor), placement blocking, air and navy.

import { territory } from './maps.mjs';

export const FACTION_TAG = { e: 'EDA', c: 'CHOSEN', g: 'GUARD' };

// The stand-in player. Defaults are a starting point; calibrate.mjs fits them to replays.
export const DEFAULT_POLICY = {
  opening: ['factory', 'mex', 'mex', 'pgen', 'pgen'], // the commander's first builds
  cmdRadius: 45,       // after the opening, the commander builds only this close to its spawn
  react: 2.0,          // seconds a builder waits between finishing one job and starting the next
  pathFactor: 1.25,    // walking distance over straight-line distance
  baseRadius: 18,      // where base structures go: a ring this far from spawn (grows with count)
  eMargin: 1.0,        // build a generator when energy income < eMargin x full energy request
  eLowFrac: 0.3,       // ... or when stored energy is under this share of storage and falling
  expandFrac0: 0.25,   // engineers take spots within this share of the distance to the enemy ...
  expandRate: 0.06,    // ... growing by this share per minute
  engFirst: 3,         // engineers the first factory makes before anything else
  engRate: 0.6,        // further engineers wanted per minute of game time
  engMax: 12,
  engPerSpot: 0.25,    // and this many more per free spot left in territory
  facBank: 250,        // alloys stored before a new factory goes down
  facRatio: 0.7,       // ... and only while alloy income >= facRatio x what the factories drain
  facMax: 24,
  facFirstAt: 0,       // earliest time (s) for a second factory
  adjPgen: 5,          // generators placed against each of the first adjFactories factories
  adjFactories: 2,
  adjMexFirst: 2,      // extractors touching the first factory (it goes beside spawn spots)
  assist: true,        // idle builders assist the nearest job
  // Teching: ASSUMED, not calibrated (the replays it was fitted to had almost no T2).
  t2MexAt: 480,        // from this time, idle builders upgrade extractors to T2 ...
  t2MexIncome: 0,      // ... once alloy income reaches this
  t3MexAt: 900,        // and T2 extractors to T3
  upgradesAtOnce: 2,   // extractor upgrades running at the same time
  t2FacAt: 540,        // from this time, upgrade a factory to T2 ...
  t2FacIncome: 25,     // ... once alloy income reaches this
  t2FacMax: 2,         // how many T2 factories to aim for
  t3FacAt: 900,
  t3FacIncome: 60,
  unitMix: null,       // { tpId: weight } per tier, default: the faction's main tank / T2 unit
};

const has = (tp, tag) => (tp?.tags || []).includes(tag);
const tierOf = tp => +((tp?.tags || []).find(t => /^TECH\d$/.test(t)) || 'TECH0').slice(4);

// The combat unit a factory of this tier builds by default: the faction's land unit new at
// that tier, a TANK if there is one, else the one with the most health.
export function defaultUnit(T, faction, tier) {
  const ftag = FACTION_TAG[faction];
  const c = Object.entries(T).filter(([id, tp]) => has(tp, ftag) && has(tp, 'MOBILE') && has(tp, 'LAND') && tierOf(tp) === tier
    && has(tp, `BUILDABLE_BY_T${tier}_FACTORY`) && !has(tp, 'DEMO_UI_ONLY') && !has(tp, 'ENGINEER') && !has(tp, 'SCOUT')
    && !has(tp, 'ANTI_AIR') && !has(tp, 'COMMAND'));
  c.sort((a, b) => (has(b[1], 'TANK') - has(a[1], 'TANK')) || (b[1].defence.health.max - a[1].defence.health.max));
  return c[0]?.[0];
}

export function simulate({ T, adjacency = {}, faction = 'e', map, spawn, enemies, rules = {}, policy = {}, until = 900, trace = false }) {
  const P = { ...DEFAULT_POLICY, ...policy };
  const id = s => s.replace('?', faction);
  const tp = s => { const t = T[id(s)]; if (!t) throw new Error('no template ' + id(s)); return t; };
  const COM = tp('u?l0000');
  const ENG = id('u?l1501');
  const FAC = [null, id('u?s1511'), id('u?s2511'), id('u?s3511')];
  const MEX = [null, id('u?s1601'), id('u?s2601'), id('u?s3601')];
  const PGEN = id('u?s1611');
  const unitFor = tier => {
    const mix = P.unitMix?.[tier];
    if (mix) { const ids = Object.keys(mix); return id(ids[Math.floor(rng() * ids.length)]); }
    return defaultUnits[tier] ??= defaultUnit(T, faction, tier);
  };
  const defaultUnits = [];
  let seed = 12345;
  const rng = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);

  const startShare = rules.startShare ?? 0.5;
  const res = {
    alloys: { cur: 0, storage: COM.economy.storage.alloys, gen: COM.economy.production.alloys, sat: 1, req: 0 },
    energy: { cur: 0, storage: COM.economy.storage.energy, gen: COM.economy.production.energy, sat: 1, req: 0 },
  };
  res.alloys.cur = res.alloys.storage * startShare;
  res.energy.cur = res.energy.storage * startShare;

  const spots = territory(map, spawn, [spawn, ...enemies]).map(s => ({ ...s, taken: false }));
  const enemyDist = Math.min(...enemies.map(e => Math.hypot(e.x - spawn.x, e.z - spawn.z)));
  const structures = [];      // { tp, x, z, kind, tier, adjPgen, adjMex, job }
  const builders = [];        // { kind, x, z, speed, range, bp, task, wait, structure }
  const jobs = new Set();     // { tp, x, z, progress, builders: Set, onDone, kind, owner }
  const counts = { combat: 0, combatAlloys: 0, combatValue: 0, engineers: 0 };
  const log = [];
  const minutes = [];
  let t = 0;
  let baseSlots = 0;

  const value = t => (t.economy.cost.alloys || 0) + (t.economy.cost.energy || 0) / 10;
  const dist = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);
  const nextBaseSpot = () => {
    // Structures in a widening ring round the spawn, facing away from the enemy.
    const n = baseSlots++;
    const ring = P.baseRadius + 6 * Math.floor(n / 10);
    const away = Math.atan2(spawn.z - enemies[0].z, spawn.x - enemies[0].x);
    const a = away + (n % 10 - 4.5) * 0.45;
    return { x: spawn.x + Math.cos(a) * ring, z: spawn.z + Math.sin(a) * ring };
  };

  const commander = { kind: 'commander', x: spawn.x, z: spawn.z, speed: COM.movement.speed, range: COM.construction.range, bp: COM.construction.buildPower, task: null, wait: 0, opening: [...P.opening] };
  builders.push(commander);

  const byKind = { factory: [], mex: [], pgen: [] };
  const factories = () => byKind.factory;
  const mexes = () => byKind.mex;
  const engineerList = [];
  const engineers = () => engineerList;

  function discountFor(builder) {
    // A factory's adjacency: each touching extractor -10% alloys, each generator -2.5/-10/-15% energy
    // (values from the dump's adjacency buffs).
    const s = builder.structure;
    if (!s || s.kind !== 'factory') return null;
    const adj = adjacency;
    const a = 1 + s.adjMex * (adj?.AlloyExtractorConstructionDiscount?.extra ?? -0.1);
    const e = 1 + s.adjPgen * (adj?.T1EnergyGeneratorConstructionDiscount?.extra ?? -0.025);
    return { alloys: Math.max(0, a), energy: Math.max(0, e) };
  }

  function startJob(builder, tpId, at, kind, onDone) {
    const job = { tp: tpId, x: at.x, z: at.z, progress: 0, builders: new Set([builder]), onDone, kind, started: t };
    jobs.add(job);
    builder.task = { type: 'build', job };
    return job;
  }

  function placeStructure(tpId, kind, at, extra = {}) {
    const t2 = T[tpId];
    const s = { tp: tpId, x: at.x, z: at.z, kind, tier: tierOf(t2), adjPgen: 0, adjMex: 0, ...extra };
    structures.push(s);
    byKind[kind]?.push(s);
    const e = t2.economy;
    if (e.production) for (const r in e.production) res[r].gen += e.production[r];
    if (e.storage) for (const r in e.storage) res[r].storage += e.storage[r];
    return s;
  }

  function replaceStructure(old, tpId) {
    const o = T[old.tp].economy, n = T[tpId].economy;
    if (o.production) for (const r in o.production) res[r].gen -= o.production[r];
    if (o.storage) for (const r in o.storage) res[r].storage -= o.storage[r];
    if (n.production) for (const r in n.production) res[r].gen += n.production[r];
    if (n.storage) for (const r in n.storage) res[r].storage += n.storage[r];
    old.tp = tpId; old.tier = tierOf(T[tpId]);
  }

  // ---- what an idle builder does next
  function goBuild(b, kind, tpId, at, onDone) {
    b.task = { type: 'move', to: at, then: () => startJob(b, tpId, at, kind, onDone) };
  }

  function wantEnergy() {
    const e = res.energy;
    // What is being spent on, not counting generators under construction themselves.
    const need = e.spend ?? e.req;
    if (e.gen < P.eMargin * need) return true;
    if (e.cur < P.eLowFrac * e.storage && e.gen < need) return true;
    // Planned demand: every factory busy and every mobile builder on structures.
    const facDrain = factories().reduce((s, f) => s + factoryDrain(f).energy, 0);
    return e.gen < P.eMargin * 0.6 * facDrain;
  }

  function factoryDrain(f) {
    const u = T[unitFor(f.tier)];
    const bp = T[f.tp].construction.buildPower;
    return { alloys: u.economy.cost.alloys * bp / u.economy.buildTime, energy: u.economy.cost.energy * bp / u.economy.buildTime };
  }

  function buildPgen(b) {
    // Against a factory with free adjacency slots, else in the base ring.
    const f = factories().slice(0, P.adjFactories).find(f => f.adjPgen < P.adjPgen && !f.pendingPgen);
    let at, onDone;
    if (f) { at = { x: f.x + 5, z: f.z }; f.pendingPgen = true; onDone = () => { f.pendingPgen = false; f.adjPgen++; placeStructure(PGEN, 'pgen', at); }; }
    else { at = nextBaseSpot(); onDone = () => placeStructure(PGEN, 'pgen', at); }
    goBuild(b, 'pgen', PGEN, at, onDone);
  }

  function buildMex(b, maxD) {
    const free = spots.filter(s => !s.taken && s.d <= maxD).sort((a, c) => dist(b, a) - dist(b, c));
    const s = free[0];
    if (!s) return false;
    s.taken = true;
    goBuild(b, 'mex', MEX[1], s, () => { s.mex = placeStructure(MEX[1], 'mex', s); });
    return true;
  }

  function buildFactory(b) {
    const first = factories().length === 0;
    const at = first ? { x: spawn.x + 6, z: spawn.z } : nextBaseSpot();
    goBuild(b, 'factory', FAC[1], at, () => {
      const f = placeStructure(FAC[1], 'factory', at, { adjMex: first ? P.adjMexFirst : 0, made: 0 });
      const fb = { kind: 'factory', x: at.x, z: at.z, bp: T[FAC[1]].construction.buildPower, task: null, wait: 0, structure: f };
      f.builder = fb;
      builders.push(fb);
    });
  }

  function wantFactory() {
    const fs = factories();
    if (fs.length >= P.facMax) return false;
    if (fs.length >= 1 && t < P.facFirstAt) return false;
    const drain = fs.reduce((s, f) => s + factoryDrain(f).alloys, 0);
    return res.alloys.cur >= P.facBank && res.alloys.gen >= P.facRatio * drain;
  }

  function upgradeTarget() {
    // An extractor to take to T2 (nearest to spawn first), if the policy says so.
    if (mexes().filter(m => m.upgrading).length >= P.upgradesAtOnce) return null;
    if (t >= P.t2MexAt && res.alloys.gen >= P.t2MexIncome) {
      const m = mexes().filter(m => m.tier === 1 && !m.upgrading).sort((a, c) => dist(a, spawn) - dist(c, spawn))[0];
      if (m) return m;
    }
    if (t >= P.t3MexAt) {
      const m = mexes().filter(m => m.tier === 2 && !m.upgrading).sort((a, c) => dist(a, spawn) - dist(c, spawn))[0];
      if (m) return m;
    }
    return null;
  }

  function startMexUpgrade(m, helper) {
    m.upgrading = true;
    const next = MEX[m.tier + 1];
    const self = { kind: 'mexself', x: m.x, z: m.z, bp: T[m.tp].construction.buildPower, task: null, wait: 0, structure: m };
    builders.push(self);
    const job = startJob(self, next, m, 'upgrade', () => { replaceStructure(m, next); m.upgrading = false; builders.splice(builders.indexOf(self), 1); });
    if (helper) helper.task = { type: 'move', to: m, then: () => { job.builders.add(helper); helper.task = { type: 'build', job }; } };
  }

  function assistNearest(b) {
    let best = null, bd = Infinity;
    for (const j of jobs) { const d = dist(b, j); if (d < bd && (b.kind !== 'commander' || dist(j, spawn) <= P.cmdRadius)) { bd = d; best = j; } }
    if (!best) return false;
    b.task = { type: 'move', to: best, then: () => { if (jobs.has(best)) { best.builders.add(b); b.task = { type: 'build', job: best }; } else b.task = null; } };
    return true;
  }

  function decideMobile(b) {
    if (b.kind === 'commander' && b.opening.length) {
      const k = b.opening.shift();
      if (k === 'factory') return buildFactory(b);
      if (k === 'mex') { if (buildMex(b, P.cmdRadius)) return; return decideMobile(b); }
      if (k === 'pgen') return buildPgen(b);
    }
    const maxD = b.kind === 'commander' ? P.cmdRadius : enemyDist * (P.expandFrac0 + P.expandRate * t / 60);
    if (wantEnergy()) return buildPgen(b);
    if (buildMex(b, maxD)) return;
    if (wantFactory()) return buildFactory(b);
    const up = upgradeTarget();
    if (up) return startMexUpgrade(up, b);
    if (P.assist && assistNearest(b)) return;
    b.wait = 1;
  }

  function decideFactory(fb) {
    const f = fb.structure;
    // Factory upgrades: the factory builds its next tier itself (and makes nothing meanwhile).
    const tFac = factories().filter(x => x.tier >= 2 || x.upgrading).length;
    if (f.tier === 1 && tFac < P.t2FacMax && t >= P.t2FacAt && res.alloys.gen >= P.t2FacIncome) {
      f.upgrading = true;
      startJob(fb, FAC[2], f, 'upgrade', () => { replaceStructure(f, FAC[2]); f.upgrading = false; fb.bp = T[FAC[2]].construction.buildPower; });
      return;
    }
    if (f.tier === 2 && t >= P.t3FacAt && res.alloys.gen >= P.t3FacIncome && !factories().some(x => x.tier === 3 || x.upgrading)) {
      f.upgrading = true;
      startJob(fb, FAC[3], f, 'upgrade', () => { replaceStructure(f, FAC[3]); f.upgrading = false; fb.bp = T[FAC[3]].construction.buildPower; });
      return;
    }
    const isFirst = factories()[0] === f;
    const wantEng = (isFirst && f.made < P.engFirst) || engineers().length < Math.min(P.engMax, P.engFirst + P.engRate * Math.max(0, t - 60) / 60 + P.engPerSpot * spots.filter(x => !x.taken).length);
    const unit = wantEng ? ENG : unitFor(f.tier);
    startJob(fb, unit, f, wantEng ? 'engineer' : 'combat', () => {
      f.made++;
      const u = T[unit];
      if (wantEng) {
        const e = T[ENG];
        const eb = { kind: 'engineer', x: f.x, z: f.z + 6, speed: e.movement.speed, range: e.construction.range, bp: e.construction.buildPower, task: null, wait: P.react };
        builders.push(eb); engineerList.push(eb);
        counts.engineers++;
      } else {
        counts.combat++; counts.combatAlloys += u.economy.cost.alloys; counts.combatValue += value(u);
        if (trace) log.push([+t.toFixed(1), 'unit', unit]);
      }
    });
  }

  // ---- main loop
  const dt = 0.1;
  for (let tick = 0; t < until; tick++, t = tick * dt) {
    // decisions
    for (const b of builders) {
      if (b.task || b.kind === 'mexself') continue;
      if (b.wait > 0) { b.wait -= dt; continue; }
      if (b.kind === 'factory') decideFactory(b);
      else { decideMobile(b); if (b.task) b.wait = 0; }
    }
    // movement
    for (const b of builders) {
      const task = b.task;
      if (!task || task.type !== 'move') continue;
      const reach = (b.range ?? 0) + 1.5;
      const d = dist(b, task.to);
      if (d <= reach + 0.01) { b.task = null; task.then(); continue; }
      const step = Math.min(d - reach, b.speed * dt / P.pathFactor);
      if (step >= d - reach - 0.01) { b.x += (task.to.x - b.x) / d * step; b.z += (task.to.z - b.z) / d * step; b.task = null; task.then(); continue; }
      b.x += (task.to.x - b.x) / d * step; b.z += (task.to.z - b.z) / d * step;
    }
    // economy (host/systems/economy.lua Update)
    const reqA = [], reqE = [];
    let requestA = 0, requestE = 0, spendE = 0;
    for (const j of jobs) {
      const e = T[j.tp].economy;
      let ra = 0, re = 0, bp = 0;
      for (const b of j.builders) {
        const disc = discountFor(b);
        ra += b.bp * (disc ? disc.alloys : 1); re += b.bp * (disc ? disc.energy : 1); bp += b.bp;
      }
      j.drainA = (e.cost.alloys || 0) * ra / e.buildTime;
      j.drainE = (e.cost.energy || 0) * re / e.buildTime;
      j.bp = bp;
      requestA += j.drainA; requestE += j.drainE;
      if (j.kind !== "pgen") spendE += j.drainE;
    }
    res.energy.spend = spendE;
    const f = Math.min(res.alloys.sat, res.energy.sat);
    for (const [r, req] of [['alloys', requestA], ['energy', requestE]]) {
      const R = res[r];
      const income = R.gen * dt, request = req * dt, outcome = request * f;
      R.cur += income;
      R.cur = R.cur < outcome ? 0 : R.cur - outcome;
      R.sat = request > 0 ? Math.min((R.cur + income) / request, 1) : 1;
      if (R.cur > R.storage) R.cur = R.storage;
      R.req = req;
      R.stallTime = (R.stallTime || 0) + (R.sat < 0.999 && request > 0 ? dt : 0);
    }
    // progress and completion
    for (const j of [...jobs]) {
      j.progress += j.bp * dt * f;
      if (j.progress >= T[j.tp].economy.buildTime) {
        jobs.delete(j);
        for (const b of j.builders) if (b.task?.job === j) { b.task = null; b.wait = b.kind === 'factory' ? 0 : P.react; }
        j.onDone();
        if (j.kind !== 'combat') log.push([+t.toFixed(1), j.kind, j.tp]);
      }
    }
    // per-minute record
    if (trace && (tick + 1) % 600 === 0) for (const b of builders) if (b.kind !== "mexself") log.push([+t.toFixed(1), "state", b.kind + " " + (b.task ? b.task.type + " " + (b.task.job?.tp || "") + (b.task.to ? " to " + Math.round(Math.hypot(b.task.to.x - b.x, b.task.to.z - b.z)) : "") : "idle w" + b.wait.toFixed(1))]);
    if ((tick + 1) % 600 === 0) {
      const m = (tick + 1) / 600;
      minutes.push({
        minute: m,
        alloyIncome: +res.alloys.gen.toFixed(2), energyIncome: +res.energy.gen.toFixed(1),
        alloyStored: Math.round(res.alloys.cur), energyStored: Math.round(res.energy.cur),
        energySatisfaction: +res.energy.sat.toFixed(2), alloySatisfaction: +res.alloys.sat.toFixed(2),
        mex: [1, 2, 3].map(tr => mexes().filter(x => x.tier === tr).length),
        pgen: byKind.pgen.length,
        factories: [1, 2, 3].map(tr => factories().filter(x => x.tier === tr).length),
        engineers: engineers().length,
        buildPower: builders.filter(b => b.kind !== 'mexself').reduce((s, b) => s + b.bp, 0),
        combatBuilt: counts.combat, combatAlloysSpent: counts.combatAlloys, combatValueBuilt: Math.round(counts.combatValue),
        stallAlloys: +(res.alloys.stallTime || 0).toFixed(0), stallEnergy: +(res.energy.stallTime || 0).toFixed(0),
      });
    }
  }
  const firstT2 = log.find(x => x[1] === 'upgrade' && x[2] === FAC[2]);
  return { minutes, log, firstT2Factory: firstT2 ? firstT2[0] : null, spotsInTerritory: spots.length };
}
