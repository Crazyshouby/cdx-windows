// Les commandes de fichiers : copier, couper, coller, nouveau dossier, renommer, supprimer, boîte de confirmation.
  // ---------------- commandes ----------------
  function copy(cut) { if (inTrash()) return; const s = selected(); if (!s.length) return; clip = { paths: s.map(pathOf), cut }; post({ cmd: 'fs.copy', paths: clip.paths, cut }); show(true); }
  function paste() { if (T().path && !T().search && !inTrash()) post({ cmd: 'fs.paste', dir: T().path, tab: T().id }); }
  function newFolder() { if (T().path && !T().search && !inTrash()) { T().wantRename = true; post({ cmd: 'fs.newfolder', dir: T().path, tab: T().id }); } }
  function rename(name) {
    const t = T(); if (inTrash()) return; if (t.search) { $('st2').textContent = "Renommer : ouvre d'abord le dossier (clic droit > Ouvrir l'emplacement)"; return; }
    const idx = t.rows.findIndex(i => i.n === (name ?? [...t.sel][0])); if (idx < 0) return;
    const it = t.rows[idx]; ensureVisible(idx); const row = $('files').querySelector(`.fr[data-i="${idx}"]`); if (!row || it.drive) return;
    row.scrollIntoView({ block: 'nearest' });
    const cell = row.querySelector('.nm'); const shown = dispName(it), tail = it.n.slice(shown.length);   // l'extension masquée se retrouve au bout du nom
    cell.innerHTML = pix(iconOf(it), 16) + `<input class="text-input" value="${esc(shown)}" spellcheck="false">`;
    const inp = cell.querySelector('input'); inp.focus(); const dot = it.d ? shown.length : shown.lastIndexOf('.'); inp.setSelectionRange(0, dot > 0 ? dot : shown.length);
    // Fini (Entrée, Échap, perte du focus) : le champ rend le focus à la liste tout de suite, sinon la relecture qui suit le renommage serait tenue pour « pendant un renommage » et attendrait.
    let done = false; const end = ok => { if (done) return; done = true; inp.blur(); $('files').focus(); if (ok && inp.value && inp.value + tail !== it.n) post({ cmd: 'fs.rename', path: pathOf(it), name: inp.value + tail, tab: t.id }); else show(true); };
    inp.onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') end(true); else if (e.key === 'Escape') end(false); };
    inp.onblur = () => end(true);
  }
  // Suppression : vers la corbeille (annulable) ; depuis la corbeille, ou avec Maj, pour de bon (confirmation).
  function del() {
    const s = selected(); if (!s.length) return;
    if (inTrash()) { purgeSel(); return; }
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


  // Une boîte qui demande une ligne de texte : rend la saisie, ou null si on annule (Annuler, Échap).
  function askText({ title = '', text = '', value = '', ok = 'OK' } = {}) {
    return new Promise(resolve => {
      $('layer').innerHTML = `<div class="veil"><div class="win active"><div class="tb"><span class="tb-title">${esc(title)}</span></div><div class="cb"><div style="padding:8px 4px;line-height:1.5">${esc(text)}</div><input id="askIn" class="text-input" type="text" spellcheck="false" autocomplete="off" value="${esc(value)}">
        <div class="btns"><button class="primary" id="askOk">${esc(ok)}</button><button class="btn" id="askNo">Annuler</button></div></div></div></div>`;
      const close = v => { $('layer').innerHTML = ''; $('files').focus(); resolve(v); };
      $('askOk').onclick = () => close($('askIn').value.trim() || null); $('askNo').onclick = () => close(null);
      $('askIn').onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') close($('askIn').value.trim() || null); else if (e.key === 'Escape') close(null); };
      $('askIn').focus(); $('askIn').select();
    });
  }

  const itemWord = n => n === 1 ? 'cet élément' : `ces ${n} éléments`;
  // Une mise à la corbeille sur un disque qui n'en a pas (clé USB, partage réseau, lecteur subst) : le programme n'a rien supprimé et fait confirmer
  // la suppression définitive. La réponse repart en fs.delete avec permOk.
  handlers['fs.confirmperm'] = m => {
    const n = m.perm.length, name = n === 1 ? m.perm[0].split('\\').pop() : '';
    dialog({ title: 'Pas de corbeille', icon: 'avertissement', text: n === 1 ? `« ${name} » est sur un disque sans corbeille : il sera supprimé définitivement, impossible de l'annuler. Continuer ?` : `${n} éléments sont sur un disque sans corbeille : ils seront supprimés définitivement, impossible de l'annuler. Continuer ?`, yes: 'Supprimer', no: 'Annuler' },
      () => post({ cmd: 'fs.delete', paths: m.paths, permOk: true, tab: m.tab }));
  };
  // Supprimer pour de bon (Maj+Suppr) : pas de corbeille, pas d'annulation.
  function delPerm() {
    const s = selected(); if (!s.length || inTrash()) { if (inTrash()) purgeSel(); return; }
    dialog({ title: 'Supprimer définitivement', icon: 'avertissement', text: s.length === 1 ? `Supprimer définitivement « ${dispName(s[0])} » ? Il n'ira pas à la corbeille : impossible de l'annuler.` : `Supprimer définitivement ces ${s.length} éléments ? Ils n'iront pas à la corbeille : impossible de l'annuler.`, yes: 'Supprimer', no: 'Annuler' },
      () => post({ cmd: 'fs.deleteperm', paths: s.map(pathOf), tab: T().id }));
  }
  // La corbeille : restaurer, effacer pour de bon, vider.
  function restoreSel() { const s = selected().filter(i => i.id); if (s.length) post({ cmd: 'fs.restore', ids: s.map(i => i.id), tab: T().id }); }
  function purgeSel() {
    const s = selected().filter(i => i.id); if (!s.length) return;
    dialog({ title: 'Effacer définitivement', icon: 'avertissement', text: `Effacer définitivement ${itemWord(s.length)} de la corbeille ? C'est irréversible.`, yes: 'Effacer', no: 'Annuler' },
      () => post({ cmd: 'fs.purge', ids: s.map(i => i.id), tab: T().id }));
  }
  function emptyTrash() {
    const n = (T().items || []).length;
    dialog({ title: 'Vider la corbeille', icon: 'avertissement', text: n ? `Supprimer définitivement les ${n} éléments de la corbeille ? C'est irréversible.` : 'Vider la corbeille de tous les disques ? C\'est irréversible.', yes: 'Vider', no: 'Annuler' },
      () => post({ cmd: 'fs.emptybin', tab: T().id }));
  }
  const undo = () => post({ cmd: 'fs.undo', tab: T().id });
  const redo = () => post({ cmd: 'fs.redo', tab: T().id });
