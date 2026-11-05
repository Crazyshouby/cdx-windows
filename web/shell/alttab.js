// Le script de alttab.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  const $ = id => document.getElementById(id);
  let items = [], sel = 0;
  const nameOf = p => { const f = (p || '').split('\\').pop().replace(/\.exe$/i, ''); return f ? f.charAt(0).toUpperCase() + f.slice(1) : ''; };

  function draw(m) {
    items = m.items; sel = m.sel;
    $('grid').style.gridTemplateColumns = `repeat(${m.cols}, 250px)`;
    $('grid').innerHTML = items.map((t, i) => `<div class="card" data-i="${i}"><div class="win ${i === sel ? 'active' : ''}"><div class="tb"><span class="tb-title">${esc(t.title)}</span></div><div class="cb"><div class="cardbody"><img data-p="${esc(t.exe)}" alt=""><span class="${t.min ? 'min' : ''}">${esc(nameOf(t.exe))}${t.min ? ' (réduite)' : ''}</span></div></div></div></div>`).join('');
    $('grid').querySelectorAll('img').forEach(img => wantIcon(img.dataset.p, img, 32));
    mark();
  }
  function mark() {
    $('grid').querySelectorAll('.card').forEach((c, i) => c.querySelector('.win').classList.toggle('active', i === sel));
    $('cur').textContent = items[sel] ? items[sel].title : '';
  }
  handlers.items = draw;
  handlers.sel = m => { sel = m.index; mark(); };
  $('grid').onclick = e => { const c = e.target.closest('[data-i]'); if (c) post({ cmd: 'alttab.pick', h: items[+c.dataset.i].h }); };

  if (!hasHost) draw({ cols: 4, sel: 1, items: [['Documents — Explorateur', 'C:\\cdx-shell.exe', false], ['Paramètres', 'C:\\x\\systemsettings.exe', false], ['Terminal', 'C:\\x\\windowsterminal.exe', false], ['notes-cdx.txt — Bloc-notes', 'C:\\x\\notepad.exe', true]].map((a, i) => ({ h: String(i), title: a[0], exe: a[1], min: a[2] })) });
  post({ cmd: 'ready', page: 'alttab' });
