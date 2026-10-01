#!/usr/bin/env node
// Past Claude Code sessions for this repo, read from ~/.claude/projects.
// Every worktree gets its own project folder, so a repo's history is spread
// over C--code-sanctuary-hud and every C--code-sanctuary-hud--claude-worktrees-*.
//
//   node tools/history.mjs list [text]            sessions, newest first (title, branch, dates, size)
//   node tools/history.mjs search <regex> [-n N]  matching lines, per session
//   node tools/history.mjs show <id-prefix>       one session as readable text
//   node tools/history.mjs condense <outdir>      every session to <outdir>/<date>-<title>.md
//
// Options: --project <prefix> (default: the folder name of the main checkout,
// e.g. C--code-sanctuary-hud), --full (show/condense: don't truncate tool
// output), --since YYYY-MM-DD.
//
// The condensed form keeps the user's messages and Claude's text whole, tool
// calls as one line each and tool output cut to a few hundred characters:
// a 20 MB transcript comes out at a few hundred KB.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const argv = process.argv.slice(2);
const flag = (name) => { const i = argv.indexOf(name); if (i < 0) return undefined; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const bool = (name) => { const i = argv.indexOf(name); if (i < 0) return false; argv.splice(i, 1); return true; };

const full = bool('--full');
const since = flag('--since');
const maxHits = Number(flag('-n') ?? 5);
let project = flag('--project');
if (!project) {
  // C:\code\sanctuary-hud\.claude\worktrees\x\tools -> C--code-sanctuary-hud
  let repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const wt = repo.split(path.sep).indexOf('.claude');
  if (wt > 0) repo = repo.split(path.sep).slice(0, wt).join(path.sep);
  project = repo.replace(/[:\\/.]/g, '-');
}
const [cmd, ...rest] = argv;
const root = path.join(os.homedir(), '.claude', 'projects');

function sessionFiles() {
  const dirs = fs.readdirSync(root).filter((d) => d === project || d.startsWith(project + '--'));
  return dirs.flatMap((d) => fs.readdirSync(path.join(root, d))
    .filter((f) => f.endsWith('.jsonl'))
    .map((f) => path.join(root, d, f)));
}

function readLines(file) {
  const out = [];
  for (const l of fs.readFileSync(file, 'utf8').split('\n')) {
    if (!l) continue;
    try { out.push(JSON.parse(l)); } catch { /* a line cut off mid-write */ }
  }
  return out;
}

function meta(file) {
  const lines = readLines(file);
  let title, first, last, branch, cwd, prompt;
  for (const o of lines) {
    if (o.type === 'custom-title' && o.customTitle) title = o.customTitle;
    if (o.timestamp) { first ??= o.timestamp; last = o.timestamp; }
    if (o.gitBranch && o.gitBranch !== 'HEAD') branch ??= o.gitBranch;
    if (o.cwd) cwd ??= o.cwd;
    if (!prompt && o.type === 'user' && !o.isSidechain) {
      const t = userText(o.message?.content);
      if (t) prompt = t;
    }
  }
  return {
    file, id: path.basename(file, '.jsonl'), lines,
    title: title ?? (prompt ?? '(untitled)').slice(0, 60).replace(/\s+/g, ' '),
    first, last, branch, cwd, prompt,
    size: fs.statSync(file).size,
  };
}

const stripReminders = (s) => s
  .replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, '')
  .replace(/<pasted_content[^>]*>([\s\S]*?)<\/pasted_content>/g, (_, p) => `[pasted:\n${p.trim()}\n]`)
  .trim();

function userText(content) {
  if (typeof content === 'string') return stripReminders(content);
  if (!Array.isArray(content)) return '';
  return content.filter((c) => c.type === 'text').map((c) => stripReminders(c.text)).filter(Boolean).join('\n');
}

const cut = (s, n) => (full || s.length <= n ? s : s.slice(0, n) + ` …[+${s.length - n} chars]`);
const one = (s) => s.replace(/\s+/g, ' ').trim();

function toolLine(c) {
  const i = c.input ?? {};
  const name = c.name.replace(/^mcp__[^_]+(?:_[^_]+)*?__/, 'mcp:');
  const arg = i.description ? `${i.description} :: ${i.command ?? ''}`
    : i.command ?? i.file_path ?? i.pattern ?? i.query ?? i.prompt ?? i.url ?? i.skill ?? JSON.stringify(i);
  return `  → ${name}: ${cut(one(String(arg)), 300)}`;
}

function resultText(c) {
  const body = typeof c.content === 'string' ? c.content
    : Array.isArray(c.content) ? c.content.map((x) => x.type === 'text' ? x.text : `[${x.type}]`).join('\n') : '';
  return `    ${c.is_error ? '✗' : '←'} ${cut(one(body), c.is_error ? 800 : 300)}`;
}

function render(m) {
  const out = [`# ${m.title}`, '',
    `- session: ${m.id}`, `- branch: ${m.branch ?? '?'}`, `- cwd: ${m.cwd ?? '?'}`,
    `- from ${m.first} to ${m.last}`, ''];
  for (const o of m.lines) {
    if (o.isSidechain) continue;
    const content = o.message?.content;
    if (o.type === 'user') {
      if (Array.isArray(content) && content.some((c) => c.type === 'tool_result')) {
        for (const c of content) if (c.type === 'tool_result') out.push(resultText(c));
        continue;
      }
      const t = userText(content);
      if (t) out.push('', `## USER (${(o.timestamp ?? '').slice(0, 16)})`, t, '');
    } else if (o.type === 'assistant' && Array.isArray(content)) {
      for (const c of content) {
        if (c.type === 'text' && c.text.trim()) out.push('', '## CLAUDE', c.text.trim(), '');
        else if (c.type === 'tool_use') out.push(toolLine(c));
      }
    } else if (o.type === 'system' && o.content && /compact|summary/i.test(o.subtype ?? '')) {
      out.push('', `## (context compacted)`, '');
    }
  }
  return out.join('\n');
}

function all() {
  return sessionFiles().map(meta)
    .filter((m) => !since || (m.last ?? '') >= since)
    .sort((a, b) => (b.last ?? '').localeCompare(a.last ?? ''));
}

switch (cmd) {
  case 'list': {
    const q = rest.join(' ').toLowerCase();
    for (const m of all()) {
      if (q && !`${m.title} ${m.branch} ${m.prompt}`.toLowerCase().includes(q)) continue;
      console.log(`${(m.last ?? '').slice(0, 10)}  ${m.id.slice(0, 8)}  ${String(Math.round(m.size / 1024)).padStart(6)}K  ${m.title}  [${m.branch ?? '?'}]`);
    }
    break;
  }
  case 'search': {
    const re = new RegExp(rest.join(' '), 'i');
    for (const m of all()) {
      const hits = render(m).split('\n').filter((l) => re.test(l));
      if (!hits.length) continue;
      console.log(`\n== ${(m.last ?? '').slice(0, 10)} ${m.id.slice(0, 8)} ${m.title} (${hits.length} hits)`);
      for (const h of hits.slice(0, maxHits)) console.log('   ' + cut(h.trim(), 240));
    }
    break;
  }
  case 'show': {
    const m = all().find((x) => x.id.startsWith(rest[0]));
    if (!m) { console.error('no session ' + rest[0]); process.exit(1); }
    console.log(render(m));
    break;
  }
  case 'condense': {
    const outDir = rest[0] ?? 'history';
    fs.mkdirSync(outDir, { recursive: true });
    for (const m of all()) {
      const slug = m.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 50);
      const name = `${(m.first ?? "0000-00-00").slice(0, 10)}-${slug}-${m.id.slice(0, 8)}.md`;
      const text = render(m);
      fs.writeFileSync(path.join(outDir, name), text);
      console.log(`${String(Math.round(text.length / 1024)).padStart(6)}K  ${name}`);
    }
    break;
  }
  default:
    console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 19).map((l) => l.replace(/^\/\/ ?/, '')).join('\n'));
}
