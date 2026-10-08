// Balance Patch preview and export: applies lua/balancepatch/changes.lua to the installed
// game's unit and projectile templates, the way lua/balancepatch/patch.lua does in game.
//
//   node BalancePatch/tools/preview.mjs                 every changed field, before -> after
//   node BalancePatch/tools/preview.mjs --changelog     CHANGELOG.md text: every change, line by
//                                                       line, factions with the same change grouped
//   node BalancePatch/tools/preview.mjs --json [file]   JSON for other tools (the unit db): every
//                                                       change, plus the patched templates
//   --game <LJ\lua folder>                              another game install
//
// Expectations that no longer hold are reported as SKIPPED, as the mod would skip them.
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const modDir = path.join(here, '..');
const args = process.argv.slice(2);
const arg = name => { const i = args.indexOf(name); return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : undefined; };
const steamApps = 'C:/Program Files (x86)/Steam/steamapps';
const game = arg('--game') || `${steamApps}/common/Sanctuary Shattered Sun Playtest/engine/LJ/lua`;

// ---- Lua table literals (.santp files and changes.lua) ----

function parseLiteral(src, top) {
  let i = 0;
  const ws = () => {
    for (;;) {
      while (/\s/.test(src[i])) i++;
      if (!src.startsWith('--', i)) break;
      if (src.startsWith('--[[', i)) i = src.indexOf(']]', i) + 2;
      else while (i < src.length && src[i] !== '\n') i++;
    }
  };
  const val = () => {
    ws();
    const c = src[i];
    if (c === '{') {
      i++;
      const obj = {}, arr = [];
      let isArr = true;
      for (;;) {
        ws();
        if (src[i] === '}') { i++; break; }
        const m = /^([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)/.exec(src.slice(i, i + 80));
        if (m) { i += m[0].length; obj[m[1]] = val(); isArr = false; }
        else if (src[i] === '[') { i++; const k = val(); ws(); i++; ws(); i++; obj[k] = val(); isArr = false; }
        else arr.push(val());
        ws();
        if (src[i] === ',' || src[i] === ';') i++;
      }
      if (isArr) return arr;
      arr.forEach((v, k) => { obj[k + 1] = v; });
      return obj;
    }
    if (c === '"' || c === "'") {
      let j = i + 1, s = '';
      while (src[j] !== c) { if (src[j] === '\\') { s += src[j + 1]; j += 2; } else s += src[j++]; }
      i = j + 1;
      return s;
    }
    let m = /^-?(0x[0-9a-fA-F]+|\d*\.?\d+(e[-+]?\d+)?)/.exec(src.slice(i, i + 40));
    if (m) { i += m[0].length; return Number(m[0]); }
    m = /^[A-Za-z_][A-Za-z0-9_.]*/.exec(src.slice(i, i + 80));
    if (m) { i += m[0].length; return m[0] === 'true' ? true : m[0] === 'false' ? false : m[0] === 'nil' ? null : { $expr: m[0] }; }
    throw new Error('bad token at ' + i + ': ' + src.slice(i, i + 30));
  };
  const at = src.search(new RegExp('^' + top + '\\s*=', 'm'));
  i = src.indexOf('=', at) + 1;
  return val();
}

// The game lower-cases template ids (PXD003 -> pxd003).
function loadTemplates(dir, top) {
  const out = {};
  for (const d of fs.readdirSync(dir)) {
    const f = path.join(dir, d, d + '.santp');
    if (fs.existsSync(f)) out[d.toLowerCase()] = parseLiteral(fs.readFileSync(f, 'utf8'), top);
  }
  return out;
}

// ---- Applying changes, as patch.lua does ----

// Lua arrays parse as JS arrays (0-based); paths use Lua's 1-based numbers.
const get = (t, k) => (Array.isArray(t) && typeof k === 'number' ? t[k - 1] : t?.[k]);
const put = (t, k, v) => { if (Array.isArray(t) && typeof k === 'number') t[k - 1] = v; else t[k] = v; };
const split = p => p.split('.').map(x => (/^\d+$/.test(x) ? Number(x) : x));
const keysOf = t => (Array.isArray(t) ? t.map((_, i) => i + 1) : Object.keys(t));
const matches = (child, where) => Object.entries(where || {}).every(([k, v]) => child[k] === v);
const clone = v => JSON.parse(JSON.stringify(v));

// Calls fn(table, key, label) for every field the path names; where filters the first "*".
function each(node, parts, i, fn, where, label) {
  const key = parts[i];
  if (i === parts.length - 1) {
    if (key === '*') for (const k of keysOf(node)) fn(node, k, label + '.' + k);
    else fn(node, key, label + '.' + key);
    return;
  }
  if (key === '*') {
    keysOf(node).forEach(k => {
      const child = get(node, k);
      if (child && typeof child === 'object' && child.category !== 'DeathExplosion' && matches(child, where))
        each(child, parts, i + 1, fn, undefined, label + '.' + k);
    });
  } else if (get(node, key) && typeof get(node, key) === 'object') {
    each(get(node, key), parts, i + 1, fn, where, label + '.' + key);
  }
}

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b) ||
  (typeof a === 'number' && typeof b === 'number' && Math.abs(a - b) < 1e-6);

function selects(c, tp, id) {
  const tags = tp.tags || [];
  if (c.ids && !c.ids.includes(id)) return false;
  if (c.idPattern && !new RegExp(c.idPattern.replace(/%\./g, '\\.')).test(id)) return false;
  if ((c.tags || []).some(t => !tags.includes(t))) return false;
  if ((c.notTags || []).some(t => tags.includes(t))) return false;
  return true;
}

const units = loadTemplates(path.join(game, 'common/units/unitsTemplates'), 'UnitTemplate');
const projectiles = loadTemplates(path.join(game, 'common/projectiles/projectilesTemplates'), 'ProjectileTemplate');
const original = { unit: clone(units), projectile: clone(projectiles) };
const Sections = parseLiteral(fs.readFileSync(path.join(modDir, 'lua/balancepatch/changes.lua'), 'utf8'), 'Sections');
const manifest = JSON.parse(fs.readFileSync(path.join(modDir, 'mod.json'), 'utf8'));
const optionOf = Object.fromEntries((manifest.options || []).map(o => [o.key, o]));

// One record per changed field.
const records = [];
const skipped = [];
Sections.forEach(section => {
  (section.changes || []).forEach((c, rule) => {
    const kind = c.kind || 'unit';
    const set = kind === 'projectile' ? projectiles : units;
    for (const [id, tp] of Object.entries(set)) {
      if (!selects(c, tp, id)) continue;
      const bad = Object.entries(c.expect || {}).filter(([p, want]) => {
        let ok = false;
        each(tp, split(p), 0, (t, k) => { ok = same(get(t, k), want); }, c.where, '');
        return !ok;
      });
      if (bad.length) { skipped.push({ section: section.key, id, fields: bad.map(b => b[0]), why: c.why }); continue; }
      const record = (field, before, after) => records.push({ section: section.key, rule, why: c.why, kind, id, field, before, after });
      for (const [p, v] of Object.entries(c.set || {}))
        each(tp, split(p), 0, (t, k, label) => {
          if (get(t, k) === undefined && p.includes('*')) return;
          const before = get(t, k);
          if (!same(before, v)) record(label.slice(1), before ?? null, clone(v));
          put(t, k, clone(v));
        }, c.where, '');
      for (const p of c.clear || [])
        each(tp, split(p), 0, (t, k, label) => {
          const before = get(t, k);
          if (before === undefined) return;
          record(label.slice(1), before, null);
          if (Array.isArray(t)) t[k - 1] = undefined; else delete t[k];
        }, c.where, '');
      for (const [p, f] of Object.entries(c.scale || {}))
        each(tp, split(p), 0, (t, k, label) => {
          const before = get(t, k);
          if (typeof before !== 'number') return;
          let v = before * f;
          if (c.round) v = Math.floor(v + 0.5);
          if (!same(before, v)) record(label.slice(1), before, v);
          put(t, k, v);
        }, c.where, '');
    }
  });
});

// ---- Names and labels ----

const FACTION = { e: 'EDA', c: 'Chosen', g: 'Guardian', w: 'Guardian' };
const FACTION_ORDER = ['EDA', 'Chosen', 'Guardian'];
const PROJECTILE_NAMES = { pei141: 'EDA and Guardian commander missile' };

function unitInfo(kind, id) {
  if (kind === 'projectile') return { kind: PROJECTILE_NAMES[id] || `Projectile ${id}`, nick: null, faction: null };
  const g = original.unit[id]?.general || {};
  return { kind: (g.displayName || id).replace(/^Tier (\d): /, 'T$1 '), nick: g.name || null, faction: FACTION[id[1]] || null };
}

// "weapon 2 damage" only when the unit has more than one real weapon.
const weaponCount = id => (original.unit[id]?.weapons || []).filter(w => w.category !== 'DeathExplosion').length;

const WEAPON_FIELDS = {
  damage: 'damage', damageRadius: 'splash radius', reloadTime: 'reload (s)', rangeMax: 'range',
  projectileLifetime: 'projectile lifetime (s)', layerTargetLimits: 'targets', projectileTemplate: 'projectile',
};
const AIM_FIELDS = {
  leadTarget: 'leads its target', aimTolerance: 'aim tolerance (deg)', projectileSpeed: 'muzzle speed',
  pitchBone: 'pitch bone', yawBone: 'yaw bone',
};
const STAGE_FIELDS = { speedMax: 'top speed', acceleration: 'acceleration', rotationSpeed: 'turn rate (deg/s)', delay: 'starts at (s)' };
const PLAIN = {
  'economy.cost.alloys': 'alloys', 'economy.cost.energy': 'energy', 'economy.production': 'income',
  'economy.production.alloys': 'alloys/s', 'economy.production.energy': 'energy/s', 'defence.health.max': 'health',
  'intel.visionRadius': 'vision', 'movement.speed': 'speed', 'general.displayName': 'name', tags: 'tags',
  'movement.type': 'homing', 'movement.speedMax': 'top speed', 'movement.acceleration': 'acceleration',
};

function fieldLabel(r) {
  if (PLAIN[r.field]) return PLAIN[r.field];
  const weapon = n => (r.kind === 'unit' && weaponCount(r.id) > 1 ? `weapon ${n} ` : '');
  let m = /^weapons\.(\d+)\.aimControllers\.\d+\.(\w+)$/.exec(r.field);
  if (m) return AIM_FIELDS[m[2]] || m[2];
  m = /^weapons\.(\d+)\.muzzleGroups/.exec(r.field);
  if (m) return weapon(m[1]) + 'muzzles';
  m = /^weapons\.(\d+)\.(\w+)$/.exec(r.field);
  if (m) return weapon(m[1]) + (WEAPON_FIELDS[m[2]] || m[2]);
  m = /^extraStages\.(\d+)\.(\w+)$/.exec(r.field);
  if (m) return `stage ${m[1]} ${STAGE_FIELDS[m[2]] || m[2]}`;
  return r.field;
}

const num = v => (Number.isInteger(v) ? String(v) : String(Math.round(v * 100) / 100));
function show(v) {
  if (v === null || v === undefined) return '(none)';
  if (typeof v === 'boolean') return v ? 'yes' : 'no';
  if (typeof v === 'number') return num(v);
  if (Array.isArray(v)) return v.length ? v.join(', ') : '(empty)';
  if (typeof v === 'object') return Object.entries(v).map(([k, x]) => `${num(x)} ${k}/s`).join(' + ');
  return String(v);
}

// A unit's changes as "field before -> after" items, folding duplicates: health.value mirrors
// health.max, and one "leads its target" stands for every weapon and aim controller.
function itemsOf(rs) {
  const items = [];
  for (const r of rs) {
    if (r.field === 'defence.health.value') continue;
    let text;
    if (r.field === 'tags') {
      const gone = (r.before || []).filter(t => !(r.after || []).includes(t));
      const added = (r.after || []).filter(t => !(r.before || []).includes(t));
      text = `tag ${gone.join(', ')} -> ${added.join(', ')}`;
    } else {
      const label = fieldLabel(r);
      text = r.after === null ? `${label} ${show(r.before)} removed` : `${label} ${show(r.before)} -> ${show(r.after)}`;
    }
    if (!items.includes(text)) items.push(text);
  }
  return items;
}

function steamBuild() {
  try {
    return /"buildid"\s+"(\d+)"/.exec(fs.readFileSync(`${steamApps}/appmanifest_4511930.acf`, 'utf8'))?.[1] ?? null;
  } catch { return null; }
}

// ---- Outputs ----

function preview() {
  for (const section of Sections) {
    const rs = records.filter(r => r.section === section.key);
    if (!rs.length) continue;
    console.log(`\n=== ${section.key} (${rs.length} fields)`);
    for (const r of rs) {
      const u = unitInfo(r.kind, r.id);
      const name = `${r.id} ${u.nick ? u.nick + ' ' : ''}(${u.kind})`;
      console.log(`  ${name.padEnd(48)} ${r.field.padEnd(42)} ${show(r.before)} -> ${show(r.after)}`);
    }
  }
  for (const s of skipped) console.log(`SKIPPED ${s.section} ${s.id} ${s.fields.join(', ')}: the game's value changed`);
}

function changelog() {
  const out = [`# ${manifest.name} ${manifest.version}`, ''];
  out.push(`Every change against game ${manifest.gameVersion} (Steam build ${steamBuild() || 'unknown'}). Each section`);
  out.push('is a lobby option, on by default. Generated by `node BalancePatch/tools/preview.mjs --changelog`.', '');
  for (const section of Sections) {
    const rs = records.filter(r => r.section === section.key);
    if (!rs.length) continue;
    out.push(`## ${optionOf[section.key]?.label || section.key}`, '');
    for (const rule of [...new Set(rs.map(r => r.rule))].sort((a, b) => a - b)) {
      const rr = rs.filter(r => r.rule === rule);
      out.push(`*${rr[0].why}*`, '');
      // Each unit's items, then units of the same kind (u?l1001) with identical items grouped.
      const byUnit = new Map();
      for (const r of rr) {
        const key = `${r.kind}:${r.id}`;
        if (!byUnit.has(key)) byUnit.set(key, []);
        byUnit.get(key).push(r);
      }
      const groups = new Map();
      for (const urs of byUnit.values()) {
        const { kind, id } = urs[0];
        const items = itemsOf(urs);
        if (!items.length) continue;
        const gkey = `${kind === 'projectile' ? id : id.replace(/^u./, 'u?')}|${items.join(';')}`;
        if (!groups.has(gkey)) groups.set(gkey, { info: unitInfo(kind, id), items, members: [] });
        groups.get(gkey).members.push({ id, ...unitInfo(kind, id) });
      }
      const sorted = [...groups.values()].sort((a, b) =>
        a.members[0].id.slice(2).localeCompare(b.members[0].id.slice(2)) || a.members[0].id.localeCompare(b.members[0].id));
      for (const g of sorted) {
        g.members.sort((a, b) => FACTION_ORDER.indexOf(a.faction) - FACTION_ORDER.indexOf(b.faction));
        let who;
        if (!g.members[0].faction) who = `**${g.info.kind}**`;
        else if (g.members.every(m => !m.nick)) who = `**${g.info.kind}** (${g.members.length === 3 ? 'all factions' : g.members.map(m => m.faction).join(', ')})`;
        else who = `**${g.info.kind}** ${g.members.map(m => `${m.nick || m.id} (${m.faction})`).join(', ')}`;
        out.push(`- ${who}: ${g.items.join('; ')}`);
      }
      out.push('');
    }
    if (section.key === 'costs') {
      out.push('*The stock AI builds generators until its energy income is a set multiple of its alloy income, a target made for units costing 10-20 energy per alloy.*', '');
      out.push('- **AI**: energy-to-alloy income targets x0.7 (20 -> 14 early; 13-15 -> 9.1-10.5 later)', '');
    }
  }
  if (skipped.length) {
    out.push('## Skipped', '', 'Made against numbers the installed game no longer has, so the mod leaves them out:', '');
    for (const s of skipped) out.push(`- ${s.id}: ${s.fields.join(', ')}`);
    out.push('');
  }
  process.stdout.write(out.join('\n'));
}

// Templates trimmed to what matters for stats: no visuals or sounds.
function trimmed(tp) {
  const t = clone(tp);
  delete t.visuals;
  delete t.audio;
  for (const w of t.weapons || []) { delete w.effects; delete w.audio; delete w.beam; }
  return t;
}

function exportJson(file) {
  const changedUnits = [...new Set(records.filter(r => r.kind === 'unit').map(r => r.id))].sort();
  const changedProjectiles = [...new Set(records.filter(r => r.kind === 'projectile').map(r => r.id))].sort();
  const data = {
    format: 'sanctuary-balance-patch/1',
    about: 'Every change the Balance Patch makes, with the patched template of every unit and projectile it changes, '
      + 'in the shape of the game\'s .santp tables minus visuals and sounds. Lua arrays are JSON arrays, but field '
      + 'paths in "changes" count array items from 1, as Lua does. Values are as with every section on (the lobby default); '
      + 'each change names its section so a viewer can show one section at a time. A field two sections change '
      + '(the land cost shift, then a unit\'s own tuning) appears once per change, in order: the last "after" is the final value, '
      + 'as in "units".',
    mod: { id: manifest.id, name: manifest.name, version: manifest.version },
    game: { version: manifest.gameVersion, steamBuild: steamBuild() },
    generated: new Date().toISOString(),
    sections: Sections.map(s => ({ key: s.key, label: optionOf[s.key]?.label || s.key, description: optionOf[s.key]?.description || '' })),
    changes: records.map(r => {
      const u = unitInfo(r.kind, r.id);
      return {
        section: r.section, kind: r.kind, id: r.id, name: u.nick,
        displayName: original[r.kind][r.id]?.general?.displayName ?? null, faction: u.faction,
        field: r.field, label: fieldLabel(r), before: r.before, after: r.after, why: r.why,
      };
    }),
    skipped,
    notes: ['AI: the stock AI\'s energy-to-alloy income targets (LessThan/MoreThanEnergyToResourceRatioIncome in AI/AIFunctions.lua) are scaled by 0.7 when the "costs" section is on. Not a template change.'],
    units: Object.fromEntries(changedUnits.map(id => [id, trimmed(units[id])])),
    projectiles: Object.fromEntries(changedProjectiles.map(id => [id, trimmed(projectiles[id])])),
  };
  fs.writeFileSync(file, JSON.stringify(data, null, 1) + '\n');
  console.log(`${file}: ${records.length} changed fields on ${changedUnits.length} units and ${changedProjectiles.length} projectiles`);
}

if (args.includes('--changelog')) changelog();
else if (args.includes('--json')) exportJson(arg('--json') || path.join(modDir, 'balancepatch.json'));
else preview();
