// Balance tool. Rulesets in variants.json, player styles in players.json, targets in targets.json.
//
//   node balance.mjs dump <variant> [--out f]       templates with the patch applied, by the mod's
//                                                    own patch.lua under the game's LuaJIT
//   node balance.mjs eco <variant>                   eco structures: cost, output, payback; build power
//   node balance.mjs sim <variant> --map M [--spawns ARMY_1,ARMY_2] [--style S] [--faction e|c|g]
//                                     [--minutes N] [--trace]
//   node balance.mjs rush <variant> --map M [...]    fastest T2 factory over a set of openings
//   node balance.mjs check <variant>...              every map class, style and faction vs targets.json
//   node balance.mjs diff <variant> <variant>...     the same numbers side by side
//   node balance.mjs calibrate <calibration.json> <rulesets.json> [--iterations N]
//       fits players.json to replays (calibration.json from the dev-only BalanceCal;
//       rulesets.json maps each replay name to { "variant": ..., "styles": [...] })
//
// Everything printed comes from the simulation (sim.mjs), not from the game: the sim's limits
// are listed at the top of sim.mjs and in REDESIGN-PLAN.md.
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { execFileSync } from 'child_process';
import { simulate, DEFAULT_POLICY, FACTION_TAG } from './sim.mjs';
import { readMap } from './maps.mjs';
import { cases, fit, runCase, error } from './calibrate.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const modDir = path.resolve(here, '../..');
const GAME = 'C:/Program Files (x86)/Steam/steamapps/common/Sanctuary Shattered Sun Playtest/engine';
const LUA_DLL = `${GAME}/Sanctuary_Data/Plugins/x86_64/lua51.dll`;
const readJson = f => JSON.parse(fs.readFileSync(f, 'utf8'));

const argv = process.argv.slice(2);
const cmd = argv.shift();
const flag = name => { const i = argv.indexOf(name); if (i < 0) return undefined; const v = argv[i + 1]; argv.splice(i, v && !v.startsWith('--') ? 2 : 1); return v && !v.startsWith('--') ? v : true; };

// ---------------------------------------------------------------- rulesets

const variants = readJson(path.join(here, 'variants.json'));
const manifest = readJson(path.join(modDir, 'mod.json'));

function optionString(spec) {
  if (spec === 'vanilla') return 'vanilla';
  const opts = Object.fromEntries(manifest.options.map(o => [o.key, o.default !== false]));
  if (spec && typeof spec === 'object') Object.assign(opts, spec);
  return Object.entries(opts).map(([k, v]) => `${k}=${v ? 1 : 0}`).join(',');
}

function gameRef() {
  const dll = path.join(repo, 'tools/GameRef/bin/Release/net8.0/GameRef.dll');
  if (!fs.existsSync(dll)) execFileSync('dotnet', ['build', path.join(repo, 'tools/GameRef/GameRef.csproj'), '-c', 'Release', '-v:quiet', '--nologo'], { stdio: 'inherit' });
  return dll;
}

// A variant's dump, rebuilt when its changes file, the patch engine or the dumper are newer.
export function loadVariant(name) {
  const v = variants[name];
  if (!v) throw new Error(`unknown variant ${name} (variants.json has ${Object.keys(variants).filter(k => k !== 'about').join(', ')})`);
  const changes = path.resolve(here, v.changes || '../../lua/balancepatch/changes.lua');
  const cacheDir = path.join(here, '.cache');
  fs.mkdirSync(cacheDir, { recursive: true });
  const out = path.join(cacheDir, name + '.json');
  const opts = optionString(v.options);
  const inputs = [changes, path.join(modDir, 'lua/balancepatch/patch.lua'), path.join(here, 'dump.lua'), path.join(here, 'variants.json')];
  const stale = !fs.existsSync(out) || inputs.some(f => fs.statSync(f).mtimeMs > fs.statSync(out).mtimeMs);
  if (stale) {
    const msg = execFileSync('dotnet', [gameRef(), 'luarun', LUA_DLL, path.join(here, 'dump.lua'), `${GAME}/LJ/lua`, path.join(modDir, 'lua'), changes, opts, out]).toString().trim();
    console.error(`dump ${name}: ${msg}`);
  }
  return { name, dump: readJson(out), rules: v.rules || {} };
}

// ---------------------------------------------------------------- players

const playersPath = path.join(here, 'players.json');
const players = fs.existsSync(playersPath) ? readJson(playersPath) : { styles: { default: { policy: {}, survival10: 0.6 } } };
const styleNames = s => (s && s !== true ? s.split(',') : Object.keys(players.styles));

function setup(variant, mapName, spawnNames, faction) {
  const map = readMap(mapName);
  const names = spawnNames || Object.keys(map.spawns).sort().slice(0, 2);
  const [a, b] = names.map(n => { const s = map.spawns[n.toUpperCase()]; if (!s) throw new Error(`${mapName} has no ${n} (has ${Object.keys(map.spawns).join(' ')})`); return s; });
  return { T: variant.dump.units, adjacency: variant.dump.adjacency, faction, map, spawn: a, enemies: [b], rules: variant.rules };
}

function runStyle(variant, mapName, spawns, style, faction, minutes) {
  const st = players.styles[style];
  const r = simulate({ ...setup(variant, mapName, spawns, faction), policy: { ...DEFAULT_POLICY, ...st.policy }, until: minutes * 60 });
  for (const m of r.minutes) m.combatAlive = Math.round(m.combatBuilt * (st.survival10 ?? 1));
  return r;
}

// Fastest T2 factory: the commander opens with k extractors and m generators, then a factory
// that upgrades at once, with the commander assisting it or not.
function fastestT2(variant, mapName, spawns, faction) {
  let best = null;
  for (let k = 0; k <= 3; k++) for (let m = 0; m <= 4; m++) for (const assist of [true, false]) {
    const policy = { ...DEFAULT_POLICY, opening: [...Array(k).fill('mex'), ...Array(m).fill('pgen'), 'factory'], cmdRadius: 40,
      engFirst: 0, engRate: 0, engPerSpot: 0, engMax: 0, facMax: 1, t2FacAt: 0, t2FacIncome: 0, assist, eMargin: 1.2, react: 0.5 };
    const r = simulate({ ...setup(variant, mapName, spawns, faction), policy, until: 600 });
    if (r.firstT2Factory != null && (!best || r.firstT2Factory < best.t)) best = { t: r.firstT2Factory, opening: `${k} mex, ${m} gen, factory${assist ? ', commander assists' : ''}` };
  }
  return best;
}

// ---------------------------------------------------------------- reports

const fmtT = s => (s == null ? '-' : `${Math.floor(s / 60)}:${String(Math.round(s % 60)).padStart(2, '0')}`);
const sum = a => a.reduce((x, y) => x + y, 0);

function ecoReport(variant) {
  const T = variant.dump.units;
  console.log(`\n${variant.name}: economy structures (my arithmetic on the templates; value = alloys + energy/10)`);
  console.log('id       tier name                          alloys  energy    bt  output       per-cost  alloy payback  value payback');
  // One faction's rows; the others are listed only where they differ.
  const key = t => JSON.stringify([t.economy, t.construction?.buildPower]);
  const rows = Object.entries(T).filter(([id, t]) => /^u.s[123]6(01|11|03)$/.test(id) && !(t.tags || []).includes('DEMO_UI_ONLY'))
    .filter(([id, t]) => id[1] === 'e' || key(t) !== key(T['ue' + id.slice(2)] || {}));
  for (const [id, t] of rows.sort()) {
    const e = t.economy, c = e.cost, p = e.production || {};
    const prev = /^u.s([23])601$/.test(id) ? T[id.replace(/s([23])601/, (_, n) => `s${n - 1}601`)] : null;
    const gain = (p.alloys || 0) - (prev?.economy.production?.alloys || 0);
    const upkeep = e.maintenanceConsumption?.energy;
    const out = (p.alloys ? `${p.alloys} A/s` : p.energy ? `${p.energy} E/s` : '') + (upkeep ? ` for ${upkeep} E/s` : '');
    const perCost = p.alloys ? (p.alloys / (c.alloys + c.energy / 10)).toFixed(4) + ' A' : p.energy ? (p.energy / c.alloys).toFixed(2) + ' E/A' : '';
    const ap = p.alloys && !upkeep ? `${Math.round(c.alloys / gain)} s${prev ? ' (upgrade)' : ''}` : upkeep ? `converts ${upkeep / p.alloys} E per A` : '';
    const vp = p.alloys && !upkeep ? `${Math.round((c.alloys + c.energy / 10) / gain)} s` : '';
    console.log(`${id}  T${(t.tags.find(x => /^TECH/.test(x)) || '').slice(4)}   ${(t.general.displayName || '').padEnd(29).slice(0, 29)} ${String(c.alloys).padStart(6)} ${String(c.energy).padStart(7)} ${String(e.buildTime).padStart(5)}  ${out.padEnd(20)}  ${perCost.padStart(9)}  ${ap.padStart(13)}  ${vp.padStart(13)}`);
  }
  const com = T[Object.keys(T).find(id => /^u.l0000$/.test(id))];
  console.log(`commander: ${JSON.stringify(com.economy.production)}, storage ${JSON.stringify(com.economy.storage)}, start ${variant.rules.startShare ?? 0.5} of it, build power ${com.construction.buildPower}`);
  for (const f of ['e', 'c', 'g']) {
    const fac = T[`u${f}s1511`], eng = T[`u${f}l1501`], tank = T[`u${f}l1001`];
    const drain = tank.economy.cost.alloys * fac.construction.buildPower / tank.economy.buildTime;
    console.log(`${FACTION_TAG[f].padEnd(6)} T1 factory ${fac.economy.cost.alloys} A (bp ${fac.construction.buildPower}); engineer ${eng.economy.cost.alloys} A (bp ${eng.construction.buildPower}); T1 tank ${tank.economy.cost.alloys} A + ${tank.economy.cost.energy} E: a factory on tanks drains ${drain.toFixed(2)} A/s`);
  }
}

const METRICS = [['alloyIncome', 'A/s'], ['energyIncome', 'E/s'], ['mexTotal', 'mex'], ['factoryTotal', 'fac'], ['engineers', 'eng'], ['combatBuilt', 'built'], ['combatAlive', 'alive'], ['stallEnergy', 'E-stall s']];
const metric = (m, k) => (k === 'mexTotal' ? sum(m.mex) : k === 'factoryTotal' ? sum(m.factories) : m[k]);

function simReport(variant, mapName, spawns, styles, faction, minutes, trace) {
  for (const style of styles) {
    const r = runStyle(variant, mapName, spawns, style, faction, minutes);
    console.log(`\n${variant.name} | ${mapName} | style ${style} | ${FACTION_TAG[faction]} | territory ${r.spotsInTerritory} spots`);
    console.log(' min ' + METRICS.map(([, h]) => h.padStart(8)).join(''));
    for (const m of r.minutes) console.log(String(m.minute).padStart(4) + ' ' + METRICS.map(([k]) => String(metric(m, k)).padStart(8)).join(''));
    if (trace) console.log(r.log.filter(x => x[1] !== 'state').slice(0, 40).map(x => `${x[1]}@${Math.round(x[0])}`).join(' '));
  }
}

function checkVariant(variant, styles, factions) {
  const targets = readJson(path.join(here, 'targets.json')).classes;
  const rows = [];
  for (const [cls, tc] of Object.entries(targets)) {
    const maxMin = Math.max(...Object.keys(tc.at).map(Number));
    for (const style of styles) {
      const agg = {};
      let n = 0;
      for (const { map, spawns } of tc.maps) for (const faction of factions) for (const order of [spawns, [...spawns].reverse()]) {
        const r = runStyle(variant, map, order, style, faction, maxMin);
        n++;
        for (const [min, want] of Object.entries(tc.at)) for (const k of Object.keys(want)) {
          const m = r.minutes.find(x => x.minute === +min);
          const v = k === 'factories' ? sum(m.factories) : m[k];
          (agg[`${min}|${k}`] ||= []).push(v);
        }
      }
      for (const [min, want] of Object.entries(tc.at)) for (const [k, [lo, hi]] of Object.entries(want)) {
        const vs = agg[`${min}|${k}`]; const avg = sum(vs) / vs.length;
        const ok = (lo == null || avg >= lo) && (hi == null || avg <= hi);
        rows.push({ cls, style, what: `${k} at ${min}:00`, want: `${lo ?? ''}-${hi ?? ''}`, got: `${avg.toFixed(avg < 100 ? 1 : 0)} (${Math.min(...vs).toFixed(0)}-${Math.max(...vs).toFixed(0)})`, ok, n });
      }
    }
    // Fastest T2 factory: the best opening on any map of the class.
    let fast = null;
    for (const { map, spawns } of tc.maps) for (const faction of factions) { const b = fastestT2(variant, map, spawns, faction); if (b && (!fast || b.t < fast.t)) fast = { ...b, map }; }
    const [lo, hi] = tc.fastestT2Factory;
    rows.push({ cls, style: 'rush', what: 'fastest T2 factory', want: `${fmtT(lo)}-${fmtT(hi)}`, got: fast ? `${fmtT(fast.t)} (${fast.opening})` : 'none', ok: fast ? ((lo == null || fast.t >= lo) && (hi == null || fast.t <= hi)) : true });
  }
  return rows;
}

// ---------------------------------------------------------------- commands

const factionsOf = f => (f === 'all' ? ['e', 'c', 'g'] : [f || 'e']);

if (cmd === 'dump') {
  const v = loadVariant(argv[0]);
  const out = flag('--out');
  if (out) fs.writeFileSync(out, JSON.stringify(v.dump));
  console.log(v.dump.meta.summary);
} else if (cmd === 'eco') {
  for (const name of argv) ecoReport(loadVariant(name));
} else if (cmd === 'sim') {
  const map = flag('--map'), spawns = flag('--spawns'), style = flag('--style'), faction = flag('--faction'), minutes = +(flag('--minutes') || 15), trace = !!flag('--trace');
  const v = loadVariant(argv[0]);
  for (const f of factionsOf(faction)) simReport(v, map, spawns && spawns.split(','), styleNames(style), f, minutes, trace);
} else if (cmd === 'rush') {
  const map = flag('--map'), spawns = flag('--spawns'), faction = flag('--faction');
  const v = loadVariant(argv[0]);
  for (const f of factionsOf(faction)) { const b = fastestT2(v, map, spawns && spawns.split(','), f); console.log(`${v.name} ${map} ${FACTION_TAG[f]}: fastest T2 factory ${b ? fmtT(b.t) + ' (' + b.opening + ')' : 'none in 10 min'}`); }
} else if (cmd === 'check' || cmd === 'diff') {
  const styles = styleNames(flag('--style')), factions = factionsOf(flag('--faction') || 'all');
  const results = argv.map(name => ({ name, rows: checkVariant(loadVariant(name), styles, factions) }));
  const base = results[0].rows;
  console.log(`\n${cmd === 'check' ? 'Check against targets.json' : 'Side by side'} (simulated; mean over maps, both spawns, ${factions.length} faction(s); range in brackets)`);
  console.log('class style  what                       target     ' + results.map(r => r.name.padEnd(30)).join(''));
  for (let i = 0; i < base.length; i++) {
    const b = base[i];
    console.log(`${b.cls.padEnd(5)} ${b.style.padEnd(6)} ${b.what.padEnd(26)} ${b.want.padEnd(10)} ` + results.map(r => `${r.rows[i].ok ? 'ok  ' : 'MISS'} ${r.rows[i].got}`.padEnd(30)).join(''));
  }
} else if (cmd === 'calibrate') {
  const [calPath, rsPath] = argv;
  const iterations = +(flag('--iterations') || 1500);
  const cal = readJson(calPath), rs = readJson(rsPath);
  const byStyle = {};
  for (const [replay, r] of Object.entries(rs)) for (const style of r.styles) (byStyle[style] ||= {})[replay] = { dump: loadVariant(r.variant).dump, rules: variants[r.variant].rules || {} };
  const out = { about: 'Stand-in players fitted by `balance.mjs calibrate` to replays read with the dev-only BalanceCal. survival10: combat units alive / built at 10:00 in those games.', styles: {} };
  for (const [style, rulesets] of Object.entries(byStyle)) {
    const cs = cases(cal, rulesets);
    const f = fit(cs, { iterations });
    const surv = [];
    for (const g of cal) if (rulesets[g.replay]) for (const p of g.players) { const m = p.minutes.find(x => x.minute === 10); if (m) surv.push(m.combatAlive / m.combatBuilt); }
    out.styles[style] = { policy: f.policy, survival10: +(sum(surv) / surv.length).toFixed(2), fittedOn: cs.map(c => c.label), error: +f.score.toFixed(3) };
    console.log(`style ${style}: error ${f.score.toFixed(3)} over ${cs.length} players; survival at 10:00 ${out.styles[style].survival10}`);
    for (const c of cs) {
      const sim = runCase(c, { ...DEFAULT_POLICY, ...f.policy });
      const s10 = sim[sim.length - 1], r10 = c.real.find(x => x.minute === s10.minute);
      console.log(`  ${c.label}: at ${s10.minute}:00 income ${s10.alloyIncome}/${r10.alloyIncome}, extractors ${sum(s10.mex)}/${sum(r10.mex)}, factories ${sum(s10.factories)}/${sum(r10.factories)}, units built ${s10.combatBuilt}/${r10.combatBuilt} (sim/real); error ${error(sim, c.real).toFixed(3)}`);
    }
  }
  fs.writeFileSync(playersPath, JSON.stringify(out, null, 1) + '\n');
  console.log(`wrote ${path.relative(process.cwd(), playersPath)}`);
} else {
  console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter(l => l.startsWith('//')).slice(0, 20).map(l => l.slice(3)).join('\n'));
}
