// Le script de barmenu.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  const $ = id => document.getElementById(id);

  function render(m) {
    $('title').textContent = m.title || '';
    const list = $('list'); list.textContent = '';
    for (const it of m.items || []) {
      const row = document.createElement('div'); row.className = 'row-item' + (it.mark ? ' sel' : ''); row.tabIndex = 0; row.dataset.id = it.id; row.title = it.label;
      if (it.faint) row.style.color = 'var(--faint)';   // fenêtre réduite
      const name = document.createElement('span'); name.className = 'grow'; name.textContent = it.label;
      if (m.items.some(x => x.mark)) { const mk = document.createElement('span'); mk.className = 'mark'; mk.textContent = it.mark || ''; row.appendChild(mk); }   // « ▶ » devant la fenêtre active
      row.appendChild(name); list.appendChild(row);
    }
  }
  const pick = row => { if (row && row.dataset.id) post({ cmd: 'barmenu.pick', id: row.dataset.id }); };

  handlers.items = render;
  handlers.show = () => { const r = document.querySelector('.row-item'); if (r) r.focus(); };
  $('list').onclick = e => pick(e.target.closest('.row-item'));
  document.addEventListener('contextmenu', e => e.preventDefault());
  document.addEventListener('keydown', e => {
    const rows = [...document.querySelectorAll('.row-item')], i = rows.indexOf(document.activeElement);
    if (e.key === 'Escape') post({ cmd: 'hideBarMenu' });
    else if (e.key === 'ArrowDown') { e.preventDefault(); (rows[i + 1] || rows[0])?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); (rows[i - 1] || rows[rows.length - 1])?.focus(); }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(document.activeElement); }
  });
  post({ cmd: 'ready', page: 'barmenu' });
  if (!hasHost) render({ title: 'Terminal', items: [{ id: 'open', label: 'Ouvrir' }, { id: 'remove', label: 'Retirer de la barre' }, { id: 'left', label: 'Déplacer à gauche' }] });   // aperçu hors du shell
