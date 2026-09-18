'use strict';
// Window listing / focusing. Compiles src/WinCtl.cs once with the .NET
// Framework csc.exe (always present on Windows) and shells out to it.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

const SRC = path.join(__dirname, '..', 'src', 'WinCtl.cs');
const CSC = '/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe';

let exePath = null;

function ensureBuilt(cache) {
  if (exePath) return exePath;
  const body = fs.readFileSync(SRC);
  const hash = crypto.createHash('sha1').update(body).digest('hex');
  const stamp = path.join(cache.unix, 'winctl.sha1');
  const exe = path.join(cache.unix, 'WinCtl.exe');
  let built = '';
  try { built = fs.readFileSync(stamp, 'utf8').trim(); } catch {}
  if (built !== hash || !fs.existsSync(exe)) {
    if (!fs.existsSync(CSC)) throw new Error('csc.exe not found — window switching unavailable');
    fs.writeFileSync(path.join(cache.unix, 'WinCtl.cs'), body);
    execFileSync(CSC, ['/nologo', '/target:exe', '/out:' + cache.win + '\\WinCtl.exe',
                       cache.win + '\\WinCtl.cs'], { cwd: '/mnt/c', stdio: 'pipe' });
    fs.writeFileSync(stamp, hash);
  }
  exePath = exe;
  return exePath;
}

function parse(line) {
  const f = line.split('\t');
  if (f.length < 9) return null;
  return {
    hwnd: f[0], pid: +f[1], proc: f[2],
    x: +f[3], y: +f[4], w: +f[5], h: +f[6],
    focused: f[7] === '1',
    title: f.slice(8).join('\t'),
  };
}

function run(cache, args) {
  const exe = ensureBuilt(cache);
  let out;
  try {
    out = execFileSync(exe, args, { cwd: '/mnt/c', encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (e) {
    out = e.stdout || '';
  }
  return out.split('\n').map((l) => parse(l.replace(/\r$/, ''))).filter(Boolean);
}

// Real, switchable windows only — drop zero-size and off-screen placeholders.
const list = (cache) => run(cache, ['list']).filter((win) => win.w > 120 && win.h > 60);
const foreground = (cache) => run(cache, ['fg'])[0] || null;
const focus = (cache, hwnd) => run(cache, ['focus', String(hwnd)])[0] || null;

function match(windows, needle) {
  const n = needle.toLowerCase();
  return windows.find((win) => win.title.toLowerCase().includes(n)) ||
         windows.find((win) => win.proc.toLowerCase().includes(n)) || null;
}

module.exports = { list, foreground, focus, match };
