// Balance Patch preview: applies lua/balancepatch/changes.lua to the installed game's unit and
// projectile templates, the way lua/balancepatch/patch.lua does in game, and prints every
// changed field as before -> after, grouped by section.
//
//   node BalancePatch/tools/preview.mjs [--md] [--game <LJ\lua folder>]
//
// --md prints Markdown tables (for the changelog). Expectations that no longer hold are listed
// as SKIPPED, as the mod would skip them.
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const md = args.includes('--md');
const gi = args.indexOf('--game');
const game = gi >= 0 ? args[gi + 1]
  : 'C:/Program Files (x86)/Steam/steamapps/common/Sanctuary Shattered Sun Playtest/engine/LJ/lua';

// A parser for the Lua table literals in .santp files and changes.lua.
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

function loadTemplates(dir, top) {
  const out = {};
  for (const d of fs.readdirSync(dir)) {
    const f = path.join(dir, d, d + '.santp');
    if (fs.existsSync(f)) out[d.toLowerCase()] = parseLiteral(fs.readFileSync(f, 'utf8'), top);
  }
  return out;
}

// Lua arrays parse as JS arrays (0-based); paths use Lua's 1-based numbers.
const get = (t, k) => Array.isArray(t) && typeof k === 'number' ? t[k - 1] : t?.[k];
const put = (t, k, v) => { if (Array.isArray(t) && typeof k === 'number') t[k - 1] = v; else t[k] = v; };
const split = p => p.split('.').map(x => (/^\d+$/.test(x) ? Number(x) : x));
const keysOf = t => (Array.isArray(t) ? t.map((_, i) => i + 1) : Object.keys(t));
const matches = (child, where) => Object.entries(where || {}).every(([k, v]) => child[k] === v);

// Calls fn(table, key, label) for every field the path names, as patch.lua's each() does.
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
const show = v => (typeof v === 'object' && v !== null ? JSON.stringify(v) : String(v));
const tags = tp => tp.tags || [];

function selects(c, tp, id) {
  if (c.ids && !c.ids.includes(id)) return false;
  if (c.idPattern && !new RegExp(c.idPattern.replace(/%\./g, '\\.').replace(/\./g, '.')).test(id)) return false;
  if ((c.tags || []).some(t => !tags(tp).includes(t))) return false;
  if ((c.notTags || []).some(t => tags(tp).includes(t))) return false;
  return true;
}

const units = loadTemplates(path.join(game, 'common/units/unitsTemplates'), 'UnitTemplate');
const projectiles = loadTemplates(path.join(game, 'common/projectiles/projectilesTemplates'), 'ProjectileTemplate');
const { Sections } = { Sections: parseLiteral(fs.readFileSync(path.join(here, '../lua/balancepatch/changes.lua'), 'utf8'), 'Sections') };

// Labels come from the templates as the game ships them, before any change.
const names = {};
for (const [id, tp] of [...Object.entries(units), ...Object.entries(projectiles)])
  names[id] = `${id} ${tp.general?.name || ''} (${(tp.general?.displayName || '').replace(/^Tier (\d): /, 'T$1 ')})`
    .replace(/  +/g, ' ').replace(' ()', '');
const nameOf = (tp, id) => names[id];

for (const section of Sections) {
  const lines = [];
  for (const c of section.changes || []) {
    const set = c.kind === 'projectile' ? projectiles : units;
    for (const [id, tp] of Object.entries(set)) {
      if (!selects(c, tp, id)) continue;
      const bad = Object.entries(c.expect || {}).filter(([p, want]) => {
        let ok = false;
        each(tp, split(p), 0, (t, k) => { ok = same(get(t, k), want); }, c.where, '');
        return !ok;
      });
      if (bad.length) { lines.push({ id: nameOf(tp, id), field: bad.map(b => b[0]).join(', '), from: 'SKIPPED', to: 'expectation no longer holds', why: c.why }); continue; }
      const record = (t, k, label, v) => { const from = get(t, k); if (!same(from, v)) lines.push({ id: nameOf(tp, id), field: label.slice(1), from: show(from), to: show(v), why: c.why }); put(t, k, v); };
      for (const [p, v] of Object.entries(c.set || {}))
        each(tp, split(p), 0, (t, k, label) => { if (get(t, k) !== undefined || !p.includes('*')) record(t, k, label, JSON.parse(JSON.stringify(v))); }, c.where, '');
      for (const p of c.clear || [])
        each(tp, split(p), 0, (t, k, label) => { const from = get(t, k); if (from !== undefined) { lines.push({ id: nameOf(tp, id), field: label.slice(1), from: show(from), to: "(removed)", why: c.why }); if (Array.isArray(t)) t[k - 1] = undefined; else delete t[k]; } }, c.where, '');
      for (const [p, f] of Object.entries(c.scale || {}))
        each(tp, split(p), 0, (t, k, label) => { const v0 = get(t, k); if (typeof v0 === 'number') { let v = v0 * f; if (c.round) v = Math.floor(v + 0.5); record(t, k, label, v); } }, c.where, '');
    }
  }
  if (!lines.length) continue;
  if (md) {
    console.log(`\n## ${section.key}\n`);
    let why = null;
    for (const l of lines) {
      if (l.why !== why) { why = l.why; console.log(`\n${why}\n\n| Unit | Field | Before | After |\n| --- | --- | --- | --- |`); }
      console.log(`| ${l.id} | ${l.field} | ${l.from} | ${l.to} |`);
    }
  } else {
    console.log(`\n=== ${section.key} (${lines.length} fields)`);
    for (const l of lines) console.log(`  ${l.id.padEnd(44)} ${l.field.padEnd(38)} ${l.from} -> ${l.to}`);
  }
}
