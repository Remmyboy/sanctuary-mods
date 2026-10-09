// Alloy spots and spawns from the installed game's .sanmap files (JSON): spots are
// markers.Alloys.transforms, spawns the ARMY_n / Army_n markers (either case).
import fs from 'fs';
import path from 'path';

export const MAPS_DIR = 'C:/Program Files (x86)/Steam/steamapps/common/Sanctuary Shattered Sun Playtest/engine/Sanctuary_Data/Maps';
const cache = new Map();

export function listMaps(dir = MAPS_DIR) {
  return fs.readdirSync(dir).filter(n => fs.existsSync(path.join(dir, n, n + '.sanmap')));
}

export function readMap(name, dir = MAPS_DIR) {
  if (cache.has(name)) return cache.get(name);
  const file = path.join(dir, name, name + '.sanmap');
  const d = JSON.parse(fs.readFileSync(file, 'utf8'));
  const spots = Object.values(d.markers?.Alloys?.transforms || {}).map(t => ({ x: t.position.x, z: t.position.z }));
  const spawns = {};
  const look = o => {
    if (!o || typeof o !== 'object') return;
    for (const [k, v] of Object.entries(o)) {
      if (/^army_\d+$/i.test(k) && v?.position) spawns[k.toUpperCase()] = { x: v.position.x, z: v.position.z };
      else look(v);
    }
  };
  look(d.markers);
  const map = { name, width: d.width, length: d.length, spots, spawns };
  cache.set(name, map);
  return map;
}

// The spots a player at `spawn` holds in a game between `spawns`: the ones nearer to it than
// to any other spawn in play, nearest first, with their distance from the spawn.
export function territory(map, spawn, spawns) {
  return map.spots
    .map(s => ({ ...s, d: Math.hypot(s.x - spawn.x, s.z - spawn.z) }))
    .filter(s => spawns.every(o => o === spawn || Math.hypot(s.x - o.x, s.z - o.z) >= s.d))
    .sort((a, b) => a.d - b.d);
}
