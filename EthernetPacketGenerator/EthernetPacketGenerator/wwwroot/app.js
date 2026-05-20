const $ = (id) => document.getElementById(id);

const state = {
  interfaces: [],
  senderIface: '',
  captureInterfaces: new Set(),
  captureRows: [],
  captureTimer: null,
  serialTimer: null,
  serialConnected: false,
  selectedGroupIdx: 0,
  selectedTcIdx: null,
};

// ── API helper ────────────────────────────────────────────────────────────────
async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: { 'content-type': 'application/json', ...(options.headers || {}) },
  });
  const data = await res.json();
  if (!res.ok || data.ok === false) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

function toast(msg, kind = 'info') {
  const tray = $('toastTray');
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.textContent = msg;
  tray.appendChild(el);
  setTimeout(() => el.remove(), 4000);
}

function setStatus(text, ok = true) {
  $('status').textContent = text;
  $('serverState').classList.toggle('bad', !ok);
  $('workerStatus').textContent = ok ? `Worker: connected` : `Worker: offline`;
  $('workerStatus').className = ok ? 'ok' : 'err';
}

function esc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
}

function tsNow() {
  const d = new Date();
  const h = String(d.getHours()).padStart(2, '0');
  const m = String(d.getMinutes()).padStart(2, '0');
  const s = String(d.getSeconds()).padStart(2, '0');
  const ms = String(d.getMilliseconds()).padStart(3, '0');
  return `[${h}:${m}:${s}.${ms}]`;
}

// ── Tab switching ─────────────────────────────────────────────────────────────
function initTabs() {
  document.querySelectorAll('.tab').forEach(tab => {
    tab.addEventListener('click', () => {
      document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
      document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
      tab.classList.add('active');
      $(tab.dataset.view)?.classList.add('active');
      if (tab.dataset.view === 'hyperTermView') refreshSerialStatus();
    });
  });
}

// ── Interfaces ────────────────────────────────────────────────────────────────
async function refreshInterfaces() {
  const data = await api('/api/interfaces');
  state.interfaces = data.interfaces || [];
  renderSenderInterfaces();
  await refreshCaptureStatus();
  setStatus(`Connected — ${state.interfaces.length} interfaces`);
}

function renderSenderInterfaces() {
  const wrap = $('senderInterfaces');
  if (!state.interfaces.length) { wrap.innerHTML = '<p style="color:var(--muted);font-size:10px;">No interfaces found.</p>'; return; }
  wrap.innerHTML = '';
  for (const iface of state.interfaces) {
    const btn = document.createElement('button');
    btn.className = `chip ${iface.state === 'up' ? 'up' : ''}`;
    btn.textContent = `${iface.name}`;
    btn.title = iface.mac || '';
    btn.addEventListener('click', () => {
      state.senderIface = iface.name;
      wrap.querySelectorAll('.chip').forEach(b => b.classList.remove('selected'));
      btn.classList.add('selected');
      if (!$('srcMac').value && iface.mac) $('srcMac').value = iface.mac;
      if (!$('srcIp').value && iface.ipv4?.[0]?.local) $('srcIp').value = iface.ipv4[0].local;
    });
    wrap.appendChild(btn);
  }
}

// ── Frame Builder ─────────────────────────────────────────────────────────────
function buildProfile() {
  const profile = {
    protocol: $('protocol').value,
    interface: state.senderIface || null,
    dstMac: $('dstMac').value.trim(),
    srcMac: $('srcMac').value.trim(),
    srcIp: $('srcIp').value.trim(),
    dstIp: $('dstIp').value.trim(),
    udp: { srcPort: Number($('srcPort').value) || 12345, dstPort: Number($('dstPort').value) || 50000 },
    count: Number($('count').value) || 1,
    intervalMs: Number($('intervalMs').value) || 0,
    payload: { mode: 'text', data: $('payload').value },
  };
  if ($('vlanEnabled').checked) {
    profile.vlan = { enabled: true, id: Number($('vlanId').value) || 100, priority: Number($('vlanPriority').value) || 0 };
  }
  return profile;
}

function formatHex(hex) {
  if (!hex) return '';
  const bytes = hex.match(/.{1,2}/g) || [];
  const lines = [];
  for (let off = 0; off < bytes.length; off += 16) {
    const chunk = bytes.slice(off, off + 16);
    const ascii = chunk.map(b => { const n = parseInt(b, 16); return n >= 32 && n <= 126 ? String.fromCharCode(n) : '.'; }).join('');
    lines.push(`${off.toString(16).padStart(4,'0')}  ${chunk.join(' ').padEnd(47)}  ${ascii}`);
  }
  return lines.join('\n');
}

async function previewFrame() {
  try {
    const data = await api('/api/build', { method: 'POST', body: JSON.stringify(buildProfile()) });
    const out = data.stdout || data;
    $('decoded').textContent = JSON.stringify(out.decoded || {}, null, 2);
    $('hexdump').textContent = formatHex(out.frameHex);
  } catch (err) {
    toast(`Build failed: ${err.message}`, 'bad');
  }
}

async function sendFrame() {
  if (!state.senderIface) { toast('Select a sender interface first', 'warn'); return; }
  try {
    const data = await api('/api/send', { method: 'POST', body: JSON.stringify(buildProfile()) });
    const out = data.stdout || data;
    toast(`Sent ${out.framesSent || 1} frame(s), ${out.bytesSent || '?'} bytes`, 'ok');
  } catch (err) {
    toast(`Send failed: ${err.message}`, 'bad');
  }
}

// ── Capture ───────────────────────────────────────────────────────────────────
async function refreshCaptureStatus() {
  const data = await api('/api/capture/status');
  $('captureRunning').textContent = data.running ? 'capturing' : 'idle';
  $('captureTotal').textContent = `${data.totalPackets || 0} pkts`;

  const list = $('captureInterfaces');
  list.innerHTML = '';
  state.captureInterfaces = new Set((data.interfaces || []).filter(i => i.selected).map(i => i.name));
  for (const iface of data.interfaces || []) {
    const label = document.createElement('label');
    label.className = 'check-row';
    label.innerHTML = `<input type="checkbox" ${iface.selected ? 'checked' : ''} value="${esc(iface.name)}">
      <span><strong>${esc(iface.name)}</strong><small>${esc(iface.description || iface.state || '')}</small></span>`;
    label.querySelector('input').addEventListener('change', e => {
      if (e.target.checked) state.captureInterfaces.add(iface.name);
      else state.captureInterfaces.delete(iface.name);
    });
    list.appendChild(label);
  }
}

async function startCapture() {
  try {
    await api('/api/capture/start', { method: 'POST', body: JSON.stringify({ interfaces: [...state.captureInterfaces] }) });
    toast('Capture started', 'ok');
    startCapturePolling();
    await refreshCaptureStatus();
  } catch (err) { toast(`Capture failed: ${err.message}`, 'bad'); }
}

async function stopCapture() {
  await api('/api/capture/stop', { method: 'POST', body: '{}' });
  toast('Capture stopped', 'ok');
  await refreshCaptureStatus();
}

async function clearCapture() {
  await api('/api/capture/clear', { method: 'POST', body: '{}' });
  state.captureRows = [];
  renderCaptureRows();
  $('packetDetails').textContent = 'Select a packet.';
  $('packetHex').textContent = '';
  await refreshCaptureStatus();
}

function startCapturePolling() {
  if (state.captureTimer) clearInterval(state.captureTimer);
  state.captureTimer = setInterval(loadCapturePackets, 900);
  loadCapturePackets();
}

async function loadCapturePackets() {
  try {
    const data = await api('/api/capture/packets?limit=1000');
    state.captureRows = data.rows || [];
    renderCaptureRows();
    await refreshCaptureStatus();
  } catch { /* keep UI stable */ }
}

function rowMatchesFilter(row, filter) {
  if (!filter) return true;
  const text = `${row.no} ${row.time} ${row.interfaceName} ${row.source} ${row.destination} ${row.protocol} ${row.length} ${row.info} ${row.srcMac} ${row.dstMac}`.toLowerCase();
  return filter.split(/\s+/).filter(Boolean).every(tok => {
    if (tok.startsWith('mac:')) return `${row.srcMac} ${row.dstMac}`.toLowerCase().includes(tok.slice(4));
    if (tok.startsWith('ip:')) return `${row.source} ${row.destination}`.toLowerCase().includes(tok.slice(3));
    if (tok.startsWith('port:')) return `${row.source} ${row.destination} ${row.info}`.toLowerCase().includes(tok.slice(5));
    return text.includes(tok);
  });
}

function renderCaptureRows() {
  const tbody = $('captureRows');
  const filter = $('captureFilter').value.trim().toLowerCase();
  const rows = state.captureRows.filter(r => rowMatchesFilter(r, filter));
  if (!rows.length) { tbody.innerHTML = `<tr><td colspan="10" class="empty">No packets match.</td></tr>`; return; }
  tbody.innerHTML = rows.map((r, i) => `
    <tr data-idx="${i}" class="proto-${esc((r.protocol||'').toLowerCase())}">
      <td>${r.no}</td><td>${esc(r.time)}</td><td>${esc(r.interfaceName)}</td>
      <td>${esc(r.srcMac)}</td><td>${esc(r.dstMac)}</td>
      <td>${esc(r.source)}</td><td>${esc(r.destination)}</td>
      <td><strong>${esc(r.protocol)}</strong></td>
      <td>${r.length}</td><td>${esc(r.info)}</td>
    </tr>`).join('');
  tbody.querySelectorAll('tr').forEach(tr => {
    tr.addEventListener('click', () => {
      tbody.querySelectorAll('tr').forEach(r => r.classList.remove('selected'));
      tr.classList.add('selected');
      const row = rows[Number(tr.dataset.idx)];
      $('packetDetails').textContent = row.detailText || 'No decoded detail.';
      $('packetHex').textContent = row.hexDump || '';
    });
  });
}

// ── Scenario Lab — Test Cases ─────────────────────────────────────────────────
async function loadTestCases() {
  try {
    const data = await api('/api/testcases/status');
    const tc = data.testCases || {};
    $('scenarioTitle').textContent = `Test Sequence — ${tc.selected || '(none selected)'}`;
    renderTcTree(tc.groups || []);
    renderSequenceRows(tc.sequence || []);
  } catch (err) {
    $('scenarioTitle').textContent = `Test Sequence — load failed: ${err.message}`;
  }
}

function renderTcTree(groups) {
  const root = $('tcTree');
  if (!groups.length) { root.innerHTML = '<p style="color:var(--muted);font-size:10px;">No groups. Add one above.</p>'; return; }
  root.innerHTML = groups.map(g => `
    <div class="tc-group">
      <div class="tc-group-head">
        <span>${esc(g.name)}</span>
        <button class="small danger tc-del-group" data-group="${g.index}">Del</button>
      </div>
      ${(g.testCases || []).map(t => `
        <div class="tc-item ${t.selected ? 'selected' : ''}" data-group="${t.groupIndex}" data-tc="${t.index}">
          <span>${esc(t.name)}</span><small>${t.itemCount} items</small>
        </div>`).join('')}
    </div>`).join('');
  root.querySelectorAll('.tc-item').forEach(el => el.addEventListener('click', async () => {
    state.selectedGroupIdx = Number(el.dataset.group);
    state.selectedTcIdx = Number(el.dataset.tc);
    await api('/api/testcases/select', { method: 'POST', body: JSON.stringify({ groupIndex: state.selectedGroupIdx, testCaseIndex: state.selectedTcIdx }) });
    await loadTestCases();
  }));
  root.querySelectorAll('.tc-del-group').forEach(btn => btn.addEventListener('click', async () => {
    if (!confirm('Delete this group?')) return;
    await api('/api/testcases/delete', { method: 'POST', body: JSON.stringify({ groupIndex: Number(btn.dataset.group) }) });
    await loadTestCases();
  }));
}

function renderSequenceRows(items) {
  const tbody = $('sequenceRows');
  if (!items.length) { tbody.innerHTML = `<tr><td colspan="8" class="empty">No sequence loaded.</td></tr>`; return; }
  tbody.innerHTML = items.map((item, i) => `
    <tr>
      <td>${i}</td>
      <td>${esc(item.kind)}</td>
      <td>${item.checked ? '✓' : ''}</td>
      <td>${esc(item.packetName || item.eventType || '')}</td>
      <td colspan="4" style="color:var(--muted);">${esc(item.eventType ? JSON.stringify(item.params || {}) : `${(item.blocks || []).length} block(s)`)}</td>
    </tr>`).join('');
}

async function addTcGroup() {
  const name = $('tcGroupName').value.trim();
  if (!name) return;
  await api('/api/testcases/add-group', { method: 'POST', body: JSON.stringify({ name }) });
  $('tcGroupName').value = '';
  await loadTestCases();
}

async function addTcFromCurrent() {
  const name = $('tcName').value.trim();
  if (!name) { toast('Enter TC name', 'warn'); return; }
  await api('/api/testcases/add', { method: 'POST', body: JSON.stringify({ groupIndex: state.selectedGroupIdx || 0, name }) });
  $('tcName').value = '';
  await loadTestCases();
}

async function saveTcCurrent() {
  try {
    await api('/api/testcases/save-current', { method: 'POST', body: '{}' });
    toast('Saved to selected test case', 'ok');
    await loadTestCases();
  } catch (err) { toast(`Save failed: ${err.message}`, 'bad'); }
}

// ── Scenario Terminal ─────────────────────────────────────────────────────────
function appendSeqTerm(text) {
  const el = $('seqTermOutput');
  el.textContent += text + '\n';
  el.scrollTop = el.scrollHeight;
}

async function seqTermSend() {
  const text = $('seqTermInput').value.trim();
  if (!text) return;
  try {
    await api('/api/serial/send', { method: 'POST', body: JSON.stringify({ text }) });
    appendSeqTerm(`> ${text}`);
    $('seqTermInput').value = '';
  } catch (err) { toast(`Send failed: ${err.message}`, 'bad'); }
}

// ── Register (Scenario Lab panel) ────────────────────────────────────────────
async function refreshRegStatus() {
  try {
    const data = await api('/api/register/status');
    $('regStatus').textContent = `${data.serialConnected ? '● connected' : '○ disconnected'} — base ${data.baseAddress || '0x0'}`;
    if (data.baseAddress) $('regBaseAddr').value = data.baseAddress;
  } catch { $('regStatus').textContent = 'offline'; }
}

async function readRegister() {
  try {
    const data = await api('/api/register/read', { method: 'POST', body: JSON.stringify({ offset: $('regOffset').value }) });
    $('regValue').value = data.value;
    $('regResult').textContent = JSON.stringify(data, null, 2);
  } catch (err) {
    $('regResult').textContent = `Read failed: ${err.message}`;
    toast(`Register read failed: ${err.message}`, 'bad');
  }
}

async function writeRegister() {
  try {
    const data = await api('/api/register/write', { method: 'POST', body: JSON.stringify({ offset: $('regOffset').value, value: $('regValue').value }) });
    $('regResult').textContent = JSON.stringify(data, null, 2);
    toast('Register written', 'ok');
  } catch (err) {
    $('regResult').textContent = `Write failed: ${err.message}`;
    toast(`Register write failed: ${err.message}`, 'bad');
  }
}

// ── FDB (Scenario Lab panel) ──────────────────────────────────────────────────
function fdbPayload() {
  return { mac: $('fdbMac').value.trim(), port: Number($('fdbPort').value) || 0, vlanValid: $('fdbVlanValid').checked, vlanId: Number($('fdbVlanId').value) || 0 };
}

async function fdbCall(path, payload = fdbPayload()) {
  try {
    const data = await api(path, { method: 'POST', body: JSON.stringify(payload) });
    $('fdbResult').textContent = JSON.stringify(data, null, 2);
    toast(data.status || 'FDB done', 'ok');
  } catch (err) {
    $('fdbResult').textContent = `FDB failed: ${err.message}`;
    toast(`FDB failed: ${err.message}`, 'bad');
  }
}

// ── Register Viewer (HyperTerminal tab) ──────────────────────────────────────
async function rvRead(offset, valId, statusId) {
  const stEl = $(statusId);
  if (stEl) { stEl.textContent = ''; stEl.className = 'reg-status'; }
  try {
    const data = await api('/api/register/read', { method: 'POST', body: JSON.stringify({ offset }) });
    if (valId) $(valId).value = data.value || `0x${(data.valueDec || 0).toString(16).padStart(8,'0').toUpperCase()}`;
    if (stEl) { stEl.textContent = 'OK'; stEl.className = 'reg-status ok'; }
    return data;
  } catch (err) {
    if (stEl) stEl.textContent = `오류: ${err.message}`;
    throw err;
  }
}

async function rvWrite(offset, value, statusId) {
  const stEl = $(statusId);
  if (stEl) { stEl.textContent = ''; stEl.className = 'reg-status'; }
  try {
    await api('/api/register/write', { method: 'POST', body: JSON.stringify({ offset, value }) });
    if (stEl) { stEl.textContent = '쓰기 완료'; stEl.className = 'reg-status ok'; }
  } catch (err) {
    if (stEl) stEl.textContent = `오류: ${err.message}`;
    throw err;
  }
}

function initRegViewer() {
  // Generic data-rw buttons (data-off = ID of offset input, data-off-val = literal offset)
  document.getElementById('regContent').addEventListener('click', async e => {
    const btn = e.target.closest('[data-rw]');
    if (!btn) return;
    const rw = btn.dataset.rw;
    const valId = btn.dataset.val;
    const stId = btn.dataset.st;
    // offset can come from a literal value or an input element
    const offset = btn.dataset.offVal || (btn.dataset.off ? $(btn.dataset.off).value : null);
    if (!offset) return;
    try {
      if (rw === 'read') {
        await rvRead(offset, valId, stId);
      } else if (rw === 'write') {
        const val = valId ? $(valId).value : '0x00000000';
        await rvWrite(offset, val, stId);
      }
    } catch { /* status already updated */ }
  });

  // READ ALL buttons
  $('sysctlReadAll')?.addEventListener('click', async () => {
    await Promise.allSettled([
      rvRead('0x000', 'rv-version', 'rv-st-version'),
      rvRead('0x008', 'rv-enable', 'rv-st-enable'),
      rvRead('0x00C', 'rv-ahb', 'rv-st-ahb'),
    ]);
  });

  $('interruptReadAll')?.addEventListener('click', async () => {
    const offCtrl = $('rv-intr-ctrl-off').value;
    const offRaw  = $('rv-intr-raw-off').value;
    const offMask = $('rv-intr-mask-off').value;
    const offSw   = $('rv-intr-sw-off').value;
    await Promise.allSettled([
      rvRead(offCtrl, 'rv-intr-ctrl', 'rv-st-intr-ctrl'),
      rvRead(offRaw,  'rv-intr-raw',  'rv-st-intr-raw'),
      rvRead(offMask, 'rv-intr-mask', 'rv-st-intr-mask'),
      rvRead(offSw,   'rv-intr-sw',   'rv-st-intr-sw'),
    ]);
  });

  $('timestampReadAll')?.addEventListener('click', async () => {
    await Promise.allSettled([
      rvRead($('rv-ts-ns-off').value,    'rv-ts-ns',    'rv-st-ts'),
      rvRead($('rv-ts-seclo-off').value, 'rv-ts-seclo', 'rv-st-ts'),
      rvRead($('rv-ts-adj-off').value,   'rv-ts-adj',   'rv-st-ts-adj'),
      rvRead($('rv-ts-clk-off').value,   'rv-ts-clk',   'rv-st-ts-clk'),
    ]);
  });

  $('ledclockReadAll')?.addEventListener('click', async () => {
    await Promise.allSettled([
      rvRead($('rv-led-ctrl-off').value,  'rv-led-ctrl',  'rv-st-led'),
      rvRead($('rv-ext-sw-off').value,    'rv-ext-sw',    'rv-st-ext-sw'),
      rvRead($('rv-clk-limit-off').value, 'rv-clk-limit', 'rv-st-clk-limit'),
    ]);
  });

  $('countReadAll')?.addEventListener('click', async () => {
    await rvRead($('rv-count-off').value, 'rv-count-v', 'rv-st-count');
  });

  // FDB operations in Register Viewer
  function rvFdbPayload() {
    return {
      mac: $('rv-fdbMac').value.trim(),
      port: Number($('rv-fdbPort').value) || 0,
      vlanValid: $('rv-fdbVlanValid').checked,
      vlanId: Number($('rv-fdbVid').value) || 0,
    };
  }

  async function rvFdbCall(path, payload = rvFdbPayload()) {
    try {
      const data = await api(path, { method: 'POST', body: JSON.stringify(payload) });
      $('rv-fdbResult').textContent = JSON.stringify(data, null, 2);
      toast(data.status || 'FDB done', 'ok');
    } catch (err) {
      $('rv-fdbResult').textContent = `FDB failed: ${err.message}`;
      toast(`FDB failed: ${err.message}`, 'bad');
    }
  }

  $('rv-fdbRead')?.addEventListener('click', () => rvFdbCall('/api/fdb/read'));
  $('rv-fdbWrite')?.addEventListener('click', () => rvFdbCall('/api/fdb/write'));
  $('rv-fdbDelete')?.addEventListener('click', () => rvFdbCall('/api/fdb/delete'));
  $('rv-fdbFlush')?.addEventListener('click', () => { if (confirm('Flush all FDB entries?')) rvFdbCall('/api/fdb/flush', {}); });
}

// ── TOC Navigation ────────────────────────────────────────────────────────────
function initTocNav() {
  const content = $('regContent');
  document.querySelectorAll('[data-sec]').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('[data-sec]').forEach(b => b.classList.remove('toc-active'));
      btn.classList.add('toc-active');
      const target = document.getElementById(`rsec-${btn.dataset.sec}`);
      if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  });
}

// ── Layout Toggle (⊞/⊟) ──────────────────────────────────────────────────────
function initLayoutToggle() {
  const btn = $('layoutToggle');
  const content = $('hyperContent');
  let horizontal = false;
  btn.addEventListener('click', () => {
    horizontal = !horizontal;
    content.classList.toggle('horizontal', horizontal);
    btn.textContent = horizontal ? '⊟' : '⊞';
    btn.title = horizontal ? '상하 레이아웃으로 전환' : '3분할 레이아웃으로 전환';
  });
}

// ── Splitter Drag ─────────────────────────────────────────────────────────────
function initSplitter() {
  const splitter = $('hyperSplitter');
  const content  = $('hyperContent');
  const terminal = document.querySelector('.hyper-terminal');
  let dragging = false, startPos = 0, startSize = 0;

  splitter.addEventListener('mousedown', e => {
    dragging = true;
    const isH = content.classList.contains('horizontal');
    startPos  = isH ? e.clientX : e.clientY;
    startSize = isH ? terminal.offsetWidth : terminal.offsetHeight;
    e.preventDefault();
  });

  document.addEventListener('mousemove', e => {
    if (!dragging) return;
    const isH  = content.classList.contains('horizontal');
    const delta = (isH ? e.clientX : e.clientY) - startPos;
    const size  = Math.max(80, startSize - delta);
    if (isH) terminal.style.width  = `${size}px`;
    else     terminal.style.height = `${size}px`;
  });

  document.addEventListener('mouseup', () => { dragging = false; });
}

// ── HyperTerminal (Serial) ────────────────────────────────────────────────────
function updateSerialUI(connected, statusText) {
  state.serialConnected = connected;
  const led  = $('serialLed');
  const btn  = $('serialConnect');
  const stEl = $('serialState');
  led.classList.toggle('connected', connected);
  btn.textContent = connected ? '연결 해제' : '연결';
  btn.className   = connected ? 'danger' : 'primary';
  btn.style.width = '80px';
  if (stEl && statusText !== undefined) stEl.textContent = statusText;
}

async function refreshSerialStatus() {
  try {
    const data = await api('/api/serial/status');
    const t = data.terminal || {};

    const portSel = $('serialPort');
    const curPort = portSel.value || t.selectedPort || '';
    portSel.innerHTML = (t.ports || []).map(p =>
      `<option value="${esc(p.portName || p.PortName)}">${esc(p.displayName || p.DisplayName || p.portName || p.PortName)}</option>`
    ).join('');
    if (curPort) portSel.value = curPort;

    const baudSel = $('serialBaud');
    const curBaud = baudSel.value || String(t.selectedBaudRate || 115200);
    baudSel.innerHTML = (t.baudRates || [9600, 19200, 38400, 57600, 115200, 230400, 921600])
      .map(b => `<option value="${b}">${b}</option>`).join('');
    baudSel.value = curBaud;

    updateSerialUI(!!t.isConnected, t.connectionStatus || (t.isConnected ? 'connected' : 'disconnected'));

    const out = $('serialOutput');
    if (t.terminalOutput !== undefined) {
      out.textContent = t.terminalOutput || 'No terminal output.';
      out.scrollTop = out.scrollHeight;
    }
  } catch (err) {
    updateSerialUI(false, `offline — ${err.message}`);
  }
}

async function toggleSerial() {
  try {
    if (state.serialConnected) {
      await api('/api/serial/disconnect', { method: 'POST', body: '{}' });
      toast('Serial disconnected', 'ok');
    } else {
      const port = $('serialPort').value;
      const baud = Number($('serialBaud').value) || 115200;
      await api('/api/serial/connect', { method: 'POST', body: JSON.stringify({ port, baudRate: baud }) });
      toast(`Connected: ${port} @ ${baud}bps`, 'ok');
    }
    await refreshSerialStatus();
  } catch (err) {
    toast(`Serial error: ${err.message}`, 'bad');
    await refreshSerialStatus();
  }
}

function appendHyperTerm(text) {
  const el = $('serialOutput');
  if (!el) return;
  const ts = tsNow();
  el.textContent += `${ts}  ${text}\n`;
  el.scrollTop = el.scrollHeight;
}

async function sendSerial() {
  const text = $('serialInput').value;
  if (!text.trim()) return;
  try {
    await api('/api/serial/send', { method: 'POST', body: JSON.stringify({ text }) });
    $('serialInput').value = '';
    await refreshSerialStatus();
  } catch (err) { toast(`Serial send failed: ${err.message}`, 'bad'); }
}

// ── Logs ──────────────────────────────────────────────────────────────────────
async function loadLogs() {
  try {
    const data = await api('/api/logs');
    $('logsBox').textContent = JSON.stringify(data, null, 2);
  } catch (err) { $('logsBox').textContent = `Log load failed: ${err.message}`; }
}

// ── WebSocket (worker events) ─────────────────────────────────────────────────
function initWebSocket() {
  const ws = new WebSocket(`ws://${location.host}`);
  ws.onmessage = ({ data }) => {
    try {
      const msg = JSON.parse(data);
      if (msg.type === 'workerEvent') {
        const p = msg.payload || {};
        if (p.type === 'serialData' || p.type === 'terminal') {
          const text = p.text || p.data || '';
          appendHyperTerm(text);
          appendSeqTerm(text);
        }
      }
    } catch { /* ignore parse errors */ }
  };
  ws.onclose = () => setTimeout(initWebSocket, 3000);
}

// ── Init ──────────────────────────────────────────────────────────────────────
async function init() {
  initTabs();
  initWebSocket();
  initLayoutToggle();
  initSplitter();
  initTocNav();
  initRegViewer();

  $('startTime').textContent = new Date().toLocaleTimeString();

  // Packet Generator
  $('refreshAll').addEventListener('click', refreshInterfaces);
  $('build').addEventListener('click', previewFrame);
  $('send').addEventListener('click', sendFrame);
  ['protocol','dstMac','srcMac','srcIp','dstIp','srcPort','dstPort','payload','vlanEnabled','vlanId','vlanPriority']
    .forEach(id => $(id)?.addEventListener('change', previewFrame));

  // Capture
  $('captureRefresh').addEventListener('click', refreshCaptureStatus);
  $('captureStart').addEventListener('click', startCapture);
  $('captureStop').addEventListener('click', stopCapture);
  $('captureClear').addEventListener('click', clearCapture);
  $('captureFilter').addEventListener('input', renderCaptureRows);

  // Scenario Lab
  $('tcRefresh').addEventListener('click', loadTestCases);
  $('tcAddGroup').addEventListener('click', addTcGroup);
  $('tcAdd').addEventListener('click', addTcFromCurrent);
  $('tcSaveCurrent').addEventListener('click', saveTcCurrent);
  $('seqTermSend').addEventListener('click', seqTermSend);
  $('seqTermInput').addEventListener('keydown', e => { if (e.key === 'Enter') seqTermSend(); });
  $('clearSeqTerminal').addEventListener('click', () => { $('seqTermOutput').textContent = ''; });

  // Register / FDB (Scenario Lab panel)
  $('regStatusRefresh').addEventListener('click', refreshRegStatus);
  $('regRead').addEventListener('click', readRegister);
  $('regWrite').addEventListener('click', writeRegister);
  $('fdbRead').addEventListener('click', () => fdbCall('/api/fdb/read'));
  $('fdbWrite').addEventListener('click', () => fdbCall('/api/fdb/write'));
  $('fdbDelete').addEventListener('click', () => fdbCall('/api/fdb/delete'));
  $('fdbFlush').addEventListener('click', () => { if (confirm('Flush all FDB entries?')) fdbCall('/api/fdb/flush', {}); });

  // HyperTerminal
  $('serialRefresh').addEventListener('click', refreshSerialStatus);
  $('serialConnect').addEventListener('click', toggleSerial);
  $('serialSend').addEventListener('click', sendSerial);
  $('serialClear').addEventListener('click', async () => {
    try { await api('/api/serial/clear', { method: 'POST', body: '{}' }); } catch { /* best effort */ }
    $('serialOutput').textContent = '';
  });
  $('serialInput').addEventListener('keydown', e => { if (e.key === 'Enter') sendSerial(); });

  // Settings
  $('refreshLogs').addEventListener('click', loadLogs);

  try {
    await api('/api/health');
    await refreshInterfaces();
    await loadLogs();
    await refreshSerialStatus();
    await refreshRegStatus();
    await loadTestCases();
    startCapturePolling();
    state.serialTimer = setInterval(refreshSerialStatus, 2000);
  } catch (err) {
    setStatus(`Offline — ${err.message}`, false);
    toast(`Server not reachable: ${err.message}`, 'bad');
  }
}

init();
