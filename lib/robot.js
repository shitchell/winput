'use strict';
// Shared bootstrap: locate a Windows JDK, keep a compiled RobotServer next to
// it on the Windows side, and hand back a live server with a line protocol.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');

const SRC = path.join(__dirname, '..', 'src', 'RobotServer.java');

// java.exe must be given as a full /mnt/c path: a non-interactive ssh session
// does not necessarily carry the Windows PATH entries.
function findJava() {
  if (process.env.WINPUT_JAVA) return process.env.WINPUT_JAVA;
  const roots = [
    '/mnt/c/Program Files/Microsoft',
    '/mnt/c/Program Files/Java',
    '/mnt/c/Program Files/Eclipse Adoptium',
    '/mnt/c/Program Files/Amazon Corretto',
    '/mnt/c/Program Files/Zulu',
    '/mnt/c/Program Files (x86)/Java',
  ];
  const found = [];
  for (const root of roots) {
    let entries;
    try { entries = fs.readdirSync(root); } catch { continue; }
    for (const e of entries) {
      const java = path.join(root, e, 'bin', 'java.exe');
      if (!fs.existsSync(java)) continue;
      const version = parseInt((e.match(/(\d+)/) || [0, 0])[1], 10);
      found.push({ java, version, jdk: fs.existsSync(path.join(root, e, 'bin', 'javac.exe')) });
    }
  }
  if (!found.length) {
    throw new Error('no java.exe found under Program Files — set WINPUT_JAVA to its path');
  }
  // A JDK beats a JRE (we can precompile); otherwise newest wins.
  found.sort((a, b) => (b.jdk - a.jdk) || (b.version - a.version));
  return found[0].java;
}

// Windows-side scratch dir, as both a WSL path and a Windows path.
function winCache() {
  const stampFile = path.join(os.homedir(), '.cache', 'winput', 'localappdata');
  let win;
  try { win = fs.readFileSync(stampFile, 'utf8').trim(); } catch {}
  if (!win) {
    win = execFileSync('/mnt/c/Windows/System32/cmd.exe', ['/c', 'echo %LOCALAPPDATA%'],
                       { encoding: 'utf8', cwd: '/mnt/c' }).trim();
    fs.mkdirSync(path.dirname(stampFile), { recursive: true });
    fs.writeFileSync(stampFile, win);
  }
  win = win.replace(/\\+$/, '') + '\\winput';
  const unix = execFileSync('wslpath', ['-u', win], { encoding: 'utf8' }).trim();
  fs.mkdirSync(unix, { recursive: true });
  return { win, unix };
}

// The .class lives on the Windows filesystem so the JVM never reaches back
// across the WSL share to load it. Rebuilt only when the source hash changes.
function ensureBuilt(java) {
  const body = fs.readFileSync(SRC);
  const hash = crypto.createHash('sha1').update(body).digest('hex');
  const { win, unix } = winCache();
  const stamp = path.join(unix, 'build.sha1');
  let built = '';
  try { built = fs.readFileSync(stamp, 'utf8').trim(); } catch {}
  if (built === hash && fs.existsSync(path.join(unix, 'RobotServer.class'))) {
    return { win, mode: 'class' };
  }
  fs.writeFileSync(path.join(unix, 'RobotServer.java'), body);
  const javac = java.replace(/java\.exe$/, 'javac.exe');
  if (fs.existsSync(javac)) {
    execFileSync(javac, ['-d', win, win + '\\RobotServer.java'], { cwd: '/mnt/c', stdio: 'pipe' });
    fs.writeFileSync(stamp, hash);
    return { win, mode: 'class' };
  }
  return { win, mode: 'source' }; // JRE only: single-file source launch
}

// Stands in for the JVM so the client's input parsing can be exercised
// without delivering anything to the real desktop. WINPUT_DRYRUN=1.
const MOCK = `
let buf = '';
process.stdout.write('ready\\n');
process.stdin.on('data', (d) => {
  buf += d;
  let n;
  while ((n = buf.indexOf('\\n')) >= 0) {
    const line = buf.slice(0, n); buf = buf.slice(n + 1);
    process.stderr.write('SENT: ' + line + '\\n');
    if (line === '?screens') process.stdout.write('= screen 0 \\\\Display0 0 0 1536 864 primary\\n= screen 1 \\\\Display1 0 -1080 1920 1080\\nok\\n');
    else if (line === '?pointer') process.stdout.write('= pointer 100 100\\nok\\n');
    else if (line === 'q') process.exit(0);
    else process.stdout.write('ok\\n');
  }
});
`;

function start(opts = {}) {
  let proc;
  if (process.env.WINPUT_DRYRUN) {
    proc = spawn(process.execPath, ['-e', MOCK], { stdio: ['pipe', 'pipe', 'pipe'] });
  } else {
    const java = findJava();
    const { win, mode } = ensureBuilt(java);
    const args = ['-Dwinput.delay=' + (opts.delay == null ? 8 : opts.delay)];
    if (mode === 'class') args.push('-cp', win, 'RobotServer');
    else args.push(win + '\\RobotServer.java');
    proc = spawn(java, args, { cwd: '/mnt/c', stdio: ['pipe', 'pipe', 'pipe'] });
  }
  let stdoutBuf = '';
  let booted = false;
  let onBoot = null;
  const pending = [];

  proc.stdout.setEncoding('utf8');
  proc.stdout.on('data', (chunk) => {
    stdoutBuf += chunk;
    let nl;
    while ((nl = stdoutBuf.indexOf('\n')) >= 0) {
      const line = stdoutBuf.slice(0, nl).replace(/\r$/, '');
      stdoutBuf = stdoutBuf.slice(nl + 1);
      if (!booted) { booted = true; if (onBoot) onBoot(); continue; }
      if (line.startsWith('= ')) { if (pending[0]) pending[0].lines.push(line.slice(2)); continue; }
      const p = pending.shift();
      if (p) p.resolve({ lines: p.lines, status: line });
    }
  });

  const server = {
    proc,
    // Every command answers with exactly one ok/err line, so responses pair up
    // with sends in order. Never rejects - callers may fire and forget.
    send(line) {
      return new Promise((resolve) => {
        if (proc.killed || !proc.stdin.writable) return resolve({ lines: [], status: 'err closed' });
        if (line === 'q') { proc.stdin.write('q\n'); return resolve({ lines: [], status: 'ok' }); }
        pending.push({ lines: [], resolve });
        proc.stdin.write(line + '\n');
      });
    },
    async query(q) {
      const r = await server.send(q);
      if (r.status.startsWith('err')) throw new Error(r.status);
      return r.lines;
    },
    ready() {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('RobotServer did not start')), 20000);
        if (booted) { clearTimeout(timer); return resolve(); }
        onBoot = () => { clearTimeout(timer); resolve(); };
      });
    },
    // '?pointer' is ordered behind every queued command, so awaiting it proves
    // the server finished the work before we pull the plug.
    async close() {
      try { await server.query('?pointer'); } catch {}
      server.send('q');
      await new Promise((resolve) => {
        const timer = setTimeout(() => { try { proc.kill(); } catch {} resolve(); }, 5000);
        proc.once('exit', () => { clearTimeout(timer); resolve(); });
      });
    },
  };
  return server;
}

async function screens(server) {
  const rows = await server.query('?screens');
  return rows.map((r) => {
    const [, idx, id, x, y, w, h, primary] = r.match(/^screen (\d+) (\S+) (-?\d+) (-?\d+) (\d+) (\d+)( primary)?$/);
    return { index: +idx, id, x: +x, y: +y, w: +w, h: +h, primary: !!primary };
  });
}

async function pointer(server) {
  const [row] = await server.query('?pointer');
  const [, x, y] = row.match(/^pointer (-?\d+) (-?\d+)$/);
  return { x: +x, y: +y };
}

module.exports = { start, screens, pointer, findJava, winCache };
