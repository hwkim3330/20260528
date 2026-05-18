const $ = (id) => document.getElementById(id);

const state = {
  interfaces: [],
  senderInterface: '',
  captureInterfaces: new Set(),
  captureRows: [],
  captureTimer: null,
  serialTimer: null,
  serialReader: null,
  streamActive: false,
  selectedGroupIndex: 0,
  selectedTestCaseIndex: null,
};

async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: { 'content-type': 'application/json', ...(options.headers || {}) },
  });
  const data = await res.json();
  if (!res.ok || data.ok === false) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

function toast(message, kind = 'info') {
  const tray = $('toastTray');
  const node = document.createElement('div');
  node.className = `toast ${kind}`;
  node.textContent = message;
  tray.appendChild(node);
  setTimeout(() => node.remove(), 4200);
}

function setStatus(text, ok = true) {
  $('status').textContent = text;
  $('serverState').classList.toggle('bad', !ok);
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function initTabs() {
  document.querySelectorAll('.tab').forEach((tab) => {
    tab.addEventListener('click', () => {
      document.querySelectorAll('.tab').forEach((t) => t.classList.remove('active'));
      document.querySelectorAll('.view').forEach((v) => v.classList.remove('active'));
      tab.classList.add('active');
      $(tab.dataset.view)?.classList.add('active');
    });
  });
}

async function refreshInterfaces() {
  const data = await api('/api/interfaces');
  state.interfaces = data.interfaces || [];
  renderSenderInterfaces();
  await refreshCaptureStatus();
  setStatus(`Connected - ${state.interfaces.length} interfaces`);
}

function renderSenderInterfaces() {
  const wrap = $('senderInterfaces');
  wrap.innerHTML = '';
  if (!state.interfaces.length) {
    wrap.innerHTML = '<p class="empty">No interfaces found.</p>';
    return;
  }

  for (const iface of state.interfaces) {
    const btn = document.createElement('button');
    btn.className = `chip ${iface.state === 'up' ? 'up' : 'down'}`;
    btn.textContent = `${iface.name} ${iface.state || ''}`;
    btn.title = `${iface.mac || ''}`;
    btn.addEventListener('click', () => {
      state.senderInterface = iface.name;
      document.querySelectorAll('#senderInterfaces .chip').forEach((b) => b.classList.remove('selected'));
      btn.classList.add('selected');
      if (!$('srcMac').value && iface.mac) $('srcMac').value = iface.mac;
      if (!$('srcIp').value && iface.ipv4?.[0]?.local) $('srcIp').value = iface.ipv4[0].local;
    });
    wrap.appendChild(btn);
  }
}

function buildProfile() {
  const profile = {
    protocol: $('protocol').value,
    interface: state.senderInterface || null,
    dstMac: $('dstMac').value.trim(),
    srcMac: $('srcMac').value.trim(),
    srcIp: $('srcIp').value.trim(),
    dstIp: $('dstIp').value.trim(),
    udp: {
      srcPort: Number($('srcPort').value) || 12345,
      dstPort: Number($('dstPort').value) || 50000,
    },
    count: Number($('count').value) || 1,
    intervalMs: Number($('intervalMs').value) || 0,
    payload: { mode: 'text', data: $('payload').value },
  };

  if ($('vlanEnabled').checked) {
    profile.vlan = {
      enabled: true,
      id: Number($('vlanId').value) || 100,
      priority: Number($('vlanPriority').value) || 0,
    };
  }
  return profile;
}

function formatHex(hex) {
  if (!hex) return '';
  const bytes = hex.match(/.{1,2}/g) || [];
  const out = [];
  for (let offset = 0; offset < bytes.length; offset += 16) {
    const chunk = bytes.slice(offset, offset + 16);
    const ascii = chunk.map((b) => {
      const n = parseInt(b, 16);
      return n >= 32 && n <= 126 ? String.fromCharCode(n) : '.';
    }).join('');
    out.push(`${offset.toString(16).padStart(4, '0')}  ${chunk.join(' ').padEnd(47, ' ')}  ${ascii}`);
  }
  return out.join('\n');
}

async function previewFrame() {
  try {
    const data = await api('/api/build', { method: 'POST', body: JSON.stringify(buildProfile()) });
    const out = data.stdout || data;
    $('decoded').textContent = JSON.stringify(out.decoded || {}, null, 2);
    $('hexdump').textContent = formatHex(out.frameHex);
    toast('Preview updated', 'ok');
  } catch (err) {
    toast(`Build failed: ${err.message}`, 'bad');
  }
}

async function sendFrame() {
  if (!state.senderInterface) {
    toast('Select a sender interface first', 'warn');
    return;
  }
  try {
    const data = await api('/api/send', { method: 'POST', body: JSON.stringify(buildProfile()) });
    const out = data.stdout || data;
    toast(`Sent ${out.framesSent || 1} frame(s), ${out.bytesSent || '?'} bytes`, 'ok');
  } catch (err) {
    toast(`Send failed: ${err.message}`, 'bad');
  }
}

async function refreshCaptureStatus() {
  const data = await api('/api/capture/status');
  $('captureRunning').textContent = data.running ? 'capturing' : 'idle';
  $('captureTotal').textContent = `${data.totalPackets || 0} packets`;

  const list = $('captureInterfaces');
  list.innerHTML = '';
  state.captureInterfaces = new Set((data.interfaces || []).filter((i) => i.selected).map((i) => i.name));

  for (const iface of data.interfaces || []) {
    const label = document.createElement('label');
    label.className = 'check-row';
    label.innerHTML = `
      <input type="checkbox" ${iface.selected ? 'checked' : ''} value="${escapeHtml(iface.name)}">
      <span><strong>${escapeHtml(iface.name)}</strong><small>${escapeHtml(iface.description || iface.state || '')}</small></span>
    `;
    label.querySelector('input').addEventListener('change', (e) => {
      if (e.target.checked) state.captureInterfaces.add(iface.name);
      else state.captureInterfaces.delete(iface.name);
    });
    list.appendChild(label);
  }
}

async function startCapture() {
  try {
    await api('/api/capture/start', {
      method: 'POST',
      body: JSON.stringify({ interfaces: Array.from(state.captureInterfaces) }),
    });
    toast('Capture started', 'ok');
    startCapturePolling();
    await refreshCaptureStatus();
  } catch (err) {
    toast(`Capture start failed: ${err.message}`, 'bad');
  }
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
  } catch {
    // keep UI stable if capture is temporarily unavailable
  }
}

function rowMatchesFilter(row, filter) {
  if (!filter) return true;
  const text = `${row.no} ${row.time} ${row.interfaceName} ${row.source} ${row.destination} ${row.protocol} ${row.length} ${row.info} ${row.srcMac} ${row.dstMac}`.toLowerCase();
  return filter.split(/\s+/).filter(Boolean).every((token) => {
    if (token.startsWith('mac:')) return `${row.srcMac} ${row.dstMac}`.toLowerCase().includes(token.slice(4));
    if (token.startsWith('ip:')) return `${row.source} ${row.destination}`.toLowerCase().includes(token.slice(3));
    if (token.startsWith('port:')) return `${row.source} ${row.destination} ${row.info}`.toLowerCase().includes(token.slice(5));
    return text.includes(token);
  });
}

function renderCaptureRows() {
  const tbody = $('captureRows');
  const filter = $('captureFilter').value.trim().toLowerCase();
  const rows = state.captureRows.filter((r) => rowMatchesFilter(r, filter));
  if (!rows.length) {
    tbody.innerHTML = '<tr><td colspan="10" class="empty">No packets match.</td></tr>';
    return;
  }

  tbody.innerHTML = rows.map((r, idx) => `
    <tr data-index="${idx}" class="proto-${escapeHtml((r.protocol || '').toLowerCase())}">
      <td>${r.no}</td>
      <td>${escapeHtml(r.time)}</td>
      <td>${escapeHtml(r.interfaceName)}</td>
      <td>${escapeHtml(r.srcMac)}</td>
      <td>${escapeHtml(r.dstMac)}</td>
      <td>${escapeHtml(r.source)}</td>
      <td>${escapeHtml(r.destination)}</td>
      <td><strong>${escapeHtml(r.protocol)}</strong></td>
      <td>${r.length}</td>
      <td>${escapeHtml(r.info)}</td>
    </tr>
  `).join('');

  Array.from(tbody.querySelectorAll('tr')).forEach((tr) => {
    tr.addEventListener('click', () => {
      tbody.querySelectorAll('tr').forEach((r) => r.classList.remove('selected'));
      tr.classList.add('selected');
      const row = rows[Number(tr.dataset.index)];
      $('packetDetails').textContent = row.detailText || 'No decoded detail.';
      $('packetHex').textContent = row.hexDump || '';
    });
  });
}

async function loadLogs() {
  try {
    const data = await api('/api/logs');
    $('logsBox').textContent = JSON.stringify(data, null, 2);
  } catch (err) {
    $('logsBox').textContent = `Log load failed: ${err.message}`;
  }
}

async function loadTestCases() {
  try {
    const data = await api('/api/testcases/status');
    const tc = data.snapshot || data.testCases || {};
    $('tcStatus').textContent = `${tc.status || ''} Selected: ${tc.selected || '(none)'}`;
    renderTestCaseTree(tc.groups || []);
    renderSequenceRows(tc.sequence || []);
  } catch (err) {
    $('tcStatus').textContent = `Test case load failed: ${err.message}`;
  }
}

function renderTestCaseTree(groups) {
  const root = $('tcTree');
  if (!groups.length) {
    root.innerHTML = '<p class="empty">No groups. Add one.</p>';
    return;
  }
  root.innerHTML = groups.map((g) => `
    <section class="tc-group">
      <div class="tc-group-head">
        <strong>${escapeHtml(g.name)}</strong>
        <button class="small danger tc-delete-group" data-group="${g.index}">Del</button>
      </div>
      ${(g.testCases || []).map((t) => `
        <div class="tc-item-row">
          <button class="tc-item ${t.selected ? 'selected' : ''}" data-group="${t.groupIndex}" data-tc="${t.index}">
            <span>${escapeHtml(t.name)}</span><small>${t.itemCount} items</small>
          </button>
          <button class="small danger tc-delete-tc" data-group="${t.groupIndex}" data-tc="${t.index}" title="Delete TC">&times;</button>
        </div>
      `).join('')}
    </section>
  `).join('');
  root.querySelectorAll('.tc-item').forEach((btn) => btn.addEventListener('click', async () => {
    state.selectedGroupIndex = Number(btn.dataset.group);
    state.selectedTestCaseIndex = Number(btn.dataset.tc);
    await api('/api/testcases/select', {
      method: 'POST',
      body: JSON.stringify({ groupIndex: state.selectedGroupIndex, testCaseIndex: state.selectedTestCaseIndex }),
    });
    await loadTestCases();
  }));
  root.querySelectorAll('.tc-delete-group').forEach((btn) => btn.addEventListener('click', async () => {
    if (!confirm('Delete this group?')) return;
    await api('/api/testcases/delete', { method: 'POST', body: JSON.stringify({ groupIndex: Number(btn.dataset.group) }) });
    await loadTestCases();
  }));
  root.querySelectorAll('.tc-delete-tc').forEach((btn) => btn.addEventListener('click', async (e) => {
    e.stopPropagation();
    if (!confirm('Delete this test case?')) return;
    await api('/api/testcases/delete', {
      method: 'POST',
      body: JSON.stringify({ groupIndex: Number(btn.dataset.group), testCaseIndex: Number(btn.dataset.tc) }),
    });
    await loadTestCases();
  }));
}

function renderSequenceRows(items) {
  const tbody = $('sequenceRows');
  if (!items.length) {
    tbody.innerHTML = '<tr><td colspan="8" class="empty">No sequence loaded.</td></tr>';
    return;
  }
  tbody.innerHTML = items.map((item, i) => {
    const isEvent = item.kind === 'Event';
    const name = isEvent ? (item.eventType || '') : (item.packetName || '');
    const addr = isEvent ? (item.address || '') : '';
    const val  = isEvent ? (item.value  || '') : '';
    const mask = isEvent ? (item.mask   || '') : '';
    const timing = isEvent
      ? (item.delayMs ? `delay:${item.delayMs}ms` : item.timeoutMs ? `timeout:${item.timeoutMs}ms` : '')
      : `${(item.blocks || []).length} blocks`;
    return `<tr>
      <td>${i}</td>
      <td>${escapeHtml(item.kind)}</td>
      <td>${item.checked ? '&#10003;' : ''}</td>
      <td><strong>${escapeHtml(name)}</strong></td>
      <td><code>${escapeHtml(addr)}</code></td>
      <td><code>${escapeHtml(val)}</code></td>
      <td><code>${escapeHtml(mask)}</code></td>
      <td>${escapeHtml(timing)}</td>
    </tr>`;
  }).join('');
}

async function addTestCaseGroup() {
  await api('/api/testcases/add-group', { method: 'POST', body: JSON.stringify({ name: $('tcGroupName').value }) });
  $('tcGroupName').value = '';
  await loadTestCases();
}

async function addTestCaseFromCurrent() {
  await api('/api/testcases/add', {
    method: 'POST',
    body: JSON.stringify({ groupIndex: state.selectedGroupIndex || 0, name: $('tcName').value }),
  });
  $('tcName').value = '';
  await loadTestCases();
}

async function saveCurrentToSelected() {
  try {
    await api('/api/testcases/save-current', { method: 'POST', body: '{}' });
    toast('Current sequence saved to selected test case', 'ok');
    await loadTestCases();
  } catch (err) {
    toast(`Save failed: ${err.message}`, 'bad');
  }
}

async function runSequence() {
  try {
    const data = await api('/api/sequence/run', { method: 'POST', body: '{}' });
    toast(data.status === 'already-running' ? 'Sequence already running' : 'Sequence started', 'ok');
  } catch (err) {
    toast(`Run failed: ${err.message}`, 'bad');
  }
}

async function refreshRegisterStatus() {
  try {
    const data = await api('/api/register/status');
    $('regStatus').textContent = `${data.serialConnected ? 'Serial connected' : 'Serial disconnected'} - base ${data.baseAddress || ''}`;
  } catch (err) {
    $('regStatus').textContent = `Register bridge offline: ${err.message}`;
  }
}

async function readRegister() {
  try {
    const data = await api('/api/register/read', {
      method: 'POST',
      body: JSON.stringify({ offset: $('regOffset').value }),
    });
    $('regValue').value = data.value;
    $('regResult').textContent = JSON.stringify(data, null, 2);
  } catch (err) {
    $('regResult').textContent = `Read failed: ${err.message}`;
    toast(`Register read failed: ${err.message}`, 'bad');
  }
}

async function writeRegister() {
  try {
    const data = await api('/api/register/write', {
      method: 'POST',
      body: JSON.stringify({ offset: $('regOffset').value, value: $('regValue').value }),
    });
    $('regResult').textContent = JSON.stringify(data, null, 2);
    toast('Register written', 'ok');
  } catch (err) {
    $('regResult').textContent = `Write failed: ${err.message}`;
    toast(`Register write failed: ${err.message}`, 'bad');
  }
}

function fdbPayload() {
  return {
    mac: $('fdbMac').value.trim(),
    port: Number($('fdbPort').value) || 0,
    vlanValid: $('fdbVlanValid').checked,
    vlanId: Number($('fdbVlanId').value) || 0,
  };
}

async function fdbCall(path, payload = fdbPayload()) {
  try {
    const data = await api(path, { method: 'POST', body: JSON.stringify(payload) });
    $('fdbResult').textContent = JSON.stringify(data, null, 2);
    toast(data.status || 'FDB operation complete', 'ok');
  } catch (err) {
    $('fdbResult').textContent = `FDB failed: ${err.message}`;
    toast(`FDB failed: ${err.message}`, 'bad');
  }
}

async function refreshSerialStatus() {
  const data = await api('/api/serial/status');
  const t = data.terminal || {};

  const portSelect = $('serialPort');
  const currentPort = portSelect.value || t.selectedPort || '';
  portSelect.innerHTML = (t.ports || []).map((p) =>
    `<option value="${escapeHtml(p.portName || p.PortName)}">${escapeHtml(p.displayName || p.DisplayName || p.portName || p.PortName)}</option>`
  ).join('');
  if (currentPort) portSelect.value = currentPort;

  const baudSelect = $('serialBaud');
  const currentBaud = baudSelect.value || String(t.selectedBaudRate || 115200);
  baudSelect.innerHTML = (t.baudRates || [9600, 19200, 38400, 57600, 115200, 230400, 921600])
    .map((b) => `<option value="${b}">${b}</option>`).join('');
  baudSelect.value = currentBaud;

  $('serialState').textContent = t.connectionStatus || (t.isConnected ? 'connected' : 'disconnected');
  $('serialState').classList.toggle('connected', Boolean(t.isConnected));
  if (!state.streamActive) {
    $('serialOutput').textContent = t.terminalOutput || 'No terminal output.';
    $('serialOutput').scrollTop = $('serialOutput').scrollHeight;
  }
}

async function startTtyStream() {
  if (state.serialReader) {
    try { state.serialReader.cancel(); } catch { /* ignore */ }
    state.serialReader = null;
  }
  try {
    const response = await fetch('/api/tty/stream');
    if (!response.ok || !response.body) return;
    const reader = response.body.getReader();
    state.serialReader = reader;
    state.streamActive = true;
    const decoder = new TextDecoder();
    let buf = '';
    (async () => {
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buf += decoder.decode(value, { stream: true });
          const lines = buf.split('\n');
          buf = lines.pop();
          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed) continue;
            try {
              const msg = JSON.parse(trimmed);
              const hex = msg.hex || msg.data || '';
              if (!hex) continue;
              const bytes = hex.match(/.{1,2}/g) || [];
              const text = bytes.map((b) => String.fromCharCode(parseInt(b, 16))).join('');
              const out = $('serialOutput');
              if (out.textContent === 'No terminal output.') out.textContent = '';
              out.textContent += text;
              out.scrollTop = out.scrollHeight;
            } catch { /* malformed line */ }
          }
        }
      } catch { /* stream cancelled or closed */ }
      state.streamActive = false;
    })();
  } catch { /* stream unavailable */ }
}

async function connectSerial() {
  try {
    await api('/api/serial/connect', {
      method: 'POST',
      body: JSON.stringify({
        port: $('serialPort').value,
        baudRate: Number($('serialBaud').value) || 115200,
      }),
    });
    toast('Serial connected', 'ok');
    await refreshSerialStatus();
    startTtyStream();
  } catch (err) {
    toast(`Serial connect failed: ${err.message}`, 'bad');
  }
}

async function disconnectSerial() {
  if (state.serialReader) {
    try { state.serialReader.cancel(); } catch { /* ignore */ }
    state.serialReader = null;
  }
  state.streamActive = false;
  await api('/api/serial/disconnect', { method: 'POST', body: '{}' });
  toast('Serial disconnected', 'ok');
  await refreshSerialStatus();
}

async function sendBreak() {
  try {
    await api('/api/serial/break', { method: 'POST', body: '{}' });
    toast('Break signal sent', 'ok');
  } catch (err) {
    toast(`Break failed: ${err.message}`, 'bad');
  }
}

async function sendSerial() {
  const text = $('serialInput').value;
  if (!text.trim()) return;
  try {
    await api('/api/serial/send', { method: 'POST', body: JSON.stringify({ text }) });
    $('serialInput').value = '';
    await refreshSerialStatus();
  } catch (err) {
    toast(`Serial send failed: ${err.message}`, 'bad');
  }
}

async function clearSerial() {
  await api('/api/serial/clear', { method: 'POST', body: '{}' });
  await refreshSerialStatus();
}

async function init() {
  initTabs();
  $('refreshAll').addEventListener('click', refreshInterfaces);
  $('captureRefresh').addEventListener('click', refreshCaptureStatus);
  $('build').addEventListener('click', previewFrame);
  $('send').addEventListener('click', sendFrame);
  $('captureStart').addEventListener('click', startCapture);
  $('captureStop').addEventListener('click', stopCapture);
  $('captureClear').addEventListener('click', clearCapture);
  $('captureFilter').addEventListener('input', renderCaptureRows);
  $('refreshLogs').addEventListener('click', loadLogs);
  $('serialRefresh').addEventListener('click', refreshSerialStatus);
  $('serialConnect').addEventListener('click', connectSerial);
  $('serialDisconnect').addEventListener('click', disconnectSerial);
  $('serialBreak').addEventListener('click', sendBreak);
  $('serialSend').addEventListener('click', sendSerial);
  $('serialClear').addEventListener('click', clearSerial);
  $('serialInput').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') sendSerial();
  });
  $('regStatusRefresh').addEventListener('click', refreshRegisterStatus);
  $('regRead').addEventListener('click', readRegister);
  $('regWrite').addEventListener('click', writeRegister);
  $('fdbRead').addEventListener('click', () => fdbCall('/api/fdb/read'));
  $('fdbWrite').addEventListener('click', () => fdbCall('/api/fdb/write'));
  $('fdbDelete').addEventListener('click', () => fdbCall('/api/fdb/delete'));
  $('fdbFlush').addEventListener('click', () => {
    if (confirm('Flush all FDB entries?')) fdbCall('/api/fdb/flush', {});
  });
  $('tcRefresh').addEventListener('click', loadTestCases);
  $('tcAddGroup').addEventListener('click', addTestCaseGroup);
  $('tcAdd').addEventListener('click', addTestCaseFromCurrent);
  $('tcSaveCurrent').addEventListener('click', saveCurrentToSelected);
  $('tcRunSequence').addEventListener('click', runSequence);
  ['protocol', 'dstMac', 'srcMac', 'srcIp', 'dstIp', 'srcPort', 'dstPort', 'payload', 'vlanEnabled', 'vlanId', 'vlanPriority'].forEach((id) => {
    $(id)?.addEventListener('change', previewFrame);
  });

  try {
    await api('/api/health');
    await refreshInterfaces();
    await loadLogs();
    await refreshSerialStatus();
    await refreshRegisterStatus();
    await loadTestCases();
    startCapturePolling();
    state.serialTimer = setInterval(refreshSerialStatus, 1500);
  } catch (err) {
    setStatus(`Offline - ${err.message}`, false);
    toast(`Server not reachable: ${err.message}`, 'bad');
  }
}

init();
