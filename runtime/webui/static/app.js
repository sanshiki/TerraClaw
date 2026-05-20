/* ── TerraClaw Agent WebUI — frontend logic ─────────────── */

// ── State ──────────────────────────────────────────────────
const state = {
  ws: null,
  connected: false,
  agentId: '',
  tick: 0,
  observation: null,
  pendingActions: {},    // action_id → entry
  actionHistory: [],     // chronological log
  toolDefs: [],          // from backend
  playerInstructions: [],
  toolCalls: [],         // assembled human tool calls
  obsCount: 0,
};

// ── DOM refs (cached on DOMContentLoaded) ─────────────────
let $ = (sel) => document.querySelector(sel);
let dom = {};

function cacheDom() {
  dom.connStatus = $('#conn-status');
  dom.connText = $('#conn-text');
  dom.agentId = $('#agent-id');
  dom.tickDisplay = $('#tick-display');
  dom.toolCount = $('#tool-count');
  dom.obsCount = $('#obs-count');

  dom.obsPos = $('#obs-pos');
  dom.obsTile = $('#obs-tile');
  dom.obsLayer = $('#obs-layer');
  dom.obsTime = $('#obs-time');
  dom.obsWeather = $('#obs-weather');
  dom.obsHardmode = $('#obs-hardmode');
  dom.obsNpcs = $('#obs-npcs');
  dom.obsItems = $('#obs-items');
  dom.obsProjectiles = $('#obs-projectiles');
  dom.obsTileList = $('#obs-tile-list');

  dom.pendingList = $('#pending-list');
  dom.instrInput = $('#instr-input');
  dom.instrSendBtn = $('#instr-send-btn');
  dom.instrList = $('#instr-list');
  dom.responseText = $('#response-text');
  dom.toolSelector = $('#tool-selector');
  dom.addToolBtn = $('#add-tool-btn');
  dom.toolCallList = $('#tool-call-list');
  dom.submitBtn = $('#submit-btn');
  dom.clearToolsBtn = $('#clear-tools-btn');
  dom.historyList = $('#history-list');
  dom.contextToggle = $('#context-toggle');
  dom.contextBody = $('#context-body');
  dom.contextContent = $('#context-content');

  dom.instrInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') sendInstruction(); });
  dom.instrSendBtn.addEventListener('click', sendInstruction);
  dom.addToolBtn.addEventListener('click', addToolCall);
  dom.submitBtn.addEventListener('click', submitResponse);
  dom.clearToolsBtn.addEventListener('click', clearAllTools);
  dom.toolSelector.addEventListener('change', onToolSelectChange);
  dom.contextToggle.addEventListener('click', toggleContext);
}

// ── WebSocket ──────────────────────────────────────────────
function connectWs() {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) return;
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const url = `${proto}//${location.host}/ws`;
  state.ws = new WebSocket(url);

  state.ws.onopen = () => {
    state.connected = true;
    updateConnectionUI();
  };

  state.ws.onclose = () => {
    state.connected = false;
    updateConnectionUI();
    setTimeout(connectWs, 2000);
  };

  state.ws.onerror = () => {};

  state.ws.onmessage = (event) => {
    try {
      const msg = JSON.parse(event.data);
      handleMessage(msg);
    } catch (e) {
      console.error('WS parse error', e);
    }
  };
}

function wsSend(obj) {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) {
    state.ws.send(JSON.stringify(obj));
  }
}

// ── Message Handler ───────────────────────────────────────
function handleMessage(msg) {
  switch (msg.type) {
    case 'connected':
      state.agentId = msg.agent_id || '?';
      dom.toolCount.textContent = msg.tool_count || 0;
      updateConnectionUI();
      break;

    case 'observation':
      state.observation = msg.data;
      state.tick = msg.data.tick || 0;
      state.obsCount++;
      renderObservation(msg.data);
      dom.tickDisplay.textContent = state.tick;
      dom.obsCount.textContent = `Obs: ${state.obsCount}`;
      break;

    case 'tool_defs':
      state.toolDefs = msg.data || [];
      updateToolSelector();
      dom.toolCount.textContent = state.toolDefs.length;
      break;

    case 'state_snapshot':
      if (msg.tick) dom.tickDisplay.textContent = msg.tick;
      if (msg.pending) {
        state.pendingActions = {};
        for (const entry of msg.pending) {
          state.pendingActions[entry.action_id] = entry;
        }
        renderPendingActions();
      }
      if (msg.history_tail) {
        for (const entry of msg.history_tail) {
          const exists = state.actionHistory.some(e =>
            e.type === entry.type &&
            e.timestamp === entry.timestamp &&
            (entry.action_id ? e.action_id === entry.action_id : true)
          );
          if (!exists) {
            state.actionHistory.push(entry);
          }
        }
        renderHistory();
      }
      break;

    case 'tool_result':
      addHistoryEntry({ type: 'tool_result', tool: msg.tool, result: msg.result });
      break;

    case 'action_resolved':
      delete state.pendingActions[msg.action_id];
      renderPendingActions();
      break;

    case 'instruction_accepted':
      dom.instrInput.value = '';
      break;

    case 'instructions_cleared':
      state.playerInstructions = [];
      renderInstructions();
      break;

    case 'context_preview':
      dom.contextContent.textContent = msg.data || '(empty)';
      break;

    case 'human_response_accepted':
      state.toolCalls = [];
      dom.responseText.value = '';
      renderToolCallList();
      break;
  }
}

// ── UI Updates ────────────────────────────────────────────

function updateConnectionUI() {
  const el = dom.connStatus;
  if (state.connected) {
    el.className = 'status-dot connected';
    dom.connText.textContent = `Connected (${state.agentId})`;
  } else {
    el.className = 'status-dot disconnected';
    dom.connText.textContent = 'Disconnected';
  }
}

function renderObservation(data) {
  const a = data.agent || {};
  const w = data.world || {};

  dom.obsPos.textContent = `(${a.position?.x?.toFixed(0) || '?'}, ${a.position?.y?.toFixed(0) || '?'}) px`;
  dom.obsTile.textContent = `(${a.tile_position?.[0] || '?'}, ${a.tile_position?.[1] || '?'})`;
  dom.obsLayer.textContent = w.depth_layer || '?';
  dom.obsTime.textContent = formatTime(w.time);
  dom.obsWeather.textContent = w.weather || 'clear';
  dom.obsHardmode.textContent = w.hardmode ? 'Yes' : 'No';

  // Entities
  const entities = data.entities || {};
  renderNpcs(entities.npcs || []);
  renderItems(entities.items_on_ground || []);
  renderProjectiles(entities.projectiles || []);

  // Tiles
  const spatial = data.spatial_window || {};
  const interesting = (spatial.tiles || {}).interesting || [];
  renderTiles(interesting);
}

function formatTime(t) {
  if (!t) return '?';
  const h = t.hour || 0;
  const m = t.minute || 0;
  return `${h}:${String(m).padStart(2, '0')} ${t.is_day ? '(day)' : '(night)'}`;
}

function renderNpcs(npcs) {
  if (!npcs.length) { dom.obsNpcs.innerHTML = '<em>No NPCs</em>'; return; }
  dom.obsNpcs.innerHTML = npcs.map(n => {
    const h = n.is_hostile ? 'tag-hostile' : 'tag-friendly';
    const b = n.is_boss ? ' BOSS' : '';
    return `<div class="entity-item"><span class="tag ${h}">${n.is_hostile ? 'H' : 'F'}${n.is_boss ? '!B' : ''}</span>${n.type} at (${n.position?.x?.toFixed(0) || '?'}, ${n.position?.y?.toFixed(0) || '?'}) dist=${n.distance_to_center?.toFixed(0) || '?'}px</div>`;
  }).join('');
}

function renderItems(items) {
  if (!items.length) { dom.obsItems.innerHTML = '<em>No items</em>'; return; }
  dom.obsItems.innerHTML = items.slice(0, 10).map(it =>
    `<div class="entity-item">${it.type} x${it.stack} dist=${it.distance_to_center?.toFixed(0) || '?'}px</div>`
  ).join('');
}

function renderProjectiles(projs) {
  const hostile = projs.filter(p => p.is_hostile);
  if (!hostile.length) { dom.obsProjectiles.innerHTML = '<em>No hostile projectiles</em>'; return; }
  dom.obsProjectiles.innerHTML = `<div class="entity-item">${hostile.length} hostile nearby</div>`;
}

function renderTiles(tiles) {
  if (!tiles.length) { dom.obsTileList.innerHTML = '<em>None</em>'; return; }
  dom.obsTileList.innerHTML = tiles.slice(0, 20).map(t => {
    const pos = t.pos || {};
    return `<div class="tile-item">(${pos.x}, ${pos.y}) ${t.type}</div>`;
  }).join('');
}

function renderPendingActions() {
  const entries = Object.values(state.pendingActions);
  if (!entries.length) { dom.pendingList.innerHTML = '<em>None</em>'; return; }
  dom.pendingList.innerHTML = entries.map(e => {
    const elapsed = Date.now() / 1000 - (e.start_time || Date.now() / 1000);
    return `<div class="pending-entry">
      <span class="pending-dot"></span>
      <span class="pending-type">${e.action_type || '?'}</span>
      <span class="pending-id">${(e.action_id || '').slice(0, 12)}...</span>
      <span class="pending-time">${elapsed.toFixed(0)}s</span>
    </div>`;
  }).join('');
  refreshActionIdDatalists();
}

function refreshActionIdDatalists() {
  const pendingIds = Object.keys(state.pendingActions);
  document.querySelectorAll('datalist[id^="action-id-list-"]').forEach(dl => {
    dl.innerHTML = pendingIds.map(id =>
      `<option value="${escapeHtml(id)}">${escapeHtml(id.slice(0, 12))}...</option>`
    ).join('');
  });
}

function renderInstructions() {
  if (!state.playerInstructions.length) { dom.instrList.innerHTML = '<em>No instructions</em>'; return; }
  dom.instrList.innerHTML = state.playerInstructions.map(t =>
    `<div class="instr-item">› ${escapeHtml(t)}</div>`
  ).join('');
}

function addHistoryEntry(entry) {
  entry.timestamp = entry.timestamp || (Date.now() / 1000);
  state.actionHistory.push(entry);
  if (state.actionHistory.length > 200) {
    state.actionHistory = state.actionHistory.slice(-200);
  }
  renderHistory();
}

function renderHistory() {
  const entries = state.actionHistory.slice(-50);
  if (!entries.length) { dom.historyList.innerHTML = '<em>No actions yet</em>'; return; }

  const baseTime = entries.length > 0 ? entries[0].timestamp : 0;

  dom.historyList.innerHTML = entries.map(e => {
    const ts = formatTimestamp(e.timestamp);
    const elapsed = (e.timestamp - baseTime).toFixed(1);
    let html = `<span class="hist-time">+${elapsed}s</span>`;

    if (e.type === 'human_response') {
      const tc = (e.tool_calls || []).join(', ');
      html += `<span class="hist-text">→ HUMAN:${e.text ? ' "' + escapeHtml(e.text.slice(0, 60)) + '"' : ''}${tc ? ' [' + escapeHtml(tc) + ']' : ''}</span>`;
    } else if (e.type === 'tool_result') {
      const r = e.result || {};
      const status = r.status || (r.error ? 'FAILED' : 'ok');
      const sc = r.error ? 'failed' : (status === 'started' || status === 'running' ? 'running' : '');
      html += `<span class="hist-tool">${escapeHtml(e.tool || '?')}</span>`;
      html += `<span class="hist-status ${sc}">${escapeHtml(status)}</span>`;
      if (r.action_id) {
        html += `<span class="hist-text">${r.action_id.slice(0, 12)}...</span>`;
      }
    } else if (e.type === 'instruction') {
      html += `<span style="color:var(--accent-yellow)">INSTR: ${escapeHtml(e.text)}</span>`;
    } else if (e.type === 'action_result') {
      const s = (e.data && e.data.status) || '?';
      html += `<span class="hist-status">${escapeHtml(e.action_type || '?')} → ${escapeHtml(s)}</span>`;
    }

    return `<div class="hist-entry">${html}</div>`;
  }).join('');

  // Auto-scroll
  dom.historyList.scrollTop = dom.historyList.scrollHeight;
}

function formatTimestamp(ts) {
  const d = new Date(ts * 1000);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
}

// ── Tool Selector & Tool Call Assembly ────────────────────

function updateToolSelector() {
  const sel = dom.toolSelector;
  sel.innerHTML = '<option value="">— Select tool —</option>';
  for (const def of state.toolDefs) {
    const opt = document.createElement('option');
    opt.value = def.name || def.function?.name || '';
    const desc = def.description || def.function?.description || '';
    opt.textContent = opt.value;
    if (desc) opt.title = desc;
    sel.appendChild(opt);
  }
  dom.addToolBtn.disabled = state.toolDefs.length === 0;
}

function onToolSelectChange() {
  dom.addToolBtn.disabled = !dom.toolSelector.value;
}

function addToolCall() {
  const name = dom.toolSelector.value;
  if (!name) return;

  const def = state.toolDefs.find(d => (d.name || d.function?.name) === name);
  if (!def) return;

  const paramsDef = def.input_schema || def.function?.parameters || def.parameters || {};
  const props = paramsDef.properties || {};
  const required = new Set(paramsDef.required || []);

  // Build default arguments
  const args = {};
  for (const [key, prop] of Object.entries(props)) {
    if (prop.type === 'number' || prop.type === 'integer') {
      args[key] = prop.default ?? (required.has(key) ? 0 : '');
    } else if (prop.type === 'string') {
      args[key] = prop.default ?? '';
    } else if (prop.type === 'boolean') {
      args[key] = prop.default ?? false;
    }
  }

  state.toolCalls.push({ name, arguments: args, _def: props, _required: required });
  dom.toolSelector.value = '';
  dom.addToolBtn.disabled = true;
  renderToolCallList();
}

function removeToolCall(index) {
  state.toolCalls.splice(index, 1);
  renderToolCallList();
}

function clearAllTools() {
  state.toolCalls = [];
  renderToolCallList();
}

function updateToolArg(index, key, value) {
  const tc = state.toolCalls[index];
  if (tc) {
    const def = tc._def[key];
    if (def) {
      if (def.type === 'integer') value = parseInt(value) || 0;
      else if (def.type === 'number') value = parseFloat(value) || 0;
      else if (def.type === 'boolean') value = value === true || value === 'true';
    }
    tc.arguments[key] = value;
  }
}

function renderToolCallList() {
  if (!state.toolCalls.length) {
    dom.toolCallList.innerHTML = '<em>No tools added yet. Select a tool above and click "+ Add Tool".</em>';
    return;
  }

  dom.toolCallList.innerHTML = state.toolCalls.map((tc, idx) => {
    const props = tc._def || {};
    const required = tc._required || new Set();
    const isActionControl = tc.name === 'get_action_status' || tc.name === 'cancel_action';

    const paramsHtml = Object.entries(props).map(([key, prop]) => {
      const isReq = required.has(key);
      const val = tc.arguments[key];

      // For action_id on get_action_status/cancel_action: dropdown auto-populated from pending actions
      if (isActionControl && key === 'action_id') {
        const pendingIds = Object.keys(state.pendingActions);
        const hasPending = pendingIds.length > 0;
        return `<div class="param-row">
          <label title="${escapeHtml(prop.description || '')}">${key}${isReq ? '<span class="param-required">*</span>' : ''}</label>
          <input type="text" list="action-id-list-${idx}"
            value="${val === '' || val === undefined ? '' : escapeHtml(String(val))}"
            placeholder="${hasPending ? 'Select or type action_id' : 'No pending actions'}"
            onchange="updateToolArg(${idx}, '${key}', this.value)">
          <datalist id="action-id-list-${idx}">
            ${pendingIds.map(id =>
              `<option value="${escapeHtml(id)}">${escapeHtml(id.slice(0, 12))}...</option>`
            ).join('')}
          </datalist>
        </div>`;
      }

      const inputType = prop.type === 'number' || prop.type === 'integer' ? 'number' : 'text';
      const step = prop.type === 'number' ? '0.1' : '1';
      const placeholder = prop.description || key;
      return `<div class="param-row">
        <label title="${escapeHtml(prop.description || '')}">${key}${isReq ? '<span class="param-required">*</span>' : ''}</label>
        <input type="${inputType}" step="${step}" value="${val === '' || val === undefined ? '' : val}"
          placeholder="${escapeHtml(placeholder)}"
          onchange="updateToolArg(${idx}, '${key}', this.value)">
      </div>`;
    }).join('');

    return `<div class="tool-call-card">
      <div class="tool-call-header">
        <span style="color:var(--accent)">${idx + 1}.</span>
        <span>${escapeHtml(tc.name)}</span>
        <button class="remove-tool-btn" onclick="removeToolCall(${idx})" title="Remove tool">✕</button>
      </div>
      <div class="tool-call-params">${paramsHtml || '<em style="font-size:11px;color:var(--text-dim)">No parameters</em>'}</div>
    </div>`;
  }).join('');
}

// ── Submit Response ───────────────────────────────────────
function submitResponse() {
  if (!state.toolCalls.length && !dom.responseText.value.trim()) {
    alert('Add at least one tool call or enter text.');
    return;
  }

  const toolCalls = state.toolCalls.map(tc => ({
    name: tc.name,
    arguments: { ...tc.arguments },
  }));

  // Clean empty argument values
  for (const tc of toolCalls) {
    for (const [k, v] of Object.entries(tc.arguments)) {
      if (v === '' || v === undefined) delete tc.arguments[k];
    }
  }

  wsSend({
    type: 'human_response',
    text: dom.responseText.value.trim(),
    tool_calls: toolCalls,
  });

  // Clear instructions on submit
  state.playerInstructions = [];
  renderInstructions();
}

// ── Instructions ──────────────────────────────────────────
function sendInstruction() {
  const text = dom.instrInput.value.trim();
  if (!text) return;
  state.playerInstructions.push(text);
  renderInstructions();
  wsSend({ type: 'instruction', text });
}

// ── Context Preview Toggle ────────────────────────────────
function toggleContext() {
  const isOpen = dom.contextBody.style.display !== 'none';
  dom.contextBody.style.display = isOpen ? 'none' : 'block';
  dom.contextToggle.classList.toggle('open', !isOpen);

  if (!isOpen) {
    // Request context preview from backend
    wsSend({ type: 'get_context_preview' });
  }
}

// ── Utility ───────────────────────────────────────────────
function escapeHtml(s) {
  if (typeof s !== 'string') s = String(s);
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// ── Init ──────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
  cacheDom();
  connectWs();

  // Periodic ping to keep connection alive
  setInterval(() => wsSend({ type: 'ping' }), 15000);

  // Periodic pending actions refresh
  setInterval(renderPendingActions, 500);

  console.log('TerraClaw WebUI initialized');
});
