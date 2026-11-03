// Le script de agenda.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  const ls = (k, d) => { try { const v = localStorage.getItem('cdx.agenda.' + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } };
  const lsSet = (k, v) => { try { localStorage.setItem('cdx.agenda.' + k, JSON.stringify(v)); } catch (e) { } };
  let state = { connected: false, email: '', hasClient: false, busy: false, error: '' };
  let ev = { calendars: [], items: [], fresh: false, from: '', to: '' }, tasks = { lists: [], fresh: false };
  let view = ls('view', 'month'), cursor = new Date(); cursor.setHours(0, 0, 0, 0);
  let showDone = ls('showDone', false);
  const MONTHS = ['janvier', 'février', 'mars', 'avril', 'mai', 'juin', 'juillet', 'août', 'septembre', 'octobre', 'novembre', 'décembre'];
  const DAYS = ['lun.', 'mar.', 'mer.', 'jeu.', 'ven.', 'sam.', 'dim.'];
  const two = n => String(n).padStart(2, '0');
  const iso = d => `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`;
  const fromIso = s => { const [y, m, d] = s.slice(0, 10).split('-').map(Number); return new Date(y, m - 1, d); };
  const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
  const monday = d => addDays(d, -((d.getDay() + 6) % 7));
  const hm = s => { const d = new Date(s); return `${two(d.getHours())}:${two(d.getMinutes())}`; };
  const todayIso = iso(new Date());
  const longDay = d => `${DAYS[(d.getDay() + 6) % 7]} ${d.getDate()} ${MONTHS[d.getMonth()]}`;

  // ---- la plage affichée, et ce qu'on demande au programme ----
  function range() {
    if (view === 'month') { const first = new Date(cursor.getFullYear(), cursor.getMonth(), 1); const start = monday(first); return [start, addDays(start, 42)]; }
    if (view === 'week') { const start = monday(cursor); return [start, addDays(start, 7)]; }
    const t = new Date(); t.setHours(0, 0, 0, 0); return [t, addDays(t, 31)];
  }
  function load(force) { const [a, b] = range(); const from = iso(a), to = iso(b); if (!force && from === ev.from && to === ev.to && ev.fresh) { render(); return; } if (state.connected) { post({ cmd: 'ag.load', from, to }); setStatus('Lecture…', 'amber pulse'); } render(); }
  function setMonth(y, m) { cursor = new Date(y, m - 1, 1); load(); }   // (essais)
  function setStatus(t, cls) { $('st1').textContent = t; $('orb').className = 'orb ' + (cls || (state.connected ? 'green' : '')); }

  // ---- événements par jour ----
  const dayKeys = e => {
    const keys = []; let d = fromIso(e.start); let end;
    if (e.allDay) end = fromIso(e.end); else { const ed = new Date(e.end); end = new Date(ed.getFullYear(), ed.getMonth(), ed.getDate()); if (ed.getHours() || ed.getMinutes()) end = addDays(end, 1); if (end <= d) end = addDays(d, 1); }
    if (!e.allDay) d = (x => new Date(x.getFullYear(), x.getMonth(), x.getDate()))(new Date(e.start));
    for (let i = 0; i < 60 && d < end; i++, d = addDays(d, 1)) keys.push(iso(d));
    return keys.length ? keys : [e.start.slice(0, 10)];
  };
  function byDay() {
    const m = new Map();
    for (const e of ev.items) for (const k of dayKeys(e)) { if (!m.has(k)) m.set(k, []); m.get(k).push(e); }
    for (const l of m.values()) l.sort((a, b) => (a.allDay ? 0 : 1) - (b.allDay ? 0 : 1) || a.start.localeCompare(b.start));
    return m;
  }
  const chip = (e, day) => `<div class="ev ${e.allDay ? 'all' : ''}" data-ev="${esc(e.cal)}|${esc(e.id)}" style="border-left-color:${esc(e.color || 'var(--accent)')}" title="${esc(e.title)}${e.location ? ' · ' + esc(e.location) : ''}">${e.allDay ? '' : `<span class="t">${hm(e.start)}</span> `}${esc(e.title)}</div>`;

  // ---- rendu ----
  function render() {
    document.querySelectorAll('#views .mb').forEach(b => b.classList.toggle('on', b.dataset.v === view));
    const main = $('main');
    if (!state.connected) {
      main.innerHTML = `<div class="hello"><div class="box"><div style="letter-spacing:3px">AGENDA</div><div class="faint">Le calendrier et les tâches de ton compte Google, à la charte. Relie le compte une fois : la suite garde un jeton chiffré pour ton compte Windows, et le rafraîchit toute seule.</div>
        ${state.hasClient ? `<button class="primary" id="connect" ${state.busy ? 'disabled' : ''}>${state.busy ? 'Attente du consentement dans le navigateur…' : 'Relier le compte Google'}</button>` : `<div class="err">Il manque l’identifiant client Google : dépose le fichier « client_secret_….json » de la console Google Cloud sous <code>${esc(state.clientFile || '%APPDATA%\\CDX\\google-client.json')}</code>, puis relance.</div>`}
        ${state.error ? `<div class="err">${esc(state.error)}</div>` : ''}</div></div>`;
      $('title').textContent = ''; $('tasks').innerHTML = '';
      if ($('connect')) $('connect').onclick = () => post({ cmd: 'ag.connect' });
      return;
    }
    const days = byDay(); const [a, b] = range();
    if (view === 'month') {
      $('title').textContent = `${MONTHS[cursor.getMonth()]} ${cursor.getFullYear()}`;
      let cells = '';
      for (let d = new Date(a); d < b; d = addDays(d, 1)) {
        const k = iso(d), l = days.get(k) || []; const max = 4;
        cells += `<div class="day ${d.getMonth() === cursor.getMonth() ? 'cur' : ''} ${k === todayIso ? 'today' : ''}" data-day="${k}"><div class="n"><b>${d.getDate()}${d.getDate() === 1 ? ' ' + MONTHS[d.getMonth()].slice(0, 4) + '.' : ''}</b>${l.length > max ? `<span class="more">+${l.length - max}</span>` : ''}</div>${l.slice(0, max).map(e => chip(e, k)).join('')}</div>`;
      }
      main.innerHTML = `<div class="wd">${DAYS.map(x => `<span>${x}</span>`).join('')}</div><div class="cal">${cells}</div>`;
    } else if (view === 'week') {
      $('title').textContent = `${a.getDate()} ${MONTHS[a.getMonth()]} – ${addDays(b, -1).getDate()} ${MONTHS[addDays(b, -1).getMonth()]} ${addDays(b, -1).getFullYear()}`;
      let cells = '';
      for (let d = new Date(a); d < b; d = addDays(d, 1)) { const k = iso(d), l = days.get(k) || []; cells += `<div class="day cur ${k === todayIso ? 'today' : ''}" data-day="${k}"><div class="n"><b>${DAYS[(d.getDay() + 6) % 7]} ${d.getDate()}</b></div>${l.map(e => chip(e, k)).join('') || ''}</div>`; }
      main.innerHTML = `<div class="week">${cells}</div>`;
    } else {
      $('title').textContent = 'Les 31 prochains jours';
      let html = '', any = false;
      for (let d = new Date(a); d < b; d = addDays(d, 1)) { const k = iso(d), l = days.get(k) || []; if (!l.length) continue; any = true; html += `<div class="dh ${k === todayIso ? 'today' : ''}">${longDay(d)}</div>${l.map(e => chip(e, k)).join('')}`; }
      main.innerHTML = `<div class="agenda">${any ? html : '<div class="empty">Rien de prévu dans les 31 jours.</div>'}</div>`;
    }
    $('st2').textContent = ev.fresh ? '' : (ev.items.length ? 'dernier relevé (hors ligne)' : '');
    renderTasks();
  }
  function renderTasks() {
    const box = $('tasks'); if (!state.connected) { box.innerHTML = ''; return; }
    box.innerHTML = tasks.lists.map(l => {
      const open = l.tasks.filter(t => !t.done), done = l.tasks.filter(t => t.done);
      const row = t => { const late = t.due && !t.done && t.due.slice(0, 10) < todayIso; return `<div class="tk ${t.done ? 'done' : ''}" data-task="${esc(l.id)}|${esc(t.id)}"><input type="checkbox" ${t.done ? 'checked' : ''} data-chk="${esc(l.id)}|${esc(t.id)}"><span class="tt">${esc(t.title)}${t.notes ? `<div class="faint">${esc(t.notes)}</div>` : ''}</span>${t.due ? `<span class="due ${late ? 'late' : ''}" title="Échéance">${t.due.slice(8, 10)}/${t.due.slice(5, 7)}</span>` : ''}</div>`; };
      return `<div class="lh"><span>${esc(l.title)}</span><span class="n">${open.length}</span></div><input class="text-input" data-add="${esc(l.id)}" placeholder="Ajouter une tâche… (Entrée)" spellcheck="false" autocomplete="off">${open.map(row).join('')}${done.length ? `<div class="doneh" data-doneh>${showDone ? '▾' : '▸'} ${done.length} terminée${done.length > 1 ? 's' : ''}</div>${showDone ? done.map(row).join('') : ''}` : ''}`;
    }).join('') || `<div class="empty">${tasks.fresh ? 'Aucune liste de tâches.' : 'Tâches : lecture…'}</div>`;
  }

  // ---- messages du programme ----
  handlers.state = m => { const was = state.connected; state = m; setStatus(m.busy ? 'Lecture…' : (m.error || (m.connected ? 'Relié à ' + m.email : 'Compte Google non relié')), m.busy ? 'amber pulse' : m.error ? 'red' : m.connected ? 'green' : ''); $('st3').textContent = m.connected ? m.email : ''; if (m.connected && !was) { load(true); post({ cmd: 'ag.tasks' }); } render(); };
  handlers.events = m => { ev = m; render(); if (m.fresh) setStatus('Relié à ' + state.email, 'green'); };
  handlers.tasks = m => { tasks = m; renderTasks(); };
  handlers.error = m => setStatus(m.text, 'red');
  handlers.saved = () => closeVeil();

  // ---- fiche d'un événement : voir, modifier, créer ----
  let veil = null;
  const closeVeil = () => { if (veil) { veil.remove(); veil = null; } };
  function openVeil(html) { closeVeil(); veil = document.createElement('div'); veil.className = 'veil'; veil.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title" id="vt"></span></div><div class="cb">${html}</div></div>`; document.body.appendChild(veil); veil.onkeydown = e => { e.stopPropagation(); if (e.key === 'Escape') closeVeil(); }; return veil; }
  function showEvent(e) {
    const d = openVeil(`<div class="detail"><div style="letter-spacing:2px;color:#fff">${esc(e.title)}</div><div>${e.allDay ? (dayKeys(e).length > 1 ? `Du ${longDay(fromIso(e.start))} au ${longDay(addDays(fromIso(e.end), -1))}` : longDay(fromIso(e.start)) + ' · toute la journée') : `${longDay(new Date(e.start))} · ${hm(e.start)} – ${hm(e.end)}`}</div>
      ${e.location ? `<div>${pix('pc', 16)} ${esc(e.location)}</div>` : ''}<div class="faint">${esc(e.calName)}</div>${e.description ? `<div class="faint">${esc(e.description)}</div>` : ''}</div>
      <div class="btns">${e.writable ? `<button class="btn" id="edit">Modifier</button><button class="btn" id="del">Supprimer</button>` : ''}${e.link ? `<button class="btn" id="link">Dans Google Agenda</button>` : ''}<button class="primary" id="ok">Fermer</button></div>`);
    d.querySelector('#vt').textContent = 'Événement'; d.querySelector('#ok').focus(); d.querySelector('#ok').onclick = closeVeil;
    if (e.writable) { d.querySelector('#edit').onclick = () => editEvent(e); d.querySelector('#del').onclick = () => { if (confirm('Supprimer « ' + e.title + ' » ?')) { post({ cmd: 'ag.event.delete', cal: e.cal, id: e.id }); closeVeil(); } }; }
    if (e.link) d.querySelector('#link').onclick = () => post({ cmd: 'ag.open', url: e.link });
  }
  function editEvent(e, day) {
    const cals = ev.calendars.filter(c => c.writable); if (!cals.length) { setStatus('Aucun agenda modifiable.', 'red'); return; }
    const allDay = e ? e.allDay : false; const s = e ? (allDay ? e.start : e.start.slice(0, 16)) : (day || todayIso) + 'T09:00'; const en = e ? (allDay ? iso(addDays(fromIso(e.end), -1)) : e.end.slice(0, 16)) : (day || todayIso) + 'T10:00';
    const d = openVeil(`<div class="f">
      <label>Titre</label><input class="text-input" id="ft" value="${esc(e ? e.title : '')}" spellcheck="false">
      <label>Agenda</label><select class="btn lsel" id="fc">${cals.map(c => `<option value="${esc(c.id)}" ${e && e.cal === c.id || !e && c.primary ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}</select>
      <label>Toute la journée</label><div class="row"><input type="checkbox" id="fall" ${allDay ? 'checked' : ''}></div>
      <label>Début</label><div class="row"><input class="text-input" type="date" id="fsd" value="${s.slice(0, 10)}"><input class="text-input" type="time" id="fst" value="${allDay ? '09:00' : s.slice(11, 16)}" ${allDay ? 'disabled' : ''}></div>
      <label>Fin</label><div class="row"><input class="text-input" type="date" id="fdend" value="${en.slice(0, 10)}"><input class="text-input" type="time" id="fet" value="${allDay ? '10:00' : en.slice(11, 16)}" ${allDay ? 'disabled' : ''}></div>
      <label>Lieu</label><input class="text-input" id="fl" value="${esc(e ? e.location : '')}" spellcheck="false">
      <label>Description</label><textarea class="text-input" id="fd">${esc(e ? e.description : '')}</textarea></div>
      <div class="btns"><button class="primary" id="save">Enregistrer</button><button class="btn" id="cancel">Annuler</button></div>`);
    d.querySelector('#vt').textContent = e ? 'Modifier l’événement' : 'Nouvel événement'; d.querySelector('#ft').focus();
    if (window.CdxSpell) CdxSpell.attach(d.querySelector('#fd'), { enabled: spell, auto: autoc, grammar: '/host/grammaire-worker.js', grammarEnabled: gram });   // le correcteur sur la description (calque retiré avec le voile)
    d.querySelector('#fall').onchange = () => { const on = d.querySelector('#fall').checked; d.querySelector('#fst').disabled = on; d.querySelector('#fet').disabled = on; };
    d.querySelector('#cancel').onclick = closeVeil;
    d.querySelector('#save').onclick = () => {
      const all = d.querySelector('#fall').checked, sd = d.querySelector('#fsd').value, ed = d.querySelector('#fdend').value || sd;
      if (!d.querySelector('#ft').value.trim() || !sd) { setStatus('Il manque le titre ou la date.', 'red'); return; }
      const start = all ? sd : sd + 'T' + (d.querySelector('#fst').value || '09:00'), end = all ? iso(addDays(fromIso(ed), 1)) : ed + 'T' + (d.querySelector('#fet').value || '10:00');
      if (!all && end <= start) { setStatus('La fin doit être après le début.', 'red'); return; }
      post({ cmd: 'ag.event.save', id: e ? e.id : '', cal: d.querySelector('#fc').value, title: d.querySelector('#ft').value.trim(), allDay: all, start, end, location: d.querySelector('#fl').value, description: d.querySelector('#fd').value });
    };
  }
  $('main').addEventListener('click', e => { const c = e.target.closest('[data-ev]'); if (!c) return; const [cal, id] = c.dataset.ev.split('|'); const x = ev.items.find(k => k.cal === cal && k.id === id); if (x) showEvent(x); });
  $('main').addEventListener('dblclick', e => { if (e.target.closest('[data-ev]')) return; const d = e.target.closest('[data-day]'); if (d && state.connected) editEvent(null, d.dataset.day); });
  $('new').onclick = () => { if (state.connected) editEvent(null, view === 'list' ? todayIso : iso(cursor)); };
  $('refresh').onclick = () => { load(true); post({ cmd: 'ag.tasks' }); };
  $('prev').onclick = () => { cursor = view === 'month' ? new Date(cursor.getFullYear(), cursor.getMonth() - 1, 1) : addDays(cursor, -7); load(); };
  $('next').onclick = () => { cursor = view === 'month' ? new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1) : addDays(cursor, 7); load(); };
  $('today').onclick = () => { cursor = new Date(); cursor.setHours(0, 0, 0, 0); load(); };
  $('views').onclick = e => { const b = e.target.closest('[data-v]'); if (b) { view = b.dataset.v; lsSet('view', view); load(); } };

  // ---- tâches ----
  $('tasks').addEventListener('change', e => { const c = e.target.closest('[data-chk]'); if (c) { const [list, id] = c.dataset.chk.split('|'); post({ cmd: 'ag.task.toggle', list, id, done: c.checked }); } });
  $('tasks').addEventListener('keydown', e => { const i = e.target.closest('[data-add]'); if (i && e.key === 'Enter' && i.value.trim()) { post({ cmd: 'ag.task.add', list: i.dataset.add, title: i.value.trim() }); i.value = ''; } e.stopPropagation(); });
  $('tasks').addEventListener('click', e => { if (e.target.closest('[data-doneh]')) { showDone = !showDone; lsSet('showDone', showDone); renderTasks(); } });
  $('tasks').addEventListener('contextmenu', e => {
    const r = e.target.closest('[data-task]'); if (!r) return; e.preventDefault();
    const [list, id] = r.dataset.task.split('|'); const l = tasks.lists.find(x => x.id === list); const t = l && l.tasks.find(x => x.id === id); if (!t) return;
    CdxFrame.menu([
      ['Renommer…', () => { const v = prompt('Titre de la tâche', t.title); if (v !== null && v.trim()) post({ cmd: 'ag.task.edit', list, id, title: v.trim() }); }],
      ['Échéance…', () => { const v = prompt('Échéance (AAAA-MM-JJ, vide : aucune)', t.due ? t.due.slice(0, 10) : ''); if (v !== null && (v === '' || /^\d{4}-\d{2}-\d{2}$/.test(v))) post({ cmd: 'ag.task.edit', list, id, due: v }); }],
      [t.done ? 'À refaire' : 'Terminée', () => post({ cmd: 'ag.task.toggle', list, id, done: !t.done })],
      null,
      ['Supprimer', () => post({ cmd: 'ag.task.delete', list, id })],
    ], e.clientX, e.clientY, t.title);
  });

  // ---- réglages de la fenêtre ----
  // le correcteur (cdx-spell.js) sur la description d'un événement ; réglages gardés
  let spell = ls('correcteur', true), autoc = ls('autocorrection', true), gram = ls('grammaire', true);
  const spellTa = () => veil && veil.querySelector('#fd') && veil.querySelector('#fd').__cdxSpell;
  const setSpell = on => { spell = on; lsSet('correcteur', on); const t = spellTa(); if (t) t.enable(on); };
  const setAuto = on => { autoc = on; lsSet('autocorrection', on); const t = spellTa(); if (t) t.auto(on); };
  const setGram = on => { gram = on; lsSet('grammaire', on); const t = spellTa(); if (t) t.grammar(on); };
  windowSettings(() => [
    ['Correcteur orthographique', () => setSpell(!spell), { check: spell }],
    ['Correction automatique à la fin du mot', () => setAuto(!autoc), { check: autoc, disabled: !spell }],
    ['Grammaire (accords, conjugaison)', () => setGram(!gram), { check: gram }],
    null,
    ['Agendas affichés', null, { sub: () => ev.calendars.length ? ev.calendars.map(c => [c.name, () => post({ cmd: 'ag.cal.toggle', id: c.id, on: !c.on }), { check: c.on }]) : [['(aucun)', null, { disabled: true }]] }],
    ['Nouvelle liste de tâches…', () => { const v = prompt('Nom de la liste'); if (v && v.trim()) post({ cmd: 'ag.list.add', title: v.trim() }); }, { disabled: !state.connected }],
    null,
    state.connected ? ['Oublier le compte Google (' + state.email + ')', () => { if (confirm('Oublier le compte Google relié ? Les données affichées seront effacées.')) post({ cmd: 'ag.disconnect' }); }] : ['Relier le compte Google', () => post({ cmd: 'ag.connect' }), { disabled: !state.hasClient }],
  ]);
  document.addEventListener('keydown', e => {
    if (e.key === 'F5') { e.preventDefault(); $('refresh').click(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') { e.preventDefault(); $('new').click(); }
    else if (e.key === 'Escape' && veil) closeVeil();
  });

  CdxFrame.init();
  CdxSplit.init({ pane: $('side'), side: 'right', key: 'agenda-taches', min: 200, max: 600 });
  render();
  if (!hasHost) {
    handlers.state({ connected: true, email: 'moi@example.org', hasClient: true, busy: false, error: '' });
    handlers.events({ from: '', to: '', fresh: true, calendars: [{ id: 'a', name: 'Perso', color: 'var(--accent)', primary: true, on: true, writable: true }], items: [{ id: '1', cal: 'a', calName: 'Perso', color: 'var(--accent)', title: 'Dentiste', allDay: false, start: todayIso + 'T14:30:00+01:00', end: todayIso + 'T15:30:00+01:00', location: 'Clermont', writable: true }, { id: '2', cal: 'a', calName: 'Perso', color: 'var(--green)', title: 'Week-end', allDay: true, start: iso(addDays(new Date(), 3)), end: iso(addDays(new Date(), 5)), writable: true }] });
    handlers.tasks({ fresh: true, lists: [{ id: 'L1', title: 'Mes tâches', tasks: [{ id: 't1', title: 'Acheter du pain', done: false }, { id: 't2', title: 'Appeler la banque', done: true, due: '2026-11-03T00:00:00.000Z' }] }] });
  } else post({ cmd: 'ag.ready' });
