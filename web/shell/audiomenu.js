// Le script de audiomenu.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  const $ = id => document.getElementById(id);
  let st = null, hold = 0, sent = 0;

  const fill = v => { const el = $('vol'); el.value = v; el.style.background = 'var(--bar) 0 0 / ' + v + '% 100% no-repeat, #222222'; $('val').textContent = v; };
  function render() {
    const list = $('list'); list.textContent = '';
    $('orb').className = 'orb' + (st && st.starting ? ' amber pulse' : st && st.running ? ' green' : '');
    $('etat').textContent = !st ? 'Audio Linker absent' : st.starting ? 'Démarrage…' : st.running ? 'En marche' + (st.playing ? ' : ' + st.playing : '') : 'Arrêté';
    $('run').textContent = st && st.running ? 'Arrêter' : 'Démarrer';
    $('run').disabled = !st || !!st.starting;
    $('mute').classList.toggle('muted', !!(st && st.muted));
    $('mute').title = st && st.muted ? 'Rétablir le son du groupe' : 'Couper le groupe';
    if (st && Date.now() > hold) fill(st.volume);   // pendant qu'on tire le curseur, l'état renvoyé ne le déplace pas
    const setups = st ? st.setups : [];
    if (!setups.length) { const e = document.createElement('div'); e.className = 'row-item'; e.textContent = 'Aucun setup enregistré'; list.appendChild(e); return; }
    for (const s of setups) {
      const row = document.createElement('div'); row.className = 'row-item' + (s.name === st.playing ? ' sel' : ''); row.tabIndex = 0; row.dataset.name = s.name;
      const mark = document.createElement('span'); mark.className = 'mark'; mark.textContent = s.name === st.playing ? '▶' : '';
      const name = document.createElement('span'); name.className = 'grow'; name.textContent = s.name;
      const sub = document.createElement('span'); sub.className = 'sub'; sub.textContent = s.count + (s.count > 1 ? ' sorties' : ' sortie');
      row.append(mark, name, sub); list.appendChild(row);
    }
  }
  function pick(row) { if (!row || !row.dataset.name) return; post({ cmd: 'audio.setup', name: row.dataset.name }); post({ cmd: 'hideAudioMenu' }); }

  handlers.audio = m => { st = m.on ? m : null; render(); };
  handlers.show = () => { const sel = document.querySelector('.row-item.sel') || document.querySelector('.row-item'); if (sel) sel.focus(); };
  $('list').onclick = e => pick(e.target.closest('.row-item'));
  $('run').onclick = () => post({ cmd: 'audio.toggle' });
  $('mute').onclick = () => post({ cmd: 'audio.mute', on: !(st && st.muted) });
  $('vol').oninput = e => {
    const v = +e.target.value; hold = Date.now() + 1500; fill(v);
    if (Date.now() - sent > 40) { sent = Date.now(); post({ cmd: 'audio.volume', v, commit: false }); }
  };
  $('vol').onchange = e => post({ cmd: 'audio.volume', v: +e.target.value, commit: true });
  document.querySelector('.vol').onwheel = e => {   // la molette règle le volume par pas de 5
    e.preventDefault(); const v = Math.max(0, Math.min(100, +$('vol').value + (e.deltaY < 0 ? 5 : -5)));
    hold = Date.now() + 1500; fill(v); post({ cmd: 'audio.volume', v, commit: true });
  };
  document.addEventListener('keydown', e => {
    const rows = [...document.querySelectorAll('.row-item[data-name]')], i = rows.indexOf(document.activeElement);
    if (e.key === 'Escape') post({ cmd: 'hideAudioMenu' });
    else if (e.key === 'ArrowDown') { e.preventDefault(); (rows[i + 1] || rows[0])?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); (rows[i - 1] || rows[rows.length - 1])?.focus(); }
    else if ((e.key === 'Enter' || e.key === ' ') && document.activeElement.classList.contains('row-item')) { e.preventDefault(); pick(document.activeElement); }
  });
  if (typeof fillPix === 'function') fillPix();
  render();
  post({ cmd: 'ready', page: 'audiomenu' });
  if (!hasHost) handlers.audio({ on: true, running: true, starting: false, muted: false, volume: 75, playing: 'Casque', setups: [{ name: 'Casque', count: 1 }, { name: 'Groupe Ecrans', count: 3 }, { name: 'Bluetooth', count: 1 }] });   // aperçu hors du shell
