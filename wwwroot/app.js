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
    headers: { 'Content-Type': 'application/json', 'X-AVO-TOKEN': getToken() },
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
  document.getElementById('cfg-token').value    = c.token    || '';
  document.getElementById('cfg-port').value     = c.port     || 8181;
  document.getElementById('cfg-physhz').value   = c.physicsHz  || 60;
  document.getElementById('cfg-gfxhz').value    = c.graphicsHz || 20;
  document.getElementById('cfg-statichz').value = c.staticHz   || 1;
  document.getElementById('cfg-setuproot').value = c.setup?.defaultRoot || '';
}

async function saveConfig(e) {
  e.preventDefault();
  const msg = document.getElementById('cfg-msg');
  const body = {
    ...(configCache || {}),
    token:      document.getElementById('cfg-token').value,
    port:       +document.getElementById('cfg-port').value,
    physicsHz:  +document.getElementById('cfg-physhz').value,
    graphicsHz: +document.getElementById('cfg-gfxhz').value,
    staticHz:   +document.getElementById('cfg-statichz').value,
    setup: {
      // Explicitly carry forward fields that are managed by other UI controls.
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
  } else if (check.restartRequired) {
    msg.textContent = '⚠ Saved – restart required for: ' + (check.fields || []).join(', ');
    msg.className = 'cfg-msg warn';
    const prevToken1 = configCache?.token;
    configCache = body;
    syncTokenIfChanged(body.token, prevToken1);
  } else {
    msg.textContent = '✓ Saved';
    msg.className = 'cfg-msg ok';
    const prevToken2 = configCache?.token;
    configCache = body;
    syncTokenIfChanged(body.token, prevToken2);
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

// ── Boot ──────────────────────────────────────────────────────────────────────
(async function init() {
  await loadConfig();
  await Promise.all([refreshState(), refreshLogs(), loadRefRoot()]);
  pollTimer = setInterval(refreshState, 2000);
  logTimer  = setInterval(refreshLogs,  5000);
})();
