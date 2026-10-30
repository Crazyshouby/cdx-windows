// Les commandes de fichiers : copier, couper, coller, nouveau dossier, renommer, supprimer, boîte de confirmation.
  // ---------------- commandes ----------------
  function copy(cut) { const s = selected(); if (!s.length) return; clip = { paths: s.map(pathOf), cut }; post({ cmd: 'fs.copy', paths: clip.paths, cut }); show(true); }
  function paste() { if (T().path) post({ cmd: 'fs.paste', dir: T().path, tab: T().id }); }
  function newFolder() { if (T().path) { T().renameAfter = 'Nouveau dossier'; post({ cmd: 'fs.newfolder', dir: T().path, tab: T().id }); } }
  function rename(name) {
    const t = T(); const idx = t.rows.findIndex(i => i.n === (name ?? [...t.sel][0])); if (idx < 0) return;
    const it = t.rows[idx], row = $('files').children[idx]; if (!row || it.drive) return;
    row.scrollIntoView({ block: 'nearest' });
    const cell = row.querySelector('.nm'); cell.innerHTML = pix(iconOf(it), 16) + `<input class="text-input" value="${esc(it.n)}" spellcheck="false">`;
    const inp = cell.querySelector('input'); inp.focus(); const dot = it.d ? it.n.length : it.n.lastIndexOf('.'); inp.setSelectionRange(0, dot > 0 ? dot : it.n.length);
    let done = false; const end = ok => { if (done) return; done = true; if (ok && inp.value && inp.value !== it.n) post({ cmd: 'fs.rename', path: pathOf(it), name: inp.value, tab: t.id }); else show(true); };
    inp.onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') end(true); else if (e.key === 'Escape') end(false); };
    inp.onblur = () => end(true);
  }
  function del() {
    const s = selected(); if (!s.length) return;
    dialog({ title: 'Supprimer', icon: 'supprimer', text: s.length === 1 ? `Placer « ${s[0].n} » dans la corbeille ?` : `Placer ces ${s.length} éléments dans la corbeille ?`, yes: 'Oui', no: 'Non' },
      () => post({ cmd: 'fs.delete', paths: s.map(pathOf), tab: T().id }));
  }
  function dialog(o, onYes) {
    $('layer').innerHTML = `<div class="veil"><div class="win active"><div class="tb"><span class="tb-title">${esc(o.title)}</span></div><div class="cb"><div class="row" style="gap:14px;padding:10px 4px">${pix(o.icon, 32)}<div style="line-height:1.5">${esc(o.text)}</div></div><div class="btns"><button class="primary" id="dy">${o.yes}</button><button class="btn" id="dn">${o.no}</button></div></div></div></div>`;
    const close = () => { $('layer').innerHTML = ''; $('files').focus(); };
    $('dy').onclick = () => { close(); onYes(); }; $('dn').onclick = close; $('dy').focus();
    $('layer').onkeydown = e => { if (e.key === 'Escape') close(); };
  }
  $('cNew').onclick = newFolder; $('cCut').onclick = () => copy(true); $('cCopy').onclick = () => copy(false); $('cPaste').onclick = paste; $('cRen').onclick = () => rename(); $('cDel').onclick = del; $('cRef').onclick = reload;

