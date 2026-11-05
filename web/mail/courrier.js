// Le script de courrier.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  let state = { connected: false, email: '', hasClient: false, busy: false, error: '' };
  let labels = [], threads = { label: 'INBOX', q: '', items: [], fresh: false }, thread = null, cur = null, showImages = new Set();
  const two = n => String(n).padStart(2, '0');
  const fmtDate = ms => { const d = new Date(ms), t = new Date(); const same = d.toDateString() === t.toDateString(); return same ? `${two(d.getHours())}:${two(d.getMinutes())}` : d.getFullYear() === t.getFullYear() ? `${two(d.getDate())}/${two(d.getMonth() + 1)}` : `${two(d.getDate())}/${two(d.getMonth() + 1)}/${d.getFullYear()}`; };
  const fmtLong = ms => { const d = new Date(ms); return `${two(d.getDate())}/${two(d.getMonth() + 1)}/${d.getFullYear()} ${two(d.getHours())}:${two(d.getMinutes())}`; };
  const size = b => { if (!b) return ''; const u = ['o', 'Ko', 'Mo']; let i = 0, v = b; while (v >= 1024 && i < 2) { v /= 1024; i++; } return (i === 0 || v >= 10 ? Math.round(v) : v.toFixed(1).replace('.', ',')) + ' ' + u[i]; };
  const addrOf = h => { const m = /<([^>]+)>/.exec(h || ''); return (m ? m[1] : h || '').trim().toLowerCase(); };
  const nameOf = h => { const m = /^"?([^"<]+?)"?\s*</.exec(h || ''); return m ? m[1].trim() : (h || ''); };
  function setStatus(t, cls) { $('st1').textContent = t; $('orb').className = 'orb ' + (cls || (state.connected ? 'green' : '')); }

  // ---- rendu ----
  function renderLabels() {
    $('lbls').innerHTML = labels.map((l, i) => `<div class="lbl ${l.id === threads.label && !threads.q ? 'on' : ''} ${!l.system && (i === 0 || labels[i - 1].system) ? 'sep' : ''}" data-l="${esc(l.id)}"><span class="pix">${pix(l.id === 'INBOX' ? 'message' : l.id === 'STARRED' ? 'cloche' : l.id === 'SENT' ? 'connecter' : l.id === 'TRASH' ? 'supprimer' : l.id === 'SPAM' ? 'avertissement' : l.id === 'DRAFT' ? 'editer' : l.system ? 'liste' : 'dossier', 16)}</span><span class="n">${esc(l.name)}</span>${l.unread ? `<span class="u">${l.unread}</span>` : ''}</div>`).join('');
  }
  function renderThreads() {
    const t = threads.items;
    $('ths').innerHTML = t.length ? t.map(x => `<div class="th ${x.unread ? 'unread' : ''} ${x.id === cur ? 'on' : ''}" data-t="${esc(x.id)}"><span class="st ${x.starred ? 'on' : ''}" data-star="${esc(x.id)}" title="${x.starred ? 'Retirer des favoris' : 'Favori'}">${x.starred ? '★' : '☆'}</span><span class="f">${esc(x.from)}${x.count > 1 ? ` <span class="c">(${x.count})</span>` : ''}</span><span class="d">${fmtDate(x.date)}</span><span class="s">${esc(x.subject)}</span><span class="sn">${esc(x.snippet)}</span></div>`).join('') : `<div class="empty">${threads.fresh ? (threads.q ? 'Aucun résultat.' : 'Rien ici.') : 'Lecture…'}</div>`;
    const l = labels.find(k => k.id === threads.label); $('st2').textContent = (threads.q ? `Recherche « ${threads.q} »` : l ? l.name : '') + (threads.fresh ? '' : ' · dernier relevé (hors ligne)');
  }
  function frameDoc(m, images) {
    const csp = `default-src 'none'; img-src ${images ? '* data: cid:' : 'data: cid:'}; style-src 'unsafe-inline'; font-src data:`;
    const body = m.html != null ? m.html : `<pre style="white-space:pre-wrap;font:14px/18px Consolas,monospace;margin:0">${esc(m.text || '')}</pre>`;
    // Fond blanc et texte sombre : les courriers sont écrits pour un fond clair (les couleurs de la charte les rendaient illisibles).
    return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}"><base target="_blank"><style>html{background:#fff}body{margin:8px;background:#fff;color:#202124;font-family:Segoe UI,sans-serif;font-size:14px}a{color:#1a56c4}img{max-width:100%;height:auto}</style></head><body>${body}</body></html>`;
  }
  const hasRemoteImages = m => /<img[^>]+src\s*=\s*["']?https?:/i.test(m.html || '');
  function renderThread() {
    const v = $('view');
    if (!thread) { v.innerHTML = `<div class="empty">${state.connected ? 'Choisis un fil.' : ''}</div>`; return; }
    const last = thread.messages[thread.messages.length - 1];
    const inTrash = thread.messages.some(m => m.labels.includes('TRASH')), inInbox = thread.messages.some(m => m.labels.includes('INBOX')), starred = thread.messages.some(m => m.starred);
    v.innerHTML = `<div class="vh"><div class="subj">${esc(last.subject || '(sans objet)')}</div><div class="acts">
        <button class="btn" data-a="reply">Répondre</button><button class="btn" data-a="replyAll">Répondre à tous</button><button class="btn" data-a="forward">Transférer</button><span class="grow"></span>
        ${inInbox ? '<button class="btn" data-act="archive" title="Retirer de la boîte de réception (E)">Archiver</button>' : inTrash ? '<button class="btn" data-act="inbox">Restaurer</button>' : '<button class="btn" data-act="inbox">Boîte de réception</button>'}
        <button class="btn" data-act="unread" title="Marquer non lu">Non lu</button><button class="btn" data-act="${starred ? 'unstar' : 'star'}">${starred ? 'Retirer des favoris' : 'Favori'}</button>${inInbox ? '<button class="btn" data-act="spam">Spam</button>' : ''}<button class="btn" data-act="trash" title="Supprimer (Suppr)">Supprimer</button></div></div>
      <div class="msgs">${thread.messages.map(m => `<div class="msg" data-m="${esc(m.id)}"><div class="hd"><span class="fr">${esc(m.from)}</span><span class="dt">${fmtLong(m.date)}</span><span class="to">À : ${esc(m.to)}</span>${m.cc ? `<span class="cc">Cc : ${esc(m.cc)}</span>` : ''}</div>
        ${m.attachments.length ? `<div class="att">${m.attachments.map(a => `<button class="btn" data-att="${esc(a.id)}" data-msg="${esc(m.id)}" data-name="${esc(a.name)}" title="Enregistrer dans Téléchargements (${size(a.size)})"><span class="pix">${pix('fichier', 16)}</span> ${esc(a.name)} <span class="faint">${size(a.size)}</span></button>`).join('')}</div>` : ''}
        ${hasRemoteImages(m) && !showImages.has(m.id) ? `<div class="imgs">Les images distantes sont bloquées.<button class="btn" data-img="${esc(m.id)}">Afficher les images</button></div>` : ''}
        <iframe sandbox="allow-popups allow-popups-to-escape-sandbox" style="height:min(70vh,900px)" srcdoc="${esc(frameDoc(m, showImages.has(m.id)))}"></iframe></div>`).join('')}</div>`;
    // Le cadre a une origine opaque (pas allow-same-origin) : le corps d'un courrier ne partage jamais l'origine de cette page, qui parle au programme.
    // Sa hauteur ne peut donc plus être mesurée d'ici : hauteur fixe, le message défile dans son cadre.
  }
  function render() {
    if (!state.connected) {
      $('view').innerHTML = `<div class="hello"><div class="box"><div style="letter-spacing:3px">COURRIER</div><div class="faint">Ta boîte Gmail, à la charte. Relie le compte Google de la suite (ou étends-le au courrier) : la suite garde un jeton chiffré pour ton compte Windows.</div>
        ${state.hasClient ? `<button class="primary" id="connect" ${state.busy ? 'disabled' : ''}>${state.busy ? 'Attente du consentement dans le navigateur…' : 'Relier le compte Google'}</button>` : `<div class="err">Il manque l’identifiant client Google : dépose le fichier « client_secret_….json » sous <code>${esc(state.clientFile || '%APPDATA%\\CDX\\google-client.json')}</code>, puis relance.</div>`}
        ${state.error ? `<div class="err">${esc(state.error)}</div>` : ''}</div></div>`;
      $('lbls').innerHTML = ''; $('ths').innerHTML = '';
      if ($('connect')) $('connect').onclick = () => post({ cmd: 'ml.connect' });
      return;
    }
    renderLabels(); renderThreads(); renderThread();
  }

  // ---- messages du programme ----
  handlers.state = m => { state = m; setStatus(m.busy ? 'Lecture…' : (m.error || (m.connected ? 'Relié à ' + m.email : 'Compte Google non relié')), m.busy ? 'amber pulse' : m.error ? 'red' : m.connected ? 'green' : ''); $('st3').textContent = m.connected ? m.email : ''; render(); };
  handlers.labels = m => { labels = m.items; renderLabels(); };
  handlers.threads = m => { threads = m; if (cur && !m.items.some(t => t.id === cur)) { cur = null; thread = null; renderThread(); } renderThreads(); renderLabels(); };
  handlers.thread = m => { thread = m; cur = m.id; renderThread(); renderThreads(); };
  handlers.flag = m => { const t = threads.items.find(x => x.id === m.id); if (t) { t.unread = m.unread; renderThreads(); } };
  handlers.error = m => setStatus(m.text, 'red');
  handlers.done = m => setStatus({ archive: 'Archivé', trash: 'Supprimé', spam: 'Marqué comme spam', inbox: 'Remis dans la boîte de réception', unread: 'Marqué non lu', read: 'Marqué lu', star: 'Favori', unstar: 'Retiré des favoris' }[m.what] || 'Fait', 'green');
  handlers.saved = m => { setStatus('Enregistré : ' + m.path, 'green'); lastSaved = m.path; };
  handlers.sent = () => { closeVeil(); setStatus('Message envoyé.', 'green'); };
  handlers.picked = m => { for (const f of m.files) if (!attachments.some(a => a.path === f.path)) attachments.push(f); renderChips(); };
  let lastSaved = '';

  // ---- actions ----
  const list = (label, q) => { cur = null; thread = null; post({ cmd: 'ml.list', label, q: q || '' }); threads = { label, q: q || '', items: threads.label === label && threads.q === (q || '') ? threads.items : [], fresh: false }; render(); };
  $('lbls').onclick = e => { const l = e.target.closest('[data-l]'); if (l) { $('q').value = ''; list(l.dataset.l, ''); } };
  $('ths').onclick = e => {
    const s = e.target.closest('[data-star]'); if (s) { const t = threads.items.find(x => x.id === s.dataset.star); if (t) post({ cmd: 'ml.act', id: t.id, what: t.starred ? 'unstar' : 'star' }); return; }
    const r = e.target.closest('[data-t]'); if (r) { cur = r.dataset.t; renderThreads(); post({ cmd: 'ml.open', id: cur }); }
  };
  $('view').addEventListener('click', e => {
    const a = e.target.closest('[data-act]'); if (a && cur) { post({ cmd: 'ml.act', id: cur, what: a.dataset.act }); return; }
    const c = e.target.closest('[data-a]'); if (c && thread) { compose(c.dataset.a); return; }
    const t = e.target.closest('[data-att]'); if (t) { post({ cmd: 'ml.attach', msg: t.dataset.msg, id: t.dataset.att, name: t.dataset.name }); return; }
    const i = e.target.closest('[data-img]'); if (i) { showImages.add(i.dataset.img); renderThread(); }
  });
  $('q').onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') list(threads.label, $('q').value.trim()); else if (e.key === 'Escape') { $('q').value = ''; list(threads.label, ''); } };
  $('refresh').onclick = () => { post({ cmd: 'ml.labels' }); list(threads.label, threads.q); };
  $('new').onclick = () => compose('new');

  // ---- écrire : nouveau, répondre, répondre à tous, transférer ----
  let veil = null, attachments = [], forward = [];
  const closeVeil = () => { if (veil) { veil.remove(); veil = null; } };
  function renderChips() { const c = $('chips'); if (!c) return; c.innerHTML = [...forward.map((a, i) => `<span class="chip" title="Pièce jointe du message transféré">${esc(a.name)}<i data-fw="${i}">✕</i></span>`), ...attachments.map((a, i) => `<span class="chip">${esc(a.name)} <span class="faint">${size(a.size)}</span><i data-rm="${i}">✕</i></span>`)].join(''); }
  function compose(kind, prefill = {}) {
    const last = thread ? thread.messages[thread.messages.length - 1] : null; const me = (state.email || '').toLowerCase();
    let to = prefill.to || '', cc = '', subject = prefill.subject || '', text = prefill.text || '', inReplyTo = '', references = '', threadId = '';
    attachments = []; forward = [];
    if (kind !== 'new' && last) {
      const quote = `\n\nLe ${fmtLong(last.date)}, ${last.from} a écrit :\n` + (last.text || stripHtml(last.html || '')).split('\n').map(l => '> ' + l).join('\n');
      if (kind === 'reply' || kind === 'replyAll') {
        to = last.from; inReplyTo = last.messageId; references = last.references; threadId = last.threadId; subject = /^re\s*:/i.test(last.subject) ? last.subject : 'Re: ' + last.subject; text = quote;
        if (kind === 'replyAll') { const others = (last.to + ',' + (last.cc || '')).split(',').map(s => s.trim()).filter(s => s && addrOf(s) !== me && addrOf(s) !== addrOf(last.from)); cc = others.join(', '); }
      } else {
        subject = /^(fwd?|tr)\s*:/i.test(last.subject) ? last.subject : 'Fwd: ' + last.subject;
        text = `\n\n---------- Message transféré ----------\nDe : ${last.from}\nDate : ${fmtLong(last.date)}\nObjet : ${last.subject}\nÀ : ${last.to}\n\n` + (last.text || stripHtml(last.html || ''));
        forward = last.attachments.map(a => ({ msg: last.id, id: a.id, name: a.name, mime: a.mime }));
      }
    }
    closeVeil(); veil = document.createElement('div'); veil.className = 'veil';
    veil.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">${kind === 'new' ? 'Nouveau message' : kind === 'forward' ? 'Transférer' : 'Répondre'}</span></div><div class="cb">
      <div class="f"><label>À</label><input class="text-input" id="cto" value="${esc(to)}" spellcheck="false"><label>Cc</label><input class="text-input" id="ccc" value="${esc(cc)}" spellcheck="false"><label>Objet</label><input class="text-input" id="csub" value="${esc(subject)}" spellcheck="false"></div>
      <textarea class="text-input" id="ctext" spellcheck="true">${esc(text)}</textarea>
      <div class="chips" id="chips"></div>
      <div class="btns"><button class="btn" id="cattach">Joindre…</button><span class="grow"></span><button class="primary" id="csend">Envoyer</button><button class="btn" id="ccancel">Annuler</button></div></div></div>`;
    veil.dataset.threadId = threadId; veil.dataset.inReplyTo = inReplyTo; veil.dataset.references = references;
    document.body.appendChild(veil); renderChips();
    if (window.CdxSpell) CdxSpell.attach(veil.querySelector('#ctext'), { enabled: spell, auto: autoc, grammar: '/host/grammaire-worker.js', grammarEnabled: gram });   // le correcteur orthographique sur le message (calque retiré avec le voile)
    veil.querySelector(kind === 'new' ? '#cto' : '#ctext').focus(); if (kind !== 'new') { const ta = veil.querySelector('#ctext'); ta.setSelectionRange(0, 0); }
    veil.querySelector('#ccancel').onclick = closeVeil; veil.querySelector('#cattach').onclick = () => post({ cmd: 'ml.pick' }); veil.querySelector('#csend').onclick = sendCompose;
    veil.querySelector('#chips').onclick = e => { const r = e.target.closest('[data-rm]'); if (r) { attachments.splice(+r.dataset.rm, 1); renderChips(); } const f = e.target.closest('[data-fw]'); if (f) { forward.splice(+f.dataset.fw, 1); renderChips(); } };
    veil.onkeydown = e => { e.stopPropagation(); if (e.key === 'Escape') closeVeil(); else if (e.key === 'Enter' && e.ctrlKey) sendCompose(); };
  }
  function attachFiles(files) { for (const f of files) attachments.push(f); renderChips(); }   // (essais : à la place de la boîte de choix de fichiers)
  function sendCompose() {
    if (!veil) return;
    const to = veil.querySelector('#cto').value.trim(), cc = veil.querySelector('#ccc').value.trim(), subject = veil.querySelector('#csub').value.trim(), text = veil.querySelector('#ctext').value;
    if (!to && !cc) { setStatus('Il manque un destinataire.', 'red'); return; }
    setStatus('Envoi…', 'amber pulse');
    post({ cmd: 'ml.send', to, cc, bcc: '', subject, text, threadId: veil.dataset.threadId, inReplyTo: veil.dataset.inReplyTo, references: veil.dataset.references, files: attachments.map(a => a.path), forward });
  }
  const stripHtml = h => { const d = document.createElement('div'); d.innerHTML = h.replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, '').replace(/<br\s*\/?>|<\/p>|<\/div>|<\/tr>|<\/li>/gi, '\n'); return (d.textContent || '').replace(/\n{3,}/g, '\n\n').trim(); };

  // ---- réglages et raccourcis ----
  const spellKey = 'cdx.mail.spell', gramKey = 'cdx.mail.grammaire', autoKey = 'cdx.mail.autocorrection'; let spell = true, gram = true, autoc = true;
  try { spell = localStorage.getItem(spellKey) !== 'off'; gram = localStorage.getItem(gramKey) !== 'off'; autoc = localStorage.getItem(autoKey) !== 'off'; } catch (e) { }
  const setAuto = on => { autoc = on; try { localStorage.setItem(autoKey, on ? 'on' : 'off'); } catch (e) { } const ta = veil && veil.querySelector('#ctext'); if (ta && ta.__cdxSpell) ta.__cdxSpell.auto(on); };
  const setSpell = on => { spell = on; try { localStorage.setItem(spellKey, on ? 'on' : 'off'); } catch (e) { } const ta = veil && veil.querySelector('#ctext'); if (ta && ta.__cdxSpell) ta.__cdxSpell.enable(on); };
  const setGram = on => { gram = on; try { localStorage.setItem(gramKey, on ? 'on' : 'off'); } catch (e) { } const ta = veil && veil.querySelector('#ctext'); if (ta && ta.__cdxSpell) ta.__cdxSpell.grammar(on); };
  windowSettings(() => [
    ['Relever maintenant', () => $('refresh').click(), { key: 'F5', disabled: !state.connected }],
    ['Nouveau message', () => compose('new'), { key: 'Ctrl+N', disabled: !state.connected }],
    ['Correcteur orthographique', () => setSpell(!spell), { check: spell }],
    ['Correction automatique à la fin du mot', () => setAuto(!autoc), { check: autoc, disabled: !spell }],
    ['Grammaire (accords, conjugaison)', () => setGram(!gram), { check: gram }],
    null,
    state.connected ? ['Oublier le compte Google (' + state.email + ')', () => { if (confirm('Oublier le compte Google relié ? Agenda le perd aussi.')) post({ cmd: 'ml.disconnect' }); }] : ['Relier le compte Google', () => post({ cmd: 'ml.connect' }), { disabled: !state.hasClient }],
  ]);
  document.addEventListener('keydown', e => {
    if (veil) return;
    const inField = e.target.matches('input, textarea');
    if (e.key === 'F5') { e.preventDefault(); $('refresh').click(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') { e.preventDefault(); if (state.connected) compose('new'); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') { e.preventDefault(); $('q').focus(); $('q').select(); }
    else if (!inField && e.key === 'Delete' && cur) post({ cmd: 'ml.act', id: cur, what: 'trash' });
    else if (!inField && e.key.toLowerCase() === 'e' && cur) post({ cmd: 'ml.act', id: cur, what: 'archive' });
    else if (!inField && e.key.toLowerCase() === 'r' && thread) compose(e.shiftKey ? 'replyAll' : 'reply');
  });

  CdxFrame.init();
  CdxSplit.init({ pane: $('labels'), side: 'left', key: 'courrier-libelles', min: 140, max: 400 });
  CdxSplit.init({ pane: $('list'), side: 'left', key: 'courrier-liste', min: 260, max: 700, minOther: 320 });
  render();
  if (!hasHost) {
    handlers.state({ connected: true, email: 'moi@example.org', hasClient: true, busy: false, error: '' });
    handlers.labels({ fresh: true, items: [{ id: 'INBOX', name: 'Boîte de réception', system: true, unread: 2, total: 12 }, { id: 'STARRED', name: 'Favoris', system: true, unread: 0 }, { id: 'SENT', name: 'Envoyés', system: true, unread: 0 }, { id: 'Label_1', name: 'Factures', system: false, unread: 1 }] });
    handlers.threads({ label: 'INBOX', q: '', fresh: true, items: [{ id: 't1', subject: 'Facture octobre', from: 'Alice, Bob', snippet: 'Voici la facture du mois…', date: Date.now(), count: 2, unread: true, starred: false, labels: ['INBOX'] }, { id: 't2', subject: 'Week-end', from: 'Chloé', snippet: 'On part samedi ?', date: Date.now() - 3e8, count: 1, unread: false, starred: true, labels: ['INBOX'] }] });
    handlers.thread({ id: 't1', messages: [{ id: 'm1', threadId: 't1', from: 'Alice <alice@example.org>', to: 'moi@example.org', cc: '', subject: 'Facture octobre', date: Date.now() - 7e6, unread: false, starred: false, labels: ['INBOX'], text: 'Bonjour,\n\nVoici la facture.\n\nAlice', html: null, attachments: [{ id: 'a1', name: 'facture.pdf', mime: 'application/pdf', size: 48213 }], messageId: '<m1@example.org>', references: '' }, { id: 'm2', threadId: 't1', from: 'Bob <bob@example.org>', to: 'moi@example.org', cc: 'alice@example.org', subject: 'Re: Facture octobre', date: Date.now(), unread: true, starred: false, labels: ['INBOX', 'UNREAD'], html: '<p>Merci, <b>reçu</b>.</p><img src="http://example.org/pixel.png">', text: null, attachments: [], messageId: '<m2@example.org>', references: '<m1@example.org>' }] });
  } else post({ cmd: 'ml.ready' });
