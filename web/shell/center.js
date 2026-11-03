// Le script de center.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  let st = { muted: false }, view = new Date(), poll = null;

  const paintVol = v => { $('vol').style.backgroundSize = v + '% 100%'; $('volv').textContent = v + ' %'; };
  handlers.center = m => {
    st = m;
    if (m.volume >= 0) { if (document.activeElement !== $('vol')) $('vol').value = m.volume; paintVol(+$('vol').value); }
    $('vol').disabled = m.volume < 0;
    $('mute').classList.toggle('on', !!m.muted); $('muteLbl').textContent = m.muted ? 'Son coupé' : 'Son actif';
    $('dev').textContent = m.device || '';
    const n = m.net || {};
    $('orb').className = 'orb ' + (n.up ? 'green' : 'red');
    $('net').textContent = n.up ? `${n.type} · ${n.speed} Mb/s · ${n.ip}` : 'Aucune connexion';
  };
  $('vol').oninput = () => { paintVol(+$('vol').value); post({ cmd: 'vol.set', v: +$('vol').value }); };
  $('mute').onclick = () => post({ cmd: 'vol.mute', on: !st.muted });
  $('quit').onclick = () => post({ cmd: 'quit' });

  // ---- notifications : envoyées par les autres applications de la suite (cdx.shell, message « notify ») ----
  // l'icône de chaque programme de la suite (famille pixel de la charte, 01/11/2026) ; les autres : le pictogramme générique
  const APP_ICON = { shell: 'cdx', explorer: 'explorateur', browser: 'navigateur', repos: 'depots', transfers: 'transferts', notes: 'notes', terminal: 'terminal', agenda: 'agenda', mail: 'courrier', messages: 'messages' };
  handlers.notify = m => {
    const box = $('notifs'); box.textContent = '';
    const items = (m.items || []).slice().reverse();
    $('nclear').disabled = !items.length;
    if (!items.length) { const e = document.createElement('div'); e.className = 'nt'; e.innerHTML = '<span class="tx">Aucune notification</span>'; box.appendChild(e); return; }
    for (const n of items) {
      const row = document.createElement('div'); row.className = 'nt ' + n.level;
      const orb = document.createElement('span'); orb.className = 'orb ' + (n.level === 'error' ? 'red' : n.level === 'warn' ? 'amber' : 'green');
      const ic = document.createElement('span'); ic.className = 'pix'; ic.innerHTML = pix(APP_ICON[n.from] || 'apps', 16);   // l'icône du programme qui a notifié
      const body = document.createElement('div'); body.className = 'body';
      const ti = document.createElement('div'); ti.className = 'ti'; ti.textContent = n.title;
      const tx = document.createElement('div'); tx.className = 'tx'; tx.textContent = n.text || '';
      body.append(ti, tx);
      const meta = document.createElement('span'); meta.className = 'meta';
      meta.textContent = n.from + ' · ' + new Date(n.at * 1000).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
      row.append(orb, ic, body, meta); box.appendChild(row);
    }
  };
  $('nclear').onclick = () => post({ cmd: 'notify.clear' });

  // ---- calendrier (semaine du lundi) ----
  function drawCal() {
    const y = view.getFullYear(), m = view.getMonth(), today = new Date();
    $('month').textContent = view.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' });
    const first = (new Date(y, m, 1).getDay() + 6) % 7, days = new Date(y, m + 1, 0).getDate(), prevDays = new Date(y, m, 0).getDate();
    let html = ['lu', 'ma', 'me', 'je', 've', 'sa', 'di'].map(d => `<div class="h">${d}</div>`).join('');
    for (let i = 0; i < 42; i++) {
      const d = i - first + 1, out = d < 1 || d > days, n = d < 1 ? prevDays + d : d > days ? d - days : d;
      const isToday = !out && d === today.getDate() && m === today.getMonth() && y === today.getFullYear();
      html += `<div class="cd ${out ? 'f' : ''} ${isToday ? 'today' : ''}">${n}</div>`;
    }
    $('cal').innerHTML = html;
  }
  $('prev').onclick = () => { view = new Date(view.getFullYear(), view.getMonth() - 1, 1); drawCal(); };
  $('next').onclick = () => { view = new Date(view.getFullYear(), view.getMonth() + 1, 1); drawCal(); };
  $('month').onclick = () => { view = new Date(); drawCal(); };   // un clic sur le mois revient à aujourd'hui

  handlers.show = () => { view = new Date(); drawCal(); post({ cmd: 'center.get' }); clearInterval(poll); poll = setInterval(() => post({ cmd: 'center.get' }), 1500); };
  handlers.hide = () => { clearInterval(poll); poll = null; };
  document.onkeydown = e => { if (e.key === 'Escape') post({ cmd: 'hideCenter' }); };

  drawCal();
  handlers.notify({ items: [] });
  if (!hasHost) { handlers.notify({ items: [{ title: 'Téléchargement terminé', text: 'photo-vacances.zip', level: 'info', from: 'browser', at: Date.now() / 1000 - 60 }, { title: 'Sortie audio perdue', text: 'Casque (Arctis 7)', level: 'warn', from: 'audio_linker', at: Date.now() / 1000 - 600 }, { title: 'Mention dans #cdx', text: '<alice> messages: tu es là ?', level: 'info', from: 'messages', at: Date.now() / 1000 - 3000 }] }); }
  if (!hasHost) handlers.center({ volume: 64, muted: false, device: 'Haut-parleurs (Realtek)', net: { up: true, type: 'Ethernet', speed: 1000, ip: '192.168.1.158' } });
  post({ cmd: 'ready', page: 'center' });
