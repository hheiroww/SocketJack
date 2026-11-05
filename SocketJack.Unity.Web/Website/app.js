(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const channels = [
    { id: 'global', name: 'Global', color: '#e6c07b', description: 'Everyone in this game deployment.', public: true },
    { id: 'trade', name: 'Trade', color: '#ffc0c0', description: 'Buy, sell and trade with players across the deployment.', public: true },
    { id: 'zone', name: 'Zone', color: '#f2d18e', description: 'Players in your current game zone.', public: true },
    { id: 'lfg', name: 'Looking for Group', color: '#d6c7a1', description: 'Find players for your next adventure.', public: true },
    { id: 'proximity', name: 'Say', color: '#ffffff', description: 'Players near your active in-game character.', public: true },
    { id: 'yell', name: 'Yell', color: '#ff4040', description: 'Players within your character’s extended voice range.', public: true },
    { id: 'party', name: 'Party', color: '#aaaaff', description: 'Only members of your party.' },
    { id: 'raid', name: 'Raid', color: '#ff7f00', description: 'Only members of your raid.' },
    { id: 'community', name: 'Community', color: '#40ff40', description: 'Members of the selected Community.' },
    { id: 'officer', name: 'Officers', color: '#40c080', description: 'Authorized Community officers.' },
    { id: 'whisper', name: 'Whisper', color: '#ff80ff', description: 'A private conversation with another player.' },
    { id: 'emote', name: 'Emote', color: '#ff8040', description: 'An emote visible near your active in-game character.', public: true },
    { id: 'system', name: 'System', color: '#ffff00', description: 'Server announcements and system messages.' },
    { id: 'combat', name: 'Combat', color: '#ff7070', description: 'Trusted game combat events. Read only.' }
  ];
  const aliases = { world: 'global', g: 'global', global: 'global', trade: 'trade', zone: 'zone', lfg: 'lfg', p: 'party', party: 'party', ra: 'raid', raid: 'raid', c: 'community', community: 'community', o: 'officer', officer: 'officer', s: 'proximity', say: 'proximity', y: 'yell', yell: 'yell', e: 'emote', emote: 'emote', w: 'whisper', whisper: 'whisper', r: 'reply', reply: 'reply' };
  const state = { token: '', user: null, page: 'chat', channel: 'global', scopeId: '', messages: [], ids: new Set(), communities: [], groups: [], invitations: [], selectedCommunity: '', lastWhisper: '', ws: null, wsPending: new Map(), requestId: 0, reconnectTimer: 0, reconnectDelay: 1000, notificationTimer: 0, action: null, accountMode: 'login', pollBusy: false, preferences: { timestamps: true, localHistory: false, colors: {} } };

  function element(tag, className, text) { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = String(text); return node; }
  function button(text, action, className = 'secondary') { const node = element('button', className, text); node.type = 'button'; node.addEventListener('click', action); return node; }
  function notify(message, error = false) { const box = $('notification'); box.textContent = String(message); box.classList.toggle('error', error); box.hidden = false; clearTimeout(state.notificationTimer); state.notificationTimer = setTimeout(() => { box.hidden = true; }, 6500); }
  function fail(error) { notify(error && error.message ? error.message : 'This action could not be completed.', true); }
  function unwrap(body) { if (body && body.ok === false) throw new Error(body.error || 'The server rejected this request.'); return body && Object.prototype.hasOwnProperty.call(body, 'data') ? body.data : body; }
  async function api(operation, data, method) {
    const controller = new AbortController(); const timeout = setTimeout(() => controller.abort(), 12000);
    try {
      const headers = { Accept: 'application/json' }; if (state.token) headers.Authorization = `Bearer ${state.token}`;
      if (data !== undefined) headers['Content-Type'] = 'application/json';
      const response = await fetch(`/api/v1/${operation}`, { method: method || (data === undefined ? 'GET' : 'POST'), headers, body: data === undefined ? undefined : JSON.stringify(data), signal: controller.signal, cache: 'no-store', credentials: 'same-origin' });
      const body = await response.json().catch(() => ({ ok: false, error: `Request failed (${response.status}).` }));
      if (response.status === 401 && state.user && operation !== 'logout') expireSession();
      if (!response.ok) throw new Error(body.error || `Request failed (${response.status}).`);
      return unwrap(body);
    } catch (error) { if (error.name === 'AbortError') throw new Error('The server took too long to respond. Try again.'); throw error; }
    finally { clearTimeout(timeout); }
  }
  function channel(id = state.channel) { return channels.find((entry) => entry.id === id) || channels[0]; }
  function readOnlyChannel(id) { return id === 'system' || id === 'combat'; }
  function channelColor(id) { return state.preferences.colors[id] || channel(id).color; }
  function currentGroup() { return state.groups.find((group) => group.members && group.members.some((member) => member.userId === state.user?.id)) || state.groups[0]; }
  function channelScope(id = state.channel) { if (id === 'community' || id === 'officer') return state.selectedCommunity || ''; if (id === 'party' || id === 'raid') return currentGroup()?.id || ''; return ''; }

  function renderChannelLists() {
    for (const container of [$('public-channels'), $('private-channels'), $('chat-tabs')]) container.replaceChildren();
    for (const entry of channels) {
      const node = button('', () => selectChannel(entry.id), ''); node.dataset.channel = entry.id; node.classList.toggle('active', entry.id === state.channel);
      node.append(element('span', 'channel-mark', '#'), element('span', '', entry.name)); node.style.color = channelColor(entry.id);
      (entry.public ? $('public-channels') : $('private-channels')).append(node);
    }
    for (const id of ['global', 'trade', 'party', 'raid', 'community', 'whisper', 'system', 'combat']) {
      const entry = channel(id); const tab = button(entry.name, () => selectChannel(id), ''); tab.setAttribute('role', 'tab'); tab.setAttribute('aria-selected', String(id === state.channel)); tab.classList.toggle('active', id === state.channel); $('chat-tabs').append(tab);
    }
  }
  function selectChannel(id, scopeId) {
    state.channel = id; if (scopeId !== undefined) { state.scopeId = scopeId; if (id === 'community' || id === 'officer') state.selectedCommunity = scopeId; }
    const entry = channel(); $('channel-title').textContent = entry.name; $('channel-description').textContent = entry.description;
    $('composer-channel').textContent = `[${entry.name}]`; $('composer-channel').style.color = channelColor(id); $('whisper-target-row').hidden = id !== 'whisper';
    $('chat-text').disabled = !state.user || readOnlyChannel(id); $('send-message').disabled = !state.user || readOnlyChannel(id);
    $('chat-context').textContent = ['proximity', 'yell', 'emote', 'zone'].includes(id) ? 'Requires an active in-game character' : (id === 'community' || id === 'officer') && !channelScope(id) ? 'Select a Community to open its chat' : entry.description;
    renderChannelLists(); renderMessages(); if (state.user) loadHistory().catch(fail);
  }
  function matchesView(message) {
    if (message.channel !== state.channel) return false;
    const scope = channelScope(); if ((state.channel === 'community' || state.channel === 'officer' || state.channel === 'party' || state.channel === 'raid') && scope && String(message.scopeId || '') !== String(scope)) return false;
    if (state.channel === 'whisper') { const target = $('whisper-target').value.trim().toLowerCase(); if (target && message.senderName?.toLowerCase() !== target && message.targetName?.toLowerCase() !== target && String(message.targetId || '') !== target) return false; }
    return true;
  }
  function appendTextWithLinks(node, text, approvedLinks = []) {
    if (Array.isArray(approvedLinks) && approvedLinks.length) {
      let cursor = 0;
      for (const link of approvedLinks.slice(0, 16).sort((a, b) => a.start - b.start)) {
        if (!['item', 'player', 'location'].includes(link.kind) || !Number.isInteger(link.start) || !Number.isInteger(link.length) || link.start < cursor || link.length < 1 || link.start + link.length > text.length) continue;
        node.append(document.createTextNode(text.slice(cursor, link.start)));
        node.append(button(`[${link.label}]`, () => {
          const event = new CustomEvent('socketjack:game-link', { detail: { kind: link.kind, id: link.id, label: link.label }, cancelable: true });
          if (window.dispatchEvent(event)) { if (link.kind === 'player') openPlayerActions(link.label, link.id); else notify(`${link.label} · ${link.kind === 'item' ? 'Item' : 'Location'} link. Open this link in the game for details.`); }
        }, 'game-link'));
        cursor = link.start + link.length;
      }
      node.append(document.createTextNode(text.slice(cursor))); return;
    }
    node.append(document.createTextNode(text));
  }
  function messageNode(message) {
    const row = element('div', `message${message.channel === 'system' ? ' system' : ''}`); row.style.setProperty('--message-color', channelColor(message.channel));
    if (state.preferences.timestamps) { const date = new Date(message.timestampUtc); const valid = !Number.isNaN(date.valueOf()); const time = element('time', '', valid ? `[${date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })}]` : '[--:--]'); if (valid) time.dateTime = date.toISOString(); row.append(time); }
    row.append(element('span', 'channel-tag', `[${channel(message.channel).name}]`));
    if (message.senderName) { row.append(button(message.senderName, () => openPlayerActions(message.senderName, message.senderId), 'player-name'), document.createTextNode(': ')); }
    appendTextWithLinks(row, String(message.text || ''), message.links); return row;
  }
  function renderMessages() {
    const box = $('messages'); const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    box.replaceChildren(...state.messages.filter(matchesView).slice(-500).map(messageNode));
    if (atBottom) box.scrollTop = box.scrollHeight; else $('new-messages').hidden = false;
  }
  function receiveMessages(data) {
    const messages = Array.isArray(data) ? data : data?.messages || (data && data.text !== undefined ? [data] : []);
    let changed = false;
    for (const message of messages) {
      if (!message || !message.id || state.ids.has(String(message.id))) continue;
      state.ids.add(String(message.id)); state.messages.push(message); changed = true;
      if (message.channel === 'whisper' && message.senderId !== state.user?.id) state.lastWhisper = { name: message.senderName, id: message.senderId };
    }
    if (!changed) return;
    state.messages.sort((a, b) => String(a.timestampUtc || '').localeCompare(String(b.timestampUtc || '')) || String(a.id).localeCompare(String(b.id)));
    while (state.messages.length > 1000) { const old = state.messages.shift(); state.ids.delete(String(old.id)); }
    renderMessages(); saveLocalHistory();
  }
  function systemMessage(text) { receiveMessages({ id: `local-${Date.now()}-${++state.requestId}`, timestampUtc: new Date().toISOString(), channel: state.channel, scopeId: channelScope(), senderName: '', text }); }
  async function loadHistory() {
    if (!state.user) return;
    const data = await api('chat/history', { channel: state.channel, scopeId: channelScope(), limit: 100, afterId: '' }); receiveMessages(data);
  }
  async function sendChat(text) {
    if (!state.user) { openAccount(); return; }
    let sendChannel = state.channel, targetUsername = $('whisper-target').value.trim(), targetId = $('whisper-target').dataset.accountId || '';
    if (text.startsWith('/')) {
      const split = text.indexOf(' '); const command = text.slice(1, split < 0 ? undefined : split).toLowerCase(); const rest = split < 0 ? '' : text.slice(split + 1).trim();
      if (command === 'help') { systemMessage('/global /trade /zone /lfg /party /raid /community /say /yell /emote · /whisper PLAYER message · /reply message'); return; }
      if (command === 'clear') { clearView(); return; }
      const resolved = aliases[command]; if (!resolved) throw new Error('Unknown command. Type /help for available commands.');
      sendChannel = resolved === 'reply' ? 'whisper' : resolved;
      text = rest;
      if (resolved === 'reply') { if (!state.lastWhisper) throw new Error('There is no whisper to reply to yet.'); targetUsername = state.lastWhisper.name || ''; targetId = state.lastWhisper.id; }
      else if (sendChannel === 'whisper') { targetId = ''; const space = rest.indexOf(' '); if (space < 1) { selectChannel('whisper'); $('whisper-target').value = rest; $('whisper-target').dataset.accountId = ''; $('chat-text').focus(); return; } targetUsername = rest.slice(0, space); text = rest.slice(space + 1).trim(); }
      if (!text) { selectChannel(sendChannel); return; }
    }
    if (!text.trim()) return;
    if (readOnlyChannel(sendChannel)) throw new Error('This channel contains read-only game events.');
    if (sendChannel === 'whisper' && !targetUsername && !targetId) throw new Error('Enter the player you want to whisper to.');
    const payload = { channel: sendChannel, text: text.trim(), scopeId: channelScope(sendChannel), targetId, targetUsername };
    const result = await api('chat/send', payload); receiveMessages(result);
  }
  function clearView() { const removed = state.messages.filter(matchesView); const ids = new Set(removed.map((message) => String(message.id))); state.messages = state.messages.filter((message) => !ids.has(String(message.id))); for (const id of ids) state.ids.delete(id); renderMessages(); saveLocalHistory(); }
  function loadPreferences() {
    try { const saved = JSON.parse(localStorage.getItem('socketjack.chat.preferences') || 'null'); if (saved) state.preferences = { timestamps: saved.timestamps !== false, localHistory: saved.localHistory === true, colors: saved.colors || {} }; } catch { /* Storage may be unavailable in private browsing. */ }
    applyColors();
  }
  function applyColors() { for (const entry of channels) document.documentElement.style.setProperty(`--${entry.id}`, channelColor(entry.id)); }
  function historyStorageKey() { return `socketjack.chat.history.${state.user?.id || 'anonymous'}`; }
  function saveLocalHistory() { if (!state.user || !state.preferences.localHistory) return; try { localStorage.setItem(historyStorageKey(), JSON.stringify(state.messages.slice(-300))); } catch { /* Local history is best-effort and never contains credentials. */ } }
  function restoreLocalHistory() { if (!state.preferences.localHistory || !state.user) return; try { receiveMessages(JSON.parse(localStorage.getItem(historyStorageKey()) || '[]')); } catch { /* Ignore invalid local history. */ } }

  function setPage(page) {
    state.page = ['chat', 'communities', 'groups', 'status', 'admin'].includes(page) ? page : 'chat';
    if (state.page === 'admin' && !isAdmin()) state.page = 'status';
    for (const node of document.querySelectorAll('.page')) node.hidden = node.id !== `page-${state.page}`;
    for (const link of document.querySelectorAll('[data-page]')) { link.classList.toggle('active', link.dataset.page === state.page); if (link.dataset.page === state.page) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current'); }
    if (state.page === 'status') refreshStatus().catch(fail);
    if (state.page === 'communities' || state.page === 'groups') refreshSocial().catch(fail);
    if (state.page === 'admin') refreshDiagnostics().catch(fail);
  }
  function isAdmin() { return state.user && ['admin', 'administrator', 'operator', 'owner'].includes(String(state.user.role || '').toLowerCase()); }
  function updateAccountView() {
    const signedIn = !!state.user; $('account-button').textContent = signedIn ? state.user.displayName || state.user.username : 'Sign in';
    $('chat-sign-in').hidden = signedIn; $('messages').hidden = !signedIn; $('admin-nav').hidden = !isAdmin();
    $('chat-text').disabled = !signedIn || readOnlyChannel(state.channel); $('send-message').disabled = !signedIn || readOnlyChannel(state.channel);
    $('account-tabs').hidden = signedIn; $('account-form').hidden = signedIn; $('signed-in-account').hidden = !signedIn;
    $('account-heading').textContent = signedIn ? 'Your account.' : state.accountMode === 'register' ? 'Make yourself at home.' : state.accountMode === 'bootstrap' ? 'Set up your server.' : 'Welcome back.';
    if (signedIn) {
      $('account-profile').replaceChildren();
      for (const [label, value] of [['Name', state.user.displayName], ['Username', state.user.username], ['Role', state.user.role]]) $('account-profile').append(element('dt', '', label), element('dd', '', value || '—'));
    }
    if (!signedIn && state.page === 'admin') { location.hash = '#status'; setPage('status'); }
  }
  function openAccount(mode) { if (mode) state.accountMode = mode; updateAccountView(); setAccountMode(state.accountMode); $('account-error').hidden = true; $('account-dialog').showModal(); }
  function setAccountMode(mode) {
    state.accountMode = mode;
    for (const button of document.querySelectorAll('[data-account-mode]')) button.classList.toggle('active', button.dataset.accountMode === mode);
    $('display-name-label').hidden = mode === 'login'; $('bootstrap-code-label').hidden = mode !== 'bootstrap'; $('bootstrap-code').required = mode === 'bootstrap';
    $('password').autocomplete = mode === 'login' ? 'current-password' : 'new-password'; $('account-submit').textContent = mode === 'login' ? 'Sign in' : mode === 'register' ? 'Create account' : 'Create operator account';
    updateAccountView();
  }
  function expireSession() { state.token = ''; state.user = null; disconnectSocket(); state.messages = []; state.ids.clear(); updateAccountView(); notify('Your session has ended. Sign in to reconnect.', true); }
  async function signOut() { try { if (state.token) await api('logout', {}); } finally { state.token = ''; state.user = null; disconnectSocket(); state.messages = []; state.ids.clear(); state.communities = []; state.groups = []; updateAccountView(); renderMessages(); $('account-dialog').close(); } }

  function socketRequest(op, data) {
    if (!state.ws || state.ws.readyState !== WebSocket.OPEN) return Promise.reject(new Error('Live connection is unavailable.'));
    const id = String(++state.requestId);
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { state.wsPending.delete(id); reject(new Error('Live request timed out.')); }, 10000);
      state.wsPending.set(id, { resolve, reject, timeout }); state.ws.send(JSON.stringify({ id, op, data }));
    });
  }
  function disconnectSocket() {
    clearTimeout(state.reconnectTimer); if (state.ws) { const socket = state.ws; state.ws = null; socket.onclose = null; socket.close(1000, 'Session ended'); }
    for (const request of state.wsPending.values()) { clearTimeout(request.timeout); request.reject(new Error('Connection closed.')); } state.wsPending.clear();
    setConnection('Offline', false);
  }
  function connectSocket() {
    if (!state.token) return; disconnectSocket(); setConnection('Connecting', false);
    const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws/api`); state.ws = socket;
    socket.onopen = async () => {
      try { await socketRequest('resume', { token: state.token }); state.reconnectDelay = 1000; setConnection('Live', true); await loadHistory(); }
      catch (error) { if (socket.readyState === WebSocket.OPEN) socket.close(); fail(error); }
    };
    socket.onmessage = (event) => {
      let body; try { body = JSON.parse(event.data); } catch { return; }
      if (body.id !== undefined && state.wsPending.has(String(body.id))) { const request = state.wsPending.get(String(body.id)); state.wsPending.delete(String(body.id)); clearTimeout(request.timeout); try { request.resolve(unwrap(body)); } catch (error) { request.reject(error); } return; }
      if (body.event === 'chat.message') receiveMessages(body.data);
      else if (body.event === 'social.changed') refreshSocial().catch(fail);
      else if (body.event === 'status') renderStatus(body.data);
      else if (body.event === 'diagnostics' && isAdmin() && state.page === 'admin') renderMetrics($('diagnostics'), body.data);
    };
    socket.onerror = () => setConnection('Reconnecting', false);
    socket.onclose = () => {
      if (state.ws === socket) state.ws = null;
      for (const request of state.wsPending.values()) { clearTimeout(request.timeout); request.reject(new Error('Live connection closed.')); } state.wsPending.clear();
      setConnection(state.token ? 'Reconnecting' : 'Offline', false);
      if (state.token) { clearTimeout(state.reconnectTimer); state.reconnectTimer = setTimeout(connectSocket, state.reconnectDelay); state.reconnectDelay = Math.min(30000, state.reconnectDelay * 2); }
    };
  }
  function setConnection(text, live) { $('connection-status').textContent = text; $('connection-light').classList.toggle('live', live); $('connection-light').classList.toggle('error', text === 'Offline' || text === 'Unavailable'); }

  function listFrom(data, key) { return Array.isArray(data) ? data : Array.isArray(data?.[key]) ? data[key] : []; }
  async function refreshSocial() {
    if (!state.user) { renderCommunities(); renderGroups(); return; }
    const [communities, groups] = await Promise.all([api('communities'), api('groups')]);
    state.communities = listFrom(communities, 'communities'); state.groups = listFrom(groups, 'groups');
    state.invitations = [...listFrom(communities, 'invitations').map((entry) => ({ ...entry, source: 'communities' })), ...listFrom(groups, 'invitations').map((entry) => ({ ...entry, source: 'groups' }))];
    if (!state.selectedCommunity && state.communities.length) state.selectedCommunity = state.communities[0].id;
    renderCommunities(); renderGroups();
  }
  function requireAccount() { if (state.user) return true; openAccount(); return false; }
  function openAction(title, fields, submitText, action) {
    $('action-title').textContent = title; $('action-fields').replaceChildren(); $('action-error').hidden = true; $('action-submit').textContent = submitText;
    for (const field of fields) {
      const label = element('label', '', field.label); let input;
      if (field.options) { input = element('select'); for (const option of field.options) { const node = element('option', '', option.label || option); node.value = option.value || option; input.append(node); } }
      else input = element(field.multiline ? 'textarea' : 'input');
      input.name = field.name; input.required = field.required !== false; if (field.value !== undefined) input.value = field.value; if (field.maxLength) input.maxLength = field.maxLength; if (field.placeholder) input.placeholder = field.placeholder; label.append(input); $('action-fields').append(label);
    }
    state.action = action; $('action-dialog').showModal();
  }
  function communityInvite(communityId) { openAction('Invite to Community', [{ name: 'targetUsername', label: 'Player username', maxLength: 64 }], 'Send invitation', (values) => api('communities/invite', { ...values, communityId })); }
  function renderCommunities() {
    const list = $('community-list'); list.replaceChildren();
    if (!state.user) { list.append(element('p', 'technical-note', 'Sign in to view your Communities.')); return; }
    if (!state.communities.length) list.append(element('p', 'technical-note', 'No Communities yet. Create one or accept an invitation.'));
    for (const community of state.communities) {
      const item = button('', () => { state.selectedCommunity = community.id; renderCommunities(); }, ''); item.classList.toggle('active', community.id === state.selectedCommunity); item.append(element('span', '', community.name), element('small', '', `${community.members?.length ?? community.memberCount ?? 0} members · ${community.role || 'Community'}`)); list.append(item);
    }
    const detail = $('community-detail'); detail.replaceChildren(); const community = state.communities.find((entry) => entry.id === state.selectedCommunity);
    if (!community) { const empty = element('div', 'empty-state'); empty.append(element('h2', '', 'Find your Community.'), element('p', '', 'Create a Community or accept an invitation to get started.')); detail.append(empty); return; }
    const heading = element('div', 'detail-heading'), description = element('div'); description.append(element('h2', '', community.name), element('p', '', community.description || 'A Community for your players.'));
    const actions = element('div', 'button-row'); actions.append(button('Open chat', () => { selectChannel('community', community.id); location.hash = '#chat'; }), button('Invite player', () => communityInvite(community.id))); heading.append(description, actions); detail.append(heading);
    const members = community.members || []; if (members.length) detail.append(memberTable(members, 'communities', community.id));
    detail.append(button('Leave Community', () => openAction('Leave Community', [], 'Leave Community', () => api('communities/leave', { communityId: community.id })), 'text-button'));
  }
  function memberTable(members, source, id) {
    const table = element('table', 'data-table'), head = element('thead'), headings = element('tr');
    for (const label of ['Player', 'Role', '']) headings.append(element('th', '', label)); head.append(headings); table.append(head); const body = element('tbody');
    for (const member of members) {
      const row = element('tr'), player = element('td'), role = element('td', '', member.role || 'Member'), actions = element('td');
      player.append(button(member.displayName || member.username || member.userId, () => openPlayerActions(member.displayName || member.username, member.userId), 'player-name'));
      if (member.userId !== state.user?.id) {
        if (source === 'communities') actions.append(button('Role', () => openAction('Change Community role', [{ name: 'role', label: 'Role', options: ['member', 'officer', 'owner'] }], 'Change role', (values) => api('communities/role', { ...values, communityId: id, targetId: member.userId })), 'text-button'));
        actions.append(button('Remove', () => openAction('Remove player', [], 'Remove player', () => api(`${source}/remove`, { [source === 'communities' ? 'communityId' : 'groupId']: id, targetId: member.userId })), 'text-button'));
      }
      row.append(player, role, actions); body.append(row);
    }
    table.append(body); return table;
  }
  function renderGroups() {
    const detail = $('group-detail'); detail.replaceChildren();
    if (!state.user) { detail.append(element('p', 'muted', 'Sign in to create a party or accept an invitation.')); return; }
    if (!state.groups.length) { const empty = element('div', 'empty-state'); empty.append(element('h2', '', 'Adventure is better together.'), element('p', '', 'Create a party or raid, then invite players by username.')); detail.append(empty); }
    for (const group of state.groups) {
      const section = element('section'), heading = element('div', 'detail-heading'), description = element('div'); description.append(element('h2', '', group.name || (String(group.kind || group.type).toLowerCase() === 'raid' ? 'Your raid' : 'Your party')), element('p', '', `${group.members?.length || 0} players`));
      const actions = element('div', 'button-row'); actions.append(button('Open chat', () => { selectChannel(String(group.kind || group.type).toLowerCase() === 'raid' ? 'raid' : 'party', group.id); location.hash = '#chat'; }), button('Invite player', () => inviteGroup(group.id)), button('Leave', () => openAction('Leave group', [], 'Leave group', () => api('groups/leave', { groupId: group.id })), 'text-button')); heading.append(description, actions); section.append(heading); if (group.members?.length) section.append(memberTable(group.members, 'groups', group.id)); detail.append(section);
    }
    const invitations = $('invitations-list'); invitations.replaceChildren();
    if (!state.invitations.length) invitations.append(element('p', 'muted small', 'No pending invitations.'));
    for (const invitation of state.invitations) {
      const row = element('div', 'invitation'), actions = element('div', 'button-row'); row.append(element('span', '', invitation.name || invitation.communityName || invitation.groupName || (invitation.source === 'communities' ? 'Community invitation' : 'Group invitation')));
      for (const [label, accept] of [['Accept', true], ['Decline', false]]) actions.append(button(label, async () => { try { await api(`${invitation.source}/respond`, { invitationId: invitation.id, accept }); await refreshSocial(); } catch (error) { fail(error); } })); row.append(actions); invitations.append(row);
    }
  }
  function inviteGroup(groupId, targetUsername = '') { openAction('Invite player', [{ name: 'targetUsername', label: 'Player username', value: targetUsername, maxLength: 64 }], 'Send invitation', (values) => api('groups/invite', { ...values, groupId })); }
  function openPlayerActions(name, id) {
    if (!requireAccount()) return;
    openAction(name || 'Player', [{ name: 'action', label: 'Action', options: [{ label: 'Whisper', value: 'whisper' }, { label: 'Invite to party', value: 'party' }, { label: 'Ignore messages', value: 'ignore' }, { label: 'Report player', value: 'report' }] }], 'Continue', async ({ action }) => {
      if (action === 'whisper') { $('whisper-target').value = name || id; $('whisper-target').dataset.accountId = id; $('whisper-target').dataset.displayName = name || id; selectChannel('whisper'); location.hash = '#chat'; return; }
      if (action === 'party') { const group = currentGroup(); if (!group) throw new Error('Create a party before inviting players.'); await api('groups/invite', { groupId: group.id, targetId: id, targetUsername: name }); return; }
      const message = action === 'report' ? state.messages.slice().reverse().find((entry) => entry.senderId === id) : null;
      if (action === 'report' && !message) throw new Error('Select a player with a recent message to report.');
      await api(action === 'ignore' ? 'chat/ignore' : 'chat/report', { targetId: id, targetUsername: name, messageId: message?.id || '', reason: action === 'report' ? 'Player report' : '' }); notify(action === 'ignore' ? 'Player ignored.' : 'Report submitted.');
    });
  }
  function renderMetrics(container, data) {
    container.replaceChildren();
    if (!data) { container.append(element('p', 'technical-note', 'No measurements are available yet.')); return; }
    for (const [key, value] of Object.entries(data)) {
      if (value === null || value === undefined || typeof value === 'object') continue;
      const metric = element('div', 'metric'), label = key.replace(/([A-Z])/g, ' $1').replace(/[_-]/g, ' ');
      metric.append(element('span', 'metric-label', label), element('span', `metric-value${typeof value === 'number' ? '' : ' text-value'}`, typeof value === 'boolean' ? value ? 'Yes' : 'No' : typeof value === 'number' ? Number.isInteger(value) ? value.toLocaleString() : value.toFixed(2) : value)); container.append(metric);
    }
  }
  function renderStatus(data) {
    if (!data) return; $('deployment-name').textContent = data.name || 'MULTIPLAYER'; document.title = `${data.name || 'SocketJack'} · Multiplayer`; $('version-label').textContent = `SocketJack ${data.version || ''}`.trim();
    renderMetrics($('server-status'), data); $('server-status-note').textContent = 'These are server-reported measurements. A working website or WebSocket connection does not prove UDP reachability or a particular game-world player capacity.';
    if (!state.user) setConnection('Server online', true);
  }
  async function refreshStatus() { try { renderStatus(await api('status')); } catch (error) { setConnection('Unavailable', false); throw error; } }
  async function refreshDiagnostics() { if (!isAdmin()) return; renderMetrics($('diagnostics'), await api('diagnostics')); }

  $('account-button').addEventListener('click', () => openAccount());
  for (const button of document.querySelectorAll('[data-open-account]')) button.addEventListener('click', () => openAccount());
  for (const button of document.querySelectorAll('[data-close-dialog]')) button.addEventListener('click', () => button.closest('dialog').close());
  for (const button of document.querySelectorAll('[data-account-mode]')) button.addEventListener('click', () => setAccountMode(button.dataset.accountMode));
  $('account-form').addEventListener('submit', async (event) => {
    event.preventDefault(); $('account-submit').disabled = true; $('account-error').hidden = true;
    try {
      const values = Object.fromEntries(new FormData(event.target)); const data = await api(state.accountMode, values);
      if (!data?.token || !data?.user) throw new Error('The server did not return a valid account session.');
      state.token = data.token; state.user = data.user; $('password').value = ''; $('bootstrap-code').value = ''; state.messages = []; state.ids.clear(); updateAccountView(); restoreLocalHistory(); $('account-dialog').close(); connectSocket(); await Promise.all([loadHistory(), refreshSocial()]); notify(`Welcome, ${state.user.displayName || state.user.username}.`);
    } catch (error) { $('account-error').textContent = error.message; $('account-error').hidden = false; }
    finally { $('account-submit').disabled = false; }
  });
  $('sign-out').addEventListener('click', () => signOut().catch(fail));
  $('chat-form').addEventListener('submit', async (event) => {
    event.preventDefault(); const input = $('chat-text'), text = input.value; if (!text.trim()) return; $('send-message').disabled = true;
    try { await sendChat(text); if (input.value === text) input.value = ''; }
    catch (error) { fail(error); }
    finally { $('send-message').disabled = !state.user || readOnlyChannel(state.channel); input.focus(); }
  });
  $('clear-chat').addEventListener('click', clearView);
  $('whisper-target').addEventListener('change', () => { renderMessages(); loadHistory().catch(fail); });
  $('whisper-target').addEventListener('input', () => { if ($('whisper-target').value !== $('whisper-target').dataset.displayName) $('whisper-target').dataset.accountId = ''; });
  $('new-messages').addEventListener('click', () => { $('messages').scrollTop = $('messages').scrollHeight; $('new-messages').hidden = true; });
  $('messages').addEventListener('scroll', () => { if ($('messages').scrollHeight - $('messages').scrollTop - $('messages').clientHeight < 50) $('new-messages').hidden = true; });
  $('create-community').addEventListener('click', () => { if (requireAccount()) openAction('Create Community', [{ name: 'name', label: 'Community name', maxLength: 64 }, { name: 'description', label: 'Description', multiline: true, required: false, maxLength: 500 }], 'Create Community', (values) => api('communities/create', values)); });
  for (const [id, kind] of [['create-party', 'party'], ['create-raid', 'raid']]) $(id).addEventListener('click', async () => { if (!requireAccount()) return; try { await api('groups/create', { kind, type: kind }); await refreshSocial(); } catch (error) { fail(error); } });
  $('action-form').addEventListener('submit', async (event) => {
    event.preventDefault(); if (!state.action) return; $('action-submit').disabled = true; $('action-error').hidden = true;
    try { await state.action(Object.fromEntries(new FormData(event.target))); $('action-dialog').close(); await refreshSocial(); }
    catch (error) { $('action-error').textContent = error.message; $('action-error').hidden = false; }
    finally { $('action-submit').disabled = false; }
  });
  $('refresh-status').addEventListener('click', () => refreshStatus().catch(fail)); $('refresh-diagnostics').addEventListener('click', () => refreshDiagnostics().catch(fail));
  $('moderation-form').addEventListener('submit', async (event) => { event.preventDefault(); try { await api('chat/moderate', Object.fromEntries(new FormData(event.target))); notify('Moderation action applied.'); } catch (error) { fail(error); } });
  $('chat-preferences').addEventListener('click', () => {
    const form = $('preferences-form'); form.elements.timestamps.checked = state.preferences.timestamps; form.elements.localHistory.checked = state.preferences.localHistory; $('channel-colors').replaceChildren();
    for (const entry of channels) { const label = element('label', '', entry.name), color = element('input'); color.type = 'color'; color.name = `color-${entry.id}`; color.value = channelColor(entry.id); label.append(color); $('channel-colors').append(label); } $('preferences-dialog').showModal();
  });
  $('preferences-form').addEventListener('submit', (event) => {
    event.preventDefault(); const values = new FormData(event.target); state.preferences.timestamps = values.has('timestamps'); const wasRemembered = state.preferences.localHistory; state.preferences.localHistory = values.has('localHistory');
    for (const entry of channels) { const value = values.get(`color-${entry.id}`); if (/^#[a-fA-F0-9]{6}$/.test(value)) state.preferences.colors[entry.id] = value; }
    try { localStorage.setItem('socketjack.chat.preferences', JSON.stringify(state.preferences)); if (wasRemembered && !state.preferences.localHistory) localStorage.removeItem(historyStorageKey()); } catch { /* Optional local settings. */ }
    applyColors(); selectChannel(state.channel); saveLocalHistory(); $('preferences-dialog').close();
  });
  window.addEventListener('hashchange', () => setPage(location.hash.slice(1)));
  window.addEventListener('beforeunload', () => { saveLocalHistory(); if (state.ws) state.ws.close(); });
  document.addEventListener('keydown', (event) => { if (event.key === '/' && state.page === 'chat' && state.user && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement.tagName)) { event.preventDefault(); $('chat-text').focus(); $('chat-text').value = '/'; } });
  setInterval(async () => {
    if (state.pollBusy || document.hidden) return; state.pollBusy = true;
    try { if (state.page === 'status' || !state.user) await refreshStatus(); if (state.user && (!state.ws || state.ws.readyState !== WebSocket.OPEN) && state.page === 'chat') await loadHistory(); if (state.page === 'admin') await refreshDiagnostics(); }
    catch { /* A disconnected indicator is already visible; avoid repeated toast messages. */ }
    finally { state.pollBusy = false; }
  }, 5000);
  loadPreferences(); selectChannel('global'); updateAccountView(); setPage(location.hash.slice(1)); refreshStatus().catch(fail);
  api('chat/preset').then((preset) => { for (const style of preset || []) { const entry = channels.find((item) => item.id === style.id); if (entry && /^#[a-fA-F0-9]{6}$/.test(style.color)) { entry.color = style.color; entry.name = style.name; } } applyColors(); renderChannelLists(); }).catch(() => {});
})();
