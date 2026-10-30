// Le clavier.
  // ---------------- clavier ----------------
  document.addEventListener('keydown', e => {
    if (e.target.matches('input')) return;
    const t = T(); if (!t || !t.rows) return; const k = e.key;
    const move = d => { const i = t.rows.findIndex(r => t.sel.has(r.n)); const n = Math.max(0, Math.min(t.rows.length - 1, (i < 0 ? (d > 0 ? -1 : t.rows.length) : i) + d)); if (t.rows[n]) { t.sel = new Set([t.rows[n].n]); t.anchor = n; show(true); $('files').children[n]?.scrollIntoView({ block: 'nearest' }); } };
    if (e.altKey && k === 'ArrowLeft') { e.preventDefault(); $('back').click(); }
    else if (e.altKey && k === 'ArrowRight') { e.preventDefault(); $('fwd').click(); }
    else if ((e.altKey && k === 'ArrowUp') || k === 'Backspace') { e.preventDefault(); $('up').click(); }
    else if (k === 'ArrowDown') { e.preventDefault(); move(1); } else if (k === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (k === 'Enter') { const s = selected(); if (s.length === 1) open(s[0]); }
    else if (k === 'F2') rename(); else if (k === 'F5') reload(); else if (k === 'Delete') del();
    else if (k === 'F4' || (e.ctrlKey && k.toLowerCase() === 'l')) { e.preventDefault(); $('addr').focus(); }
    else if (e.ctrlKey && k.toLowerCase() === 'f') { e.preventDefault(); $('find').focus(); }
    else if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); t.sel = new Set(t.rows.map(r => r.n)); show(true); }
    else if (e.ctrlKey && k.toLowerCase() === 'c') copy(false); else if (e.ctrlKey && k.toLowerCase() === 'x') copy(true); else if (e.ctrlKey && k.toLowerCase() === 'v') paste();
    else if (e.ctrlKey && e.shiftKey && k.toLowerCase() === 'n') newFolder();
    else if (e.ctrlKey && k.toLowerCase() === 't') addTab(t.path);
    else if (e.ctrlKey && k.toLowerCase() === 'w' && tabs.length > 1) { tabs.splice(cur, 1); cur = Math.min(cur, tabs.length - 1); drawTabs(); show(); }
    else if (k === 'Escape') { t.sel.clear(); show(true); }
  });

