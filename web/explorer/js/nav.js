// La navigation : précédent, suivant, parent, adresse, filtre, tri par en-tête.
  // ---------------- navigation ----------------
  $('back').onclick = () => { const t = T(); if (t.idx > 0) { t.mode = 'back'; go(t.hist[t.idx - 1], 'back'); } };
  $('fwd').onclick = () => { const t = T(); if (t.idx < t.hist.length - 1) go(t.hist[t.idx + 1], 'fwd'); };
  $('up').onclick = () => { const p = parentOf(T().path); if (p != null) go(p, 'push'); };
  $('addr').onkeydown = e => { if (e.key === 'Enter') { go($('addr').value.trim().replace(/\//g, sep), 'push'); $('files').focus(); } else if (e.key === 'Escape') { $('addr').value = T().path; $('files').focus(); } };
  $('addr').onfocus = () => $('addr').select();
  $('find').oninput = () => { T().filter = $('find').value; show(true); };
  $('lhead').onclick = e => { const b = e.target.closest('[data-k]'); if (!b) return; const t = T(); t.rev = t.sortK === b.dataset.k ? !t.rev : false; t.sortK = b.dataset.k; show(true); };

