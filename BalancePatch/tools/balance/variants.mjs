// Named rulesets (variants.json) and their dumps: templates with the patch applied by the mod's
// own patch.lua under the game's LuaJIT (dump.lua via tools/GameRef luarun), cached in .cache/.
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { execFileSync } from 'child_process';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const modDir = path.resolve(here, '../..');
const GAME = 'C:/Program Files (x86)/Steam/steamapps/common/Sanctuary Shattered Sun Playtest/engine';
const LUA_DLL = `${GAME}/Sanctuary_Data/Plugins/x86_64/lua51.dll`;
const readJson = f => JSON.parse(fs.readFileSync(f, 'utf8'));


export const variants = readJson(path.join(here, 'variants.json'));
const manifest = readJson(path.join(modDir, 'mod.json'));

export function optionString(spec) {
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

