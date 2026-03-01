/* app.js – AvoTelemetryAgent Dashboard (vanilla JS, no external dependencies) */
'use strict';

// ── State ────────────────────────────────────────────────────────────────────
const BASE = window.location.origin;
let pollTimer = null;
let logTimer  = null;
let configCache = null;

// ── Token helpers ────────────────────────────────────────────────────────────
function getToken() { return localStorage.getItem('avo_token') || ''; }
function setToken(t) { localStorage.setItem('avo_token', t); }

// ── Querystring bootstrap ─────────────────────────────────────────────────────
// Read non-empty querystring params and use them as overrides.
// This lets a caller link to the dashboard pre-configured without forcing
// empty values to overwrite what is already stored.
(function applyQueryStringOverrides() {
  const p = new URLSearchParams(window.location.search);
  const qs = (k) => { const v = p.get(k); return (v !== null && v.trim() !== '') ? v.trim() : null; };
  const qsToken = qs('Token') || qs('token');
  if (qsToken) {
    setToken(qsToken);
    // Remove from URL so the token is not visible in history/logs.
    const clean = new URL(window.location.href);
    clean.searchParams.delete('Token'); clean.searchParams.delete('token');
    history.replaceState(null, '', clean.toString());
  }
  // Store numeric overrides in sessionStorage so loadConfig can pick them up.
  ['Port','PhysicsHz','GraphicsHz','StaticHz'].forEach(k => {
    const v = qs(k);
    if (v && !isNaN(Number(v))) sessionStorage.setItem('avo_qs_' + k.toLowerCase(), v);
  });
  const root = qs('Setup.DefaultRoot');
  if (root) sessionStorage.setItem('avo_qs_setuproot', root);
})();

const inpToken = document.getElementById('inp-token');
inpToken.value = getToken();
inpToken.addEventListener('change', () => { setToken(inpToken.value.trim()); });

document.getElementById('btn-show-token').addEventListener('click', () => {
  inpToken.type = inpToken.type === 'password' ? 'text' : 'password';
});

// ── HTTP helpers ─────────────────────────────────────────────────────────────
async function api(method, path, body) {
  const opts = {
    method,
    headers: { 'Content-Type': 'application/json', 'X-API-TOKEN': getToken() },
  };
  if (body !== undefined) opts.body = JSON.stringify(body);
  try {
    const r = await fetch(BASE + path, opts);
    if (r.status === 204) return {};
    if (!r.ok) {
      const errText = await r.text();
      if ((r.status === 401 || r.status === 403) && Date.now() - _authToastAt > 8000) {
        _authToastAt = Date.now();
        toast(r.status === 401
          ? 'Unauthorized (401) — check token.'
          : 'Forbidden (403) — admin endpoints require localhost access.');
      }
      return { _error: errText, _status: r.status };
    }
    return await r.json();
  } catch (e) {
    return { _error: e.message };
  }
}

// ── Toast ─────────────────────────────────────────────────────────────────────
let toastTimeout;
let _authToastAt = 0;
function toast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg; el.classList.add('show');
  clearTimeout(toastTimeout);
  toastTimeout = setTimeout(() => el.classList.remove('show'), 2500);
}

function showAuthWarning(msg) {
  let el = document.getElementById('auth-warning');
  if (!el) {
    el = document.createElement('div');
    el.id = 'auth-warning';
    el.style.cssText =
      'background:#7c2d12;color:#fef2f2;padding:10px 20px;font-size:13px;' +
      'text-align:center;border-bottom:1px solid #991b1b;';
    document.querySelector('main').prepend(el);
  }
  el.textContent = msg;
  el.style.display = '';
}
function hideAuthWarning() {
  const el = document.getElementById('auth-warning');
  if (el) el.style.display = 'none';
}

// ── Mini chart ───────────────────────────────────────────────────────────────
class MiniChart {
  constructor(canvasId, label, color, maxVal, maxPts = 60) {
    this.cv     = document.getElementById(canvasId);
    this.ctx    = this.cv.getContext('2d');
    this.label  = label;
    this.color  = color;
    this.maxVal = maxVal;
    this.maxPts = maxPts;
    this.data   = [];
  }
  push(v) {
    this.data.push(v);
    if (this.data.length > this.maxPts) this.data.shift();
    this._draw();
  }
  _draw() {
    const { cv, ctx, data, label, color, maxVal, maxPts } = this;
    const W = cv.clientWidth  || cv.width;
    const H = cv.clientHeight || cv.height;
    cv.width  = W; cv.height = H;            // reset for crisp render

    ctx.fillStyle = '#111'; ctx.fillRect(0, 0, W, H);

    // Grid
    ctx.strokeStyle = '#222'; ctx.lineWidth = 1; ctx.setLineDash([3, 3]);
    ctx.beginPath(); ctx.moveTo(0, H / 2); ctx.lineTo(W, H / 2); ctx.stroke();
    ctx.setLineDash([]);

    if (data.length < 2) { this._label(ctx, label, '—', W, H); return; }

    // Line
    ctx.strokeStyle = color; ctx.lineWidth = 1.5;
    ctx.beginPath();
    for (let i = 0; i < data.length; i++) {
      const x = (i / (maxPts - 1)) * W;
      const y = H - Math.min(1, data[i] / maxVal) * (H - 6) - 3;
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    }
    ctx.stroke();

    // Fill under line
    ctx.fillStyle = color + '22';
    ctx.lineTo(W, H); ctx.lineTo(0, H); ctx.closePath(); ctx.fill();

    this._label(ctx, label, data[data.length - 1].toFixed(1), W, H);
  }
  _label(ctx, lbl, val, W, H) {
    ctx.fillStyle = '#aaa'; ctx.font = '10px monospace';
    ctx.fillText(`${lbl}: ${val}`, 4, 12);
  }
}

// Instantiate charts
const charts = {
  physhz:  new MiniChart('chart-physhz',  'Physics Hz',   '#7c3aed', 80),
  gfxhz:   new MiniChart('chart-gfxhz',   'Graphics Hz',  '#2563eb', 30),
  clients: new MiniChart('chart-clients', 'WS Clients',   '#22c55e', 10, 60),
  fps:     new MiniChart('chart-fps',     'Frames/s',     '#f59e0b', 80),
  lat:     new MiniChart('chart-lat',     'Latency ms',   '#ef4444', 10),
};

// ── Poll state ────────────────────────────────────────────────────────────────
async function refreshState() {
  const s = await api('GET', '/api/admin/state');
  if (s._error) { dot(false); return; }
  dot(true);

  // Status badge
  const badge = document.getElementById('status-badge');
  badge.textContent = s.isRunning ? 'RUNNING' : 'STOPPED';
  badge.className = 'status-badge ' + (s.isRunning ? 'badge-running' : 'badge-stopped');

  document.getElementById('uptime').textContent =
    s.isRunning ? 'uptime ' + fmtSec(s.uptimeSeconds) : 'stopped';

  document.getElementById('btn-start').disabled = s.isRunning;
  document.getElementById('btn-stop').disabled  = !s.isRunning;

  // AC
  const acProc = s.acProcessRunning;
  const acMem  = s.acConnected;
  setPill('ac-process', acProc ? 'Running'       : 'Not running', acProc);
  setPill('ac-memory',  acMem  ? 'Connected'     : 'Not connected', acMem);
  document.getElementById('ac-car').textContent   = s.carId   || '—';
  document.getElementById('ac-track').textContent = s.trackId || '—';

  // Metrics
  setText('m-clients', s.connectedClients ?? 0);
  setText('m-physhz',  fmtHz(s.physicsHzActual));
  setText('m-gfxhz',   fmtHz(s.graphicsHzActual));

  // Metrics from /api/admin/metrics
  const m = await api('GET', '/api/admin/metrics');
  if (!m._error) {
    setText('m-fps',      fmtHz(m.framesSentPerSec));
    setText('m-lat',      (m.avgSendLatencyMs || 0).toFixed(2));
    setText('m-drop',     m.droppedFramesCount ?? 0);
    setText('m-mem',      (m.memoryUsageMB || 0).toFixed(1));
    setText('m-restarts', m.restartCount ?? 0);
    charts.fps.push(m.framesSentPerSec || 0);
    charts.lat.push(m.avgSendLatencyMs || 0);
  }
  charts.physhz.push(s.physicsHzActual  || 0);
  charts.gfxhz.push(s.graphicsHzActual || 0);
  charts.clients.push(s.connectedClients || 0);

  // Endpoints
  const token = getToken();
  document.getElementById('ep-ws').textContent   = `ws://${location.host}/ws?token=${token ? '***' : '(no token)'}`;
  document.getElementById('ep-ping').textContent = `http://${location.host}/api/ping`;
  document.getElementById('agent-version').textContent = s.agentVersion || '';

  // Toggles (sync with config)
  if (!configCache) await loadConfig();
  if (configCache) {
    setToggle('tog-autostart', configCache.agent?.autoStartStreaming);
    setToggle('tog-autostop',  configCache.agent?.autoStopWhenAcCloses);
    setToggle('tog-discovery', configCache.discovery?.enabled);
    setToggle('tog-localhost', configCache.adminUi?.bindLocalhostOnly);
  }

  // Autostart
  const as = await api('GET', '/api/admin/autostart');
  if (!as._error) {
    const asEl = document.getElementById('autostart-status');
    asEl.textContent = as.enabled ? 'Enabled' : 'Disabled';
    asEl.className   = 'badge-pill ' + (as.enabled ? 'badge-on' : 'badge-off');
  }
}

// ── Control ───────────────────────────────────────────────────────────────────
async function ctrlStart() {
  const r = await api('POST', '/api/admin/start');
  if (r._error) toast('Error: ' + (r._error || r._status)); else { toast('Streaming started'); refreshState(); }
}
async function ctrlStop() {
  const r = await api('POST', '/api/admin/stop');
  if (r._error) toast('Error: ' + (r._error || r._status)); else { toast('Streaming stopped'); refreshState(); }
}

// ── Config ────────────────────────────────────────────────────────────────────
async function loadConfig() {
  const c = await api('GET', '/api/admin/config');
  if (c._error) {
    if (c._status === 401) {
      toast('Unauthorized (401). Set API token at top and reload.');
      showAuthWarning('⚠ Unauthorized — paste your API token in the Token field above and reload the page.');
    }
    return;
  }
  hideAuthWarning();
  configCache = c;

  // Token: server never returns the value; we read it from localStorage.
  // If a token is stored locally, show it in the config field so the user
  // can inspect/change it. The tokenConfigured flag tells us whether the
  // server has a token set at all.
  const storedToken = getToken();
  document.getElementById('cfg-token').value = storedToken;
  const tBadge = document.getElementById('cfg-token-status');
  if (tBadge) {
    tBadge.textContent = c.tokenConfigured ? '✓ configured' : '⚠ default';
    tBadge.className   = 'badge-pill ' + (c.tokenConfigured ? 'badge-on' : 'badge-warn');
  }

  // Numeric/string config — prefer querystring overrides stored in sessionStorage.
  const qPort  = sessionStorage.getItem('avo_qs_port');
  const qPhys  = sessionStorage.getItem('avo_qs_physicshz');
  const qGfx   = sessionStorage.getItem('avo_qs_graphicshz');
  const qStatic= sessionStorage.getItem('avo_qs_statichz');
  const qRoot  = sessionStorage.getItem('avo_qs_setuproot');

  document.getElementById('cfg-port').value     = qPort   ?? c.port     ?? 8181;
  document.getElementById('cfg-physhz').value   = qPhys   ?? c.physicsHz  ?? 60;
  document.getElementById('cfg-gfxhz').value    = qGfx    ?? c.graphicsHz ?? 20;
  document.getElementById('cfg-statichz').value = qStatic ?? c.staticHz   ?? 1;
  document.getElementById('cfg-setuproot').value = qRoot  ?? c.setup?.defaultRoot ?? '';

  const hasQsOverride = qPort || qPhys || qGfx || qStatic || qRoot;
  remoteLog('WebUI: Loaded config from server' + (hasQsOverride ? ' (querystring overrides applied)' : ''));
}

async function saveConfig(e) {
  e.preventDefault();
  const msg = document.getElementById('cfg-msg');

  // Guard: if no token is available we cannot authenticate the save request.
  const newToken = document.getElementById('cfg-token').value.trim();
  if (!getToken() && !newToken) {
    msg.textContent = '⚠ Token required — paste your API token in the field above first.';
    msg.className = 'cfg-msg warn';
    setTimeout(() => { msg.textContent = ''; }, 6000);
    remoteLog('WebUI: Save config blocked — no token available');
    return;
  }

  // If the token field is filled, update localStorage so subsequent API calls use it.
  if (newToken) {
    const prev = getToken();
    if (newToken !== prev) { setToken(newToken); inpToken.value = newToken; }
  }

  const body = {
    ...(configCache || {}),
    // Only include token if the user typed one; otherwise leave it blank so
    // the server preserves the existing token.
    token:      newToken || undefined,
    port:       +document.getElementById('cfg-port').value,
    physicsHz:  +document.getElementById('cfg-physhz').value,
    graphicsHz: +document.getElementById('cfg-gfxhz').value,
    staticHz:   +document.getElementById('cfg-statichz').value,
    setup: {
      referenceRoot:    configCache?.setup?.referenceRoot    || '',
      allowBrowseDialog: configCache?.setup?.allowBrowseDialog ?? true,
      defaultRoot:      document.getElementById('cfg-setuproot').value,
    },
  };
  const check = await api('POST', '/api/admin/restart-required-check', body);
  const r = await api('POST', '/api/admin/config', body);
  if (r._error) {
    msg.textContent = '✗ ' + (r._error || 'Save failed');
    msg.className = 'cfg-msg err';
    remoteLog('WebUI: Save config FAIL — ' + (r._error || r._status));
  } else if (check.restartRequired) {
    msg.textContent = '⚠ Saved – restart required for: ' + (check.fields || []).join(', ');
    msg.className = 'cfg-msg warn';
    configCache = body;
    remoteLog('WebUI: Saved config OK (restart required)');
  } else {
    msg.textContent = '✓ Saved';
    msg.className = 'cfg-msg ok';
    configCache = body;
    remoteLog('WebUI: Saved config OK');
  }
  setTimeout(() => { msg.textContent = ''; }, 5000);
}

function syncTokenIfChanged(newToken, prevToken) {
  if (newToken && newToken !== prevToken) {
    setToken(newToken);
    inpToken.value = newToken;
    toast('Token updated for this dashboard session');
  }
}

async function saveToggle(field, value) {
  if (!configCache) await loadConfig();
  if (!configCache) return;
  const updated = deepSet(JSON.parse(JSON.stringify(configCache)), field, value);
  await api('POST', '/api/admin/config', updated);
  configCache = updated;
}

// ── Diagnostics ───────────────────────────────────────────────────────────────
async function runDiag() {
  const btn = document.getElementById('btn-diag');
  btn.disabled = true; btn.textContent = '⌛ Running…';
  const r = await api('POST', '/api/admin/diagnostics/run');
  btn.disabled = false; btn.innerHTML = '▶ Run Diagnostics <kbd>D</kbd>';
  const pre = document.getElementById('diag-out');
  if (r._error) { pre.textContent = 'Error: ' + r._error; return; }
  pre.textContent = JSON.stringify(r, null, 2);
}

// ── Logs ───────────────────────────────────────────────────────────────────────
async function refreshLogs() {
  const level = document.getElementById('log-level').value;
  const cat   = document.getElementById('log-cat').value;
  const qs    = new URLSearchParams({ take: 200 });
  if (level) qs.set('level', level);
  if (cat)   qs.set('category', cat);
  const r = await api('GET', `/api/admin/logs?${qs}`);
  if (r._error || !Array.isArray(r)) return;
  const el = document.getElementById('log-lines');
  el.innerHTML = r.map(e => {
    const ts  = fmtTimestamp(e.timestampUtc);
    const lvl = e.level || '';
    const cat = (e.category || '').split('.').pop();
    return `<div class="log-line"><span class="log-ts">${ts}</span><span class="log-lvl lvl-${lvl}">${lvl.slice(0,4)}</span><span class="log-cat" title="${e.category}">${cat}</span><span class="log-msg">${escHtml(e.message || '')}</span></div>`;
  }).join('');
  el.scrollTop = el.scrollHeight;
}

async function exportLogs() {
  window.open(BASE + '/api/admin/logs/download?token=' + encodeURIComponent(getToken()), '_blank');
}

async function clearLogs() {
  if (!confirm('Clear all logs?')) return;
  await api('POST', '/api/admin/logs/clear');
  document.getElementById('log-lines').innerHTML = '';
  toast('Logs cleared');
}

// ── Autostart ─────────────────────────────────────────────────────────────────
async function autostartEnable() {
  const r = await api('POST', '/api/admin/autostart/enable');
  toast(r._error ? 'Error: ' + r._error : 'Autostart enabled'); refreshState();
}
async function autostartDisable() {
  const r = await api('POST', '/api/admin/autostart/disable');
  toast(r._error ? 'Error: ' + r._error : 'Autostart disabled'); refreshState();
}

// ── Open folder ───────────────────────────────────────────────────────────────
async function openFolder(which) {
  const r = await api('POST', '/api/admin/open-folder?which=' + which);
  if (r._error) toast('Error: ' + r._error);
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function dot(on) {
  const el = document.getElementById('conn-dot');
  el.className = 'dot ' + (on ? 'dot-on' : 'dot-off');
  el.title = on ? 'Connected' : 'Disconnected';
}
function setText(id, v) { const e = document.getElementById(id); if (e) e.textContent = v; }
function fmtHz(v) { return (v || 0).toFixed(1); }
function fmtTimestamp(utcString) {
  if (!utcString) return '';
  return new Date(utcString).toISOString().replace('T', ' ').slice(0, 23);
}
function fmtSec(s) {
  s = Math.floor(s || 0);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60;
  return `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(ss).padStart(2,'0')}`;
}
function setPill(id, text, on) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = text;
  el.className = 'badge-pill ' + (on ? 'badge-on' : 'badge-off');
}
function setToggle(id, val) {
  const el = document.getElementById(id);
  if (el) el.checked = !!val;
}
function copyEp(id) {
  const t = document.getElementById(id)?.textContent || '';
  navigator.clipboard.writeText(t).then(() => toast('Copied!'));
}
function togglePwd(id) {
  const el = document.getElementById(id);
  if (el) el.type = el.type === 'password' ? 'text' : 'password';
}
function toggleSection(id) {
  const el = document.getElementById(id);
  if (!el) return;
  const hidden = el.style.display === 'none';
  el.style.display = hidden ? '' : 'none';
  if (id === 'logs-body' && hidden) refreshLogs();
  if (id === 'setup-ai-body' && hidden) aiLoadCars();
}
function escHtml(s) {
  return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}
/**
 * Sets a nested property on `obj` using a dot-separated path.
 * Path segments are PascalCase (e.g. "Agent.AutoStartStreaming") and are
 * converted to camelCase when applied (e.g. obj.agent.autoStartStreaming).
 */
function deepSet(obj, dotPath, val) {
  const keys = dotPath.split('.');
  let cur = obj;
  for (let i = 0; i < keys.length - 1; i++) {
    const k = keys[i].charAt(0).toLowerCase() + keys[i].slice(1);
    if (!cur[k]) cur[k] = {};
    cur = cur[k];
  }
  const last = keys[keys.length - 1];
  const lk = last.charAt(0).toLowerCase() + last.slice(1);
  cur[lk] = val;
  return obj;
}

// ── Keyboard shortcuts ────────────────────────────────────────────────────────
document.addEventListener('keydown', e => {
  if (['INPUT','TEXTAREA','SELECT'].includes(e.target.tagName)) return;
  if (e.key === 'S' || e.key === 's') {
    const running = document.getElementById('status-badge').classList.contains('badge-running');
    running ? ctrlStop() : ctrlStart();
  }
  if (e.key === 'D' || e.key === 'd') {
    const body = document.getElementById('diag-body');
    if (body.style.display === 'none') toggleSection('diag-body');
    runDiag();
  }
  if (e.key === 'L' || e.key === 'l') {
    const body = document.getElementById('logs-body');
    if (body.style.display === 'none') toggleSection('logs-body');
    document.getElementById('card-logs').scrollIntoView({ behavior: 'smooth' });
  }
});

// ── Reference Setups Folder ───────────────────────────────────────────────────
async function loadRefRoot() {
  const r = await api('GET', '/api/admin/referenceRoot/get');
  if (r._error) return;
  document.getElementById('inp-refroot').value = r.path || '';
  const msg = document.getElementById('refroot-counts');
  if (msg) {
    msg.textContent = r.configured
      ? `✓ Configured — ${r.carsCount} car folder(s), ${r.totalCount} setup file(s) found`
      : (r.path ? '⚠ Folder does not exist' : 'Not configured — enter a path and click Save');
  }
}

async function refBrowse() {
  const r = await api('POST', '/api/admin/referenceRoot/browse');
  if (r._error) { toast('Browse error: ' + r._error); return; }
  if (r.ok && r.path) {
    document.getElementById('inp-refroot').value = r.path;
    toast('Folder selected');
  } else {
    toast(`Folder selection cancelled or blocked. Open dashboard via http://${location.host} on the simulator PC and ensure token is set.`);
  }
}

async function refSave() {
  const path = document.getElementById('inp-refroot').value.trim();
  const msg  = document.getElementById('refroot-msg');
  const r    = await api('POST', '/api/admin/referenceRoot/set', { path });
  if (r._error) {
    msg.textContent = '✗ ' + (r._error || 'Save failed');
    msg.className   = 'cfg-msg err';
  } else {
    msg.textContent = '✓ Saved';
    msg.className   = 'cfg-msg ok';
    await loadRefRoot();
  }
  setTimeout(() => { msg.textContent = ''; }, 4000);
}

async function refRescan() {
  const r = await api('POST', '/api/admin/setup/reference/rescan');
  if (r._error) { toast('Rescan error: ' + r._error); return; }
  toast(`Rescan done — ${r.count} setup file(s)`);
  await loadRefRoot();
}

// ── Remote Setups ─────────────────────────────────────────────────────────────
const REM_KEY = 'avo_remote';

function loadRemoteConfig() {
  try {
    const c = JSON.parse(localStorage.getItem(REM_KEY) || '{}');
    document.getElementById('rem-host').value  = c.host  || '';
    document.getElementById('rem-port').value  = c.port  || 8181;
    document.getElementById('rem-token').value = c.token || '';
    const en = !!c.enabled;
    document.getElementById('tog-remote').checked = en;
    document.getElementById('remote-controls').style.display = en ? '' : 'none';
  } catch (_) {}
}

function saveRemoteConfig() {
  const c = {
    host:    document.getElementById('rem-host').value.trim(),
    port:    document.getElementById('rem-port').value,
    token:   document.getElementById('rem-token').value,
    enabled: document.getElementById('tog-remote').checked,
  };
  localStorage.setItem(REM_KEY, JSON.stringify(c));
}

function onRemoteModeChange() {
  saveRemoteConfig();
  const en = document.getElementById('tog-remote').checked;
  document.getElementById('remote-controls').style.display = en ? '' : 'none';
  if (en) { remoteLoadCars(); aiLoadCars(); }
}

function remoteBase() {
  const host = document.getElementById('rem-host').value.trim() || 'localhost';
  const port = document.getElementById('rem-port').value || 8181;
  return `http://${host}:${port}`;
}

function remoteToken() { return document.getElementById('rem-token').value; }

async function remoteApi(method, path, body) {
  const opts = {
    method,
    headers: { 'Content-Type': 'application/json', 'X-API-TOKEN': remoteToken() },
  };
  if (body !== undefined) opts.body = JSON.stringify(body);
  try {
    const r = await fetch(remoteBase() + path, opts);
    if (r.status === 204) return {};
    if (!r.ok) return { _error: await r.text(), _status: r.status };
    return await r.json();
  } catch (e) {
    return { _error: e.message };
  }
}

function remoteLog(msg) {
  const el = document.getElementById('rem-terminal');
  const ts = new Date().toISOString().replace('T', ' ').slice(0, 23);
  const line = document.createElement('div');
  line.className = 'remote-term-line';
  line.textContent = `[${ts}] ${msg}`;
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
}

async function remoteLoadCars() {
  const r = await remoteApi('GET', '/api/reference/cars');
  const sel = document.getElementById('rem-car');
  sel.innerHTML = '<option value="">— select car —</option>';
  if (!Array.isArray(r)) { remoteLog('Error loading cars: ' + (r._error || 'unknown')); return; }
  r.forEach(c => { const o = document.createElement('option'); o.value = o.textContent = c; sel.appendChild(o); });
}

async function remoteLoadTracks() {
  const car  = document.getElementById('rem-car').value;
  const tSel = document.getElementById('rem-track');
  const sSel = document.getElementById('rem-setup');
  tSel.innerHTML = '<option value="">— select track —</option>';
  sSel.innerHTML = '<option value="">— select setup —</option>';
  document.getElementById('rem-content').value = '';
  if (!car) return;
  const r = await remoteApi('GET', `/api/reference/tracks?car=${encodeURIComponent(car)}`);
  if (!Array.isArray(r)) { remoteLog('Error loading tracks: ' + (r._error || 'unknown')); return; }
  r.forEach(t => { const o = document.createElement('option'); o.value = o.textContent = t; tSel.appendChild(o); });
}

async function remoteLoadSetups() {
  const car   = document.getElementById('rem-car').value;
  const track = document.getElementById('rem-track').value;
  const sSel  = document.getElementById('rem-setup');
  sSel.innerHTML = '<option value="">— select setup —</option>';
  document.getElementById('rem-content').value = '';
  if (!car || !track) return;
  const r = await remoteApi('GET', `/api/reference/setups?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}`);
  if (!Array.isArray(r)) { remoteLog('Error loading setups: ' + (r._error || 'unknown')); return; }
  r.forEach(f => { const o = document.createElement('option'); o.value = o.textContent = f; sSel.appendChild(o); });
}

async function remoteLoadContent() {
  const car   = document.getElementById('rem-car').value;
  const track = document.getElementById('rem-track').value;
  const file  = document.getElementById('rem-setup').value;
  if (!car || !track || !file) return;
  const r = await remoteApi('GET', `/api/reference/setup/read?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}&file=${encodeURIComponent(file)}`);
  if (r._error) { remoteLog('Error loading setup content: ' + r._error); return; }
  document.getElementById('rem-content').value = r.setupText || '';
}

async function remoteSaveSetup() {
  const car          = document.getElementById('rem-car').value;
  const track        = document.getElementById('rem-track').value;
  const setupFile    = document.getElementById('rem-setup').value;
  const content      = document.getElementById('rem-content').value;
  const baseFileName = setupFile ? setupFile.replace(/\.(ini|json)$/i, '') : 'iter';
  if (!car || !track)    { remoteLog('Error: car and track are required.'); return; }
  if (!content.trim())   { remoteLog('Error: content is empty.'); return; }

  const msg = document.getElementById('rem-msg');
  const r = await remoteApi('POST', '/api/setups/save', { car, track, baseFileName, content });
  if (r._error) {
    remoteLog('Error saving: ' + (r._error || String(r._status)));
    msg.textContent = '✗ Save failed'; msg.className = 'cfg-msg err';
  } else {
    remoteLog(`Saved remote setup: ${r.fileName}`);
    msg.textContent = `✓ ${r.fileName}`; msg.className = 'cfg-msg ok';
    await remoteLoadSetups();
  }
  setTimeout(() => { msg.textContent = ''; }, 5000);
}

// ── Setup AI Workflow ─────────────────────────────────────────────────────────
const aiState = { car: '', track: '', baseFile: '', baseIni: '', changes: [], nextId: 1 };
let _wsSocket = null;

function aiShowTab(tabId) {
  document.querySelectorAll('#card-setup-ai .tab-pane').forEach(el => el.style.display = 'none');
  document.getElementById(tabId).style.display = '';
  document.querySelectorAll('#card-setup-ai .tab-btn').forEach(b => b.classList.remove('tab-active'));
  const btn = [...document.querySelectorAll('#card-setup-ai .tab-btn')]
    .find(b => b.getAttribute('onclick') === `aiShowTab('${tabId}')`);
  if (btn) btn.classList.add('tab-active');
  if (tabId === 'ai-tab-versions') aiLoadVersions();
  if (tabId === 'ai-tab-logs' && !_wsSocket) wsConnectLogs();
}

async function aiLoadCars() {
  const r = await remoteApi('GET', '/api/reference/cars');
  const sel = document.getElementById('ai-car');
  sel.innerHTML = '<option value="">— select car —</option>';
  if (!Array.isArray(r)) return;
  r.forEach(c => { const o = document.createElement('option'); o.value = o.textContent = c; sel.appendChild(o); });
}

async function aiLoadTracks() {
  aiState.car = document.getElementById('ai-car').value;
  const tSel = document.getElementById('ai-track');
  const sSel = document.getElementById('ai-setup');
  tSel.innerHTML = '<option value="">— select track —</option>';
  sSel.innerHTML = '<option value="">— select setup —</option>';
  Object.assign(aiState, { track: '', baseFile: '', baseIni: '' });
  document.getElementById('ai-base-content').value = '';
  document.getElementById('ai-loaded-badge').textContent = '';
  if (!aiState.car) return;
  const r = await remoteApi('GET', `/api/reference/tracks?car=${encodeURIComponent(aiState.car)}`);
  if (!Array.isArray(r)) return;
  r.forEach(t => { const o = document.createElement('option'); o.value = o.textContent = t; tSel.appendChild(o); });
}

async function aiLoadSetups() {
  aiState.track = document.getElementById('ai-track').value;
  const sSel = document.getElementById('ai-setup');
  sSel.innerHTML = '<option value="">— select setup —</option>';
  Object.assign(aiState, { baseFile: '', baseIni: '' });
  document.getElementById('ai-base-content').value = '';
  document.getElementById('ai-loaded-badge').textContent = '';
  if (!aiState.car || !aiState.track) return;
  const r = await remoteApi('GET', `/api/reference/setups?car=${encodeURIComponent(aiState.car)}&track=${encodeURIComponent(aiState.track)}`);
  if (!Array.isArray(r)) return;
  r.forEach(f => { const o = document.createElement('option'); o.value = o.textContent = f; sSel.appendChild(o); });
}

async function aiLoadContent() {
  aiState.baseFile = document.getElementById('ai-setup').value;
  document.getElementById('ai-base-content').value = '';
  document.getElementById('ai-loaded-badge').textContent = '';
  if (!aiState.car || !aiState.track || !aiState.baseFile) { aiUpdateApplyBtn(); return; }
  const r = await remoteApi('GET', `/api/reference/setup/read?car=${encodeURIComponent(aiState.car)}&track=${encodeURIComponent(aiState.track)}&file=${encodeURIComponent(aiState.baseFile)}`);
  if (r._error) { remoteLog('AI: Error loading: ' + r._error); return; }
  aiState.baseIni = r.setupText || '';
  document.getElementById('ai-base-content').value = aiState.baseIni;
  document.getElementById('ai-loaded-badge').textContent = `✓ Loaded: ${aiState.baseFile}  (${aiState.baseIni.length} bytes)`;
  remoteLog(`AI: Loaded base file: ${aiState.baseFile}`);
  aiUpdateApplyBtn();
}

function aiAddChange() {
  const id = aiState.nextId++;
  aiState.changes.push({ id, section: '', key: '', value: '' });
  aiRenderChanges();
}
function aiRemoveChange(id) {
  aiState.changes = aiState.changes.filter(c => c.id !== id);
  aiRenderChanges();
}
function aiUpdateChange(id, field, value) {
  const c = aiState.changes.find(c => c.id === id);
  if (c) c[field] = value;
  aiUpdateApplyBtn();
}
function aiRenderChanges() {
  const container = document.getElementById('ai-changes-list');
  if (!aiState.changes.length) {
    container.innerHTML = '<div class="muted small">No changes yet — click "+ Add Change" to add key/value patches.</div>';
    aiUpdateApplyBtn(); return;
  }
  container.innerHTML = aiState.changes.map(c => `
    <div class="ai-change-row" data-id="${c.id}">
      <input type="text" placeholder="Section" value="${escHtml(c.section)}"
        oninput="aiUpdateChange(${c.id},'section',this.value)" style="width:120px">
      <input type="text" placeholder="Key" value="${escHtml(c.key)}"
        oninput="aiUpdateChange(${c.id},'key',this.value)" style="width:150px">
      <input type="text" placeholder="Value" value="${escHtml(c.value)}"
        oninput="aiUpdateChange(${c.id},'value',this.value)" style="flex:1;min-width:80px">
      <button class="btn-icon" onclick="aiRemoveChange(${c.id})" title="Remove change">✕</button>
    </div>`).join('');
  aiUpdateApplyBtn();
}
function aiUpdateApplyBtn() {
  const btn = document.getElementById('btn-ai-apply');
  if (!btn) return;
  const hasFile    = !!aiState.baseFile;
  const hasChanges = aiState.changes.some(c => c.section && c.key);
  btn.disabled = !(hasFile && hasChanges);
}

async function aiApply() {
  const btn    = document.getElementById('btn-ai-apply');
  const status = document.getElementById('ai-apply-status');
  btn.disabled = true;
  status.textContent = '⌛ Applying…'; status.className = 'cfg-msg';
  const body = {
    car:                aiState.car,
    track:              aiState.track,
    baseFile:           aiState.baseFile,
    changes:            aiState.changes.filter(c => c.section && c.key)
                          .map(c => ({ section: c.section, key: c.key, value: c.value })),
    createVersionedCopy: document.getElementById('ai-versioned').checked,
    reason:             document.getElementById('ai-reason').value.trim() || 'AI',
  };
  const r = await remoteApi('POST', '/api/reference/setup/apply', body);
  if (r._error || !r.ok) {
    status.textContent = '✗ ' + (r._error || 'Apply failed'); status.className = 'cfg-msg err';
    remoteLog('AI: Apply FAILED: ' + (r._error || JSON.stringify(r)));
    btn.disabled = false; return;
  }
  status.textContent = `✓ Saved: ${r.savedFile}`; status.className = 'cfg-msg ok';
  const badge = document.getElementById('ai-applied-badge');
  badge.style.display = '';
  badge.innerHTML = `Applied → <strong>${escHtml(r.savedFile)}</strong><br><span class="muted small">${escHtml(r.path || '')}</span>`;
  remoteLog(`AI: Applied → ${r.savedFile}`);
  await aiRefreshSetups(r.savedFile);
  aiRenderDiff(r.diff);
  btn.disabled = false;
  setTimeout(() => { status.textContent = ''; }, 6000);
}

async function aiRefreshSetups(selectFile) {
  if (!aiState.car || !aiState.track) return;
  const sSel = document.getElementById('ai-setup');
  const r = await remoteApi('GET', `/api/reference/setups?car=${encodeURIComponent(aiState.car)}&track=${encodeURIComponent(aiState.track)}`);
  if (!Array.isArray(r)) return;
  sSel.innerHTML = '<option value="">— select setup —</option>';
  r.forEach(f => {
    const o = document.createElement('option');
    o.value = o.textContent = f;
    if (f === selectFile) o.selected = true;
    sSel.appendChild(o);
  });
  if (selectFile) { aiState.baseFile = selectFile; await aiLoadContent(); }
}

function aiRenderDiff(diff) {
  const empty = document.getElementById('ai-diff-empty');
  const table = document.getElementById('ai-diff-table');
  const tbody = document.getElementById('ai-diff-tbody');
  if (!diff || !diff.length) { empty.style.display = ''; table.style.display = 'none'; return; }
  empty.style.display = 'none';
  tbody.innerHTML = diff.map(d => `
    <tr>
      <td class="mono">${escHtml(d.section || '')}</td>
      <td class="mono">${escHtml(d.key || '')}</td>
      <td class="mono diff-old">${escHtml(d.oldValue ?? '—')}</td>
      <td class="mono diff-new">${escHtml(d.newValue ?? '—')}</td>
    </tr>`).join('');
  table.style.display = '';
  aiShowTab('ai-tab-diff');
}

async function aiLoadVersions() {
  const container = document.getElementById('ai-versions-list');
  if (!aiState.car || !aiState.track || !aiState.baseFile) {
    container.innerHTML = '<span class="muted small">Select car, track and base setup in the Load tab first.</span>';
    return;
  }
  container.innerHTML = '<span class="muted small">Loading…</span>';
  const r = await remoteApi('GET',
    `/api/reference/setup/versions?car=${encodeURIComponent(aiState.car)}&track=${encodeURIComponent(aiState.track)}&baseFile=${encodeURIComponent(aiState.baseFile)}`);
  if (r._error || !r.ok) {
    container.innerHTML = `<span class="muted small">Error: ${escHtml(r._error || 'unknown')}</span>`; return;
  }
  if (!r.versions || !r.versions.length) {
    container.innerHTML = '<span class="muted small">No versioned copies found yet.</span>'; return;
  }
  container.innerHTML = r.versions.map(v => {
    const ts   = v.savedUtc ? new Date(v.savedUtc).toISOString().replace('T', ' ').slice(0, 19) : '';
    const meta = v.meta ? ` <span class="muted small" title="${escHtml(JSON.stringify(v.meta, null, 2))}">📄 meta</span>` : '';
    return `<div class="version-row"><strong class="mono">${escHtml(v.fileName)}</strong><span class="muted small">${ts} · ${v.sizeBytes} B</span>${meta}</div>`;
  }).join('');
}

// ── WebSocket live logs ───────────────────────────────────────────────────────
function wsConnectLogs() {
  if (_wsSocket && _wsSocket.readyState < 2) return;
  const host  = document.getElementById('rem-host')?.value.trim() || 'localhost';
  const port  = document.getElementById('rem-port')?.value || 8181;
  const token = remoteToken();
  const url   = `ws://${host}:${port}/ws/logs${token ? '?token=' + encodeURIComponent(token) : ''}`;
  document.getElementById('ws-status').textContent = 'Connecting…';
  _wsSocket = new WebSocket(url);
  _wsSocket.onopen = () => {
    document.getElementById('ws-status').textContent = '● Connected';
    document.getElementById('btn-ws-connect').style.display = 'none';
    document.getElementById('btn-ws-disconnect').style.display = '';
    wsAppendLog('[system] Connected to ' + url);
  };
  _wsSocket.onmessage = e => {
    let msg = e.data;
    try {
      const obj = JSON.parse(e.data);
      msg = `[${obj.level || 'LOG'}] ${obj.message || JSON.stringify(obj)}`;
    } catch (_) { /* plain text */ }
    const filter = document.getElementById('ws-log-filter').value;
    if (!filter || msg.toUpperCase().includes(filter)) wsAppendLog(msg);
  };
  _wsSocket.onerror  = () => wsAppendLog('[system] WebSocket error');
  _wsSocket.onclose  = () => {
    document.getElementById('ws-status').textContent = '○ Disconnected';
    document.getElementById('btn-ws-connect').style.display = '';
    document.getElementById('btn-ws-disconnect').style.display = 'none';
    wsAppendLog('[system] Disconnected');
    _wsSocket = null;
  };
}
function wsDisconnectLogs() { if (_wsSocket) { _wsSocket.close(); _wsSocket = null; } }
function wsAppendLog(msg) {
  const el = document.getElementById('ws-log-lines');
  if (!el) return;
  const ts   = new Date().toISOString().replace('T', ' ').slice(0, 23);
  const line = document.createElement('div');
  line.className = 'log-line';
  line.textContent = `[${ts}] ${msg}`;
  el.appendChild(line);
  if (document.getElementById('ws-autoscroll')?.checked) el.scrollTop = el.scrollHeight;
  while (el.children.length > 500) el.removeChild(el.firstChild);
}

// ── Boot ──────────────────────────────────────────────────────────────────────
(async function init() {
  // Check auth-info: show banner immediately if token is required but not stored.
  try {
    const info = await fetch(BASE + '/api/public/auth-info').then(r => r.json());
    if (info.tokenRequired && !getToken()) {
      showAuthWarning('⚠ Token required — paste your API token in the Token field above and reload the page.');
    }
  } catch (_) { /* non-critical */ }

  loadRemoteConfig();
  await loadConfig();
  await Promise.all([refreshState(), refreshLogs(), loadRefRoot()]);
  pollTimer = setInterval(refreshState, 2000);
  logTimer  = setInterval(refreshLogs,  5000);
})();
