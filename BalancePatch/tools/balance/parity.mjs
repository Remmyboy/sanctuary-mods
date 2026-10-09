// Parity test: the LuaJIT dump (dump.lua running the mod's real patch.lua) against
// preview.mjs's JavaScript re-implementation of the same patch.
//
//   node BalancePatch/tools/balance/parity.mjs <dump.json> <vanilla-dump.json> <preview.json>
//
// preview.json is `node BalancePatch/tools/preview.mjs --json <file>`: the patched templates
// of every unit and projectile it changed. Those must match the dump field by field, and
// every other template must be untouched (equal to the vanilla dump).
import fs from 'fs';

const [dumpPath, vanillaPath, previewPath] = process.argv.slice(2);
const read = p => JSON.parse(fs.readFileSync(p, 'utf8'));
const dump = read(dumpPath), vanilla = read(vanillaPath), preview = read(previewPath);

// preview.mjs leaves presentation out of its export.
const SKIP = new Set(['audio', 'visuals', 'effects', 'beam']);

// Numbers compare to 1e-6; an empty Lua table dumps as {} where JS may have [].
function diff(a, b, at, out, skip) {
  if (out.length > 20) return;
  if (typeof a === 'number' && typeof b === 'number') { if (Math.abs(a - b) > 1e-6 * Math.max(1, Math.abs(a))) out.push(`${at}: ${a} vs ${b}`); return; }
  if (a && b && typeof a === 'object' && typeof b === 'object') {
    if (Array.isArray(a) !== Array.isArray(b) && Object.keys(a).length === 0 && Object.keys(b).length === 0) return;
    for (const k of new Set([...Object.keys(a), ...Object.keys(b)])) {
      if (skip && SKIP.has(k) && b[k] === undefined) continue;
      diff(a[k], b[k], `${at}.${k}`, out, skip);
    }
    return;
  }
  if (a !== b && !(a == null && b == null)) out.push(`${at}: ${JSON.stringify(a)} vs ${JSON.stringify(b)}`);
}

let compared = 0, bad = 0;
function check(label, a, b, skip) {
  const out = [];
  diff(a, b, label, out, skip);
  compared++;
  if (out.length) { bad++; console.log(out.slice(0, 8).join('\n')); }
}

for (const kind of ['units', 'projectiles']) {
  const changed = preview[kind] || {};
  for (const id of Object.keys(dump[kind])) {
    if (changed[id]) check(`${kind} ${id} (patched)`, dump[kind][id], changed[id], true);
    else check(`${kind} ${id} (untouched)`, dump[kind][id], vanilla[kind][id]);
  }
  for (const id of Object.keys(changed)) if (!dump[kind][id]) { bad++; console.log(`${kind} ${id}: in preview, not in the dump`); }
}
console.log(`${compared} templates compared, ${bad} differ`);
process.exit(bad ? 1 : 0);
