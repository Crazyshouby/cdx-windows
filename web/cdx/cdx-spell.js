/* cdx-spell.js : le correcteur des zones de texte (textarea), pour toute la suite CDX : orthographe, correction automatique, et grammaire si la page
   fournit un worker.

   Orthographe : la page ne corrige rien elle-même ; elle découpe le texte en mots, demande à l'hôte lesquels sont inconnus et les souligne ; au clic droit
   sur un mot souligné, elle demande des suggestions et les propose dans le menu de la charte (CdxFrame.menu), avec « Ajouter au dictionnaire ». Le
   dictionnaire, la langue et les mots ajoutés vivent dans l'hôte (cdx-windows : Hunspell et le dictionnaire français de Dicollecte, Cdx.Host/Spell.cs).
   Correction automatique (option `auto`, décisions du 01/11/2026) : à la fin d'un mot (espace, ponctuation, retour à la ligne), un mot inconnu est remplacé
   tout seul si la suggestion est sûre : une seule candidate à une lettre d'écart (ou, pour un mot d'au moins six lettres, une seule à deux lettres) ;
   sinon il reste souligné. Le mot corrigé reste souligné en pointillé « ok » huit secondes ; clic droit dessus : « Rétablir », « Ajouter au dictionnaire » ;
   Ctrl+Z rend le mot tapé, et il n'est plus corrigé dans la fenêtre. Jamais corrigés tout seuls : mots avec apostrophe ou trait d'union, majuscule interne.
   Grammaire (accords, conjugaison, confusions) : un worker de la page (option `grammar`, cdx-windows : Grammalecte dans web/cdx/grammaire-worker.js)
   reçoit le texte et rend des erreurs avec message et suggestions ; elles sont soulignées autrement (pointillé « attention ») et proposées au clic droit.

   Usage :  const sp = CdxSpell.attach(textarea, { markdown: true, enabled: true, auto: true, grammar: "/cdx/grammaire-worker.js", grammarEnabled: true });
            (grammar : l'adresse du worker, ou un objet qui se comporte comme lui, pour les essais)
            sp.enable(false);  sp.auto(false);  sp.grammar(false);  sp.refresh();  sp.destroy();
   - markdown : ne pas vérifier le code (``` ... ```, `code`), les liens [[Nom]] et [texte](cible), les #étiquettes.
   - Jamais vérifiés : adresses web et courriel, mots avec chiffres, sigles en capitales, lettres seules, et le mot en cours de frappe (celui du curseur) ;
     une élision (l', d', qu'…) est retirée avant la vérification. Une erreur de grammaire qui contient le curseur attend aussi la fin de la frappe.

   Contrat avec l'hôte (messages JSON de WebView2, window.chrome.webview) :
     page → hôte : { cmd: "sp.check", id, words: [...] }   { cmd: "sp.suggest", id, word }   { cmd: "sp.add", word }
     hôte → page : { type: "sp.result", id, bad: [...] }   { type: "sp.suggestions", id, word, list: [...], known }   { type: "sp.added", word }   { type: "sp.off" }
   Sans hôte, ou si l'hôte répond sp.off (pas de dictionnaire), rien n'est souligné pour l'orthographe.
   Contrat avec le worker de grammaire (un seul par page et par adresse, partagé par les zones) :
     page → worker : { type: "init" }   { type: "parse", id, text }
     worker → page : { type: "ready" } | { type: "error", message }   { type: "result", id, errors: [{ s, e, type, msg, sugg: [...], rule }] }

   Le soulignement : une zone de texte ne se colore pas par morceaux. Un calque (div) est posé sous elle, même police, même largeur utile, mêmes retours
   à la ligne, texte transparent, les mots inconnus dans des <mark> soulignés en rouge ondulé, les erreurs de grammaire dans des <mark class="g"> en
   pointillé, les corrections automatiques récentes dans des <mark class="a"> ; la zone de texte devient transparente (son fond passe au calque) et
   défile avec lui. Le calque est recalé à chaque changement de taille ; il ne change pas la mise en page de la page. */
(() => {
  const webview = window.chrome && window.chrome.webview;
  const known = new Map();          // mot → true (connu) | false (inconnu) ; partagé par toutes les zones de la page
  const waiting = new Map();        // id de demande → fonction de rappel
  const instances = new Set();
  const workers = new Map();        // adresse → { w, ready, failed, waiting: Map(id → rappel) }
  const AUTO_SHOWN = 8000;          // ms : le rappel d'une correction automatique
  let seq = 0, off = false, listening = false;

  const post = o => { if (webview) webview.postMessage(o); };
  const ask = (cmd, extra, cb) => { if (!webview || off) { cb(null); return; } const id = "sp" + (++seq); waiting.set(id, cb); post(Object.assign({ cmd, id }, extra)); };
  function listen() {
    if (listening || !webview) return; listening = true;
    webview.addEventListener("message", e => {
      const m = e.data; if (!m || typeof m.type !== "string" || !m.type.startsWith("sp.")) return;
      if (m.type === "sp.off") { off = true; instances.forEach(i => i.render()); return; }
      if (m.type === "sp.added") { known.set(m.word, true); known.set(m.word.toLowerCase(), true); instances.forEach(i => i.render()); return; }
      const cb = waiting.get(m.id); if (cb) { waiting.delete(m.id); cb(m); }
    });
  }
  // Le worker de grammaire d'une adresse (créé au premier besoin) ; null s'il ne peut pas être créé. `url` peut aussi être un objet qui se comporte
  // comme un worker (postMessage, onmessage) : les pages de test en donnent un faux.
  function grammarWorker(url) {
    if (workers.has(url)) return workers.get(url);
    let w = null;
    try { w = typeof url === "string" ? new Worker(url) : url; } catch (e) { console.warn("cdx-spell : pas de worker de grammaire (" + e.message + ")"); workers.set(url, null); return null; }
    const g = { w, ready: false, failed: null, waiting: new Map() };
    w.onmessage = e => {
      const m = e.data || {};
      if (m.type === "ready") g.ready = true;
      else if (m.type === "error") { g.failed = m.message || "erreur"; console.warn("cdx-spell : grammaire indisponible : " + g.failed); g.waiting.forEach(cb => cb(null)); g.waiting.clear(); }
      else if (m.type === "result") { const cb = g.waiting.get(m.id); if (cb) { g.waiting.delete(m.id); cb(m.errors || []); } }
    };
    w.onerror = e => { g.failed = e.message || "erreur"; console.warn("cdx-spell : worker de grammaire en erreur : " + g.failed); g.waiting.forEach(cb => cb(null)); g.waiting.clear(); };
    w.postMessage({ type: "init" });
    workers.set(url, g);
    return g;
  }

  const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const ELISIONS = /^(l|d|qu|n|s|j|m|t|c|jusqu|lorsqu|puisqu|quoiqu)['’]/i;
  // Les parties du texte à ne pas vérifier, remplacées par des espaces (les positions ne bougent pas).
  const MASKS_ALWAYS = [/https?:\/\/\S+/g, /\bwww\.\S+/g, /[\w.+-]+@[\w-]+\.[\w.-]+/g];
  const MASKS_MD = [/```[\s\S]*?(```|$)/g, /`[^`\n]*`/g, /\[\[[^\]]*\]\]/g, /\]\([^)]*\)/g, /(^|\s)#[^\s#]+/g, /^\s*(---|\+\+\+)[\s\S]*?^\s*(---|\+\+\+)\s*$/m];
  function masked(text, markdown) {
    let t = text;
    for (const re of markdown ? MASKS_ALWAYS.concat(MASKS_MD) : MASKS_ALWAYS) t = t.replace(re, m => " ".repeat(m.length));
    return t;
  }
  // Les mots à vérifier : { s, e, w } (début, fin, mot). Une lettre, un sigle ou un mot avec chiffre ne compte pas.
  function tokens(text, markdown) {
    const out = [], t = masked(text, markdown), re = /[\p{L}\p{M}\p{N}'’_-]+/gu;
    let m;
    while ((m = re.exec(t))) {
      let s = m.index, e = s + m[0].length;
      while (s < e && /['’_-]/.test(t[s])) s++;
      while (e > s && /['’_-]/.test(t[e - 1])) e--;
      const el = ELISIONS.exec(t.slice(s, e)); if (el) s += el[0].length;   // « l'école » : on vérifie « école »
      if (e - s < 2) continue;
      const w = t.slice(s, e);
      if (/[\p{N}_]/u.test(w)) continue;
      if (w === w.toUpperCase() && w !== w.toLowerCase()) continue;   // sigle
      out.push({ s, e, w });
    }
    return out;
  }
  // Distance d'édition (Damerau-Levenshtein, alignement optimal), sans tenir compte de la casse ; bornée à `max`.
  function distance(a, b, max) {
    a = a.toLowerCase(); b = b.toLowerCase();
    if (Math.abs(a.length - b.length) > max) return max + 1;
    const d = [];
    for (let i = 0; i <= a.length; i++) { d[i] = [i]; }
    for (let j = 0; j <= b.length; j++) d[0][j] = j;
    for (let i = 1; i <= a.length; i++) {
      for (let j = 1; j <= b.length; j++) {
        const cost = a[i - 1] === b[j - 1] ? 0 : 1;
        d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost);
        if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) d[i][j] = Math.min(d[i][j], d[i - 2][j - 2] + 1);
      }
    }
    return d[a.length][b.length];
  }
  // La suggestion sûre pour `w` parmi `list`, ou null : une seule à une lettre d'écart ; sinon, dès six lettres, une seule à deux lettres.
  function pick(w, list) {
    const cands = list.filter(c => c && !/\s/.test(c) && c !== w);
    const d1 = cands.filter(c => distance(w, c, 1) <= 1);
    if (d1.length === 1) return d1[0];
    if (d1.length === 0 && w.length >= 6) { const d2 = cands.filter(c => distance(w, c, 2) <= 2); if (d2.length === 1) return d2[0]; }
    return null;
  }

  const COPY = ["fontFamily", "fontSize", "fontWeight", "fontStyle", "fontVariant", "fontKerning", "lineHeight", "letterSpacing", "wordSpacing", "textTransform", "textIndent", "textRendering",
    "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth", "boxSizing", "whiteSpace", "overflowWrap", "wordBreak", "tabSize", "direction"];

  function attach(ta, opts = {}) {
    if (!ta || ta.tagName !== "TEXTAREA") return null;
    if (ta.__cdxSpell) return ta.__cdxSpell;
    listen();
    const markdown = !!opts.markdown; let enabled = opts.enabled !== false, pendingBatch = null, timer = 0;
    let auto = opts.auto !== false; const autos = []; let autoTimer = 0;   // les corrections automatiques récentes : { s, e, original, to, at }
    const gurl = opts.grammar || null; let genabled = opts.grammarEnabled !== false, gtimer = 0, gseq = 0, gtext = null, gerrs = [];
    const gignored = new Set();   // « Ignorer ici » : règle + texte souligné
    const bd = document.createElement("div"); bd.className = "cdx-spell"; bd.setAttribute("aria-hidden", "true");
    const bg0 = ta.style.background, pos0 = ta.style.position, z0 = ta.style.zIndex;
    // (pas de pointer-events: none : elementsFromPoint doit voir les <mark> sous la zone de texte ; le calque est dessous, il ne reçoit rien)
    Object.assign(bd.style, { position: "absolute", overflow: "hidden", zIndex: "0", margin: "0", color: "transparent", userSelect: "none", borderStyle: "solid", borderColor: "transparent" });
    if (!document.getElementById("cdx-spell-style")) {
      const st = document.createElement("style"); st.id = "cdx-spell-style";
      st.textContent = ".cdx-spell mark { background: none; color: transparent; text-decoration: underline wavy var(--red, #ff5a4a); text-decoration-skip-ink: none; text-underline-offset: 2px; }" +
        ".cdx-spell mark.g { text-decoration: underline dotted var(--warn, #faa320); text-decoration-thickness: 2px; }" +
        ".cdx-spell mark.a { text-decoration: underline dotted var(--green, #00ff00); text-decoration-thickness: 2px; }";
      document.head.appendChild(st);
    }
    let bg = "";

    function geometry() {
      if (!ta.isConnected) { destroy(); return false; }
      const rects = ta.getClientRects();
      if (!rects.length) { bd.style.display = "none"; return false; }
      bd.style.display = "";
      const cs = getComputedStyle(ta);
      for (const p of COPY) bd.style[p] = cs[p];
      if (!bg) { bg = cs.backgroundColor; ta.style.background = "transparent"; ta.style.position = cs.position === "static" ? "relative" : cs.position; if (cs.zIndex === "auto") ta.style.zIndex = "1"; }
      bd.style.background = bg;
      const r = ta.getBoundingClientRect(), op = bd.offsetParent, inBody = !op || op === document.body || op === document.documentElement;
      const o = inBody ? { left: -window.scrollX, top: -window.scrollY } : op.getBoundingClientRect();
      const bw = parseFloat(cs.borderLeftWidth) + parseFloat(cs.borderRightWidth), bh = parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
      bd.style.left = (r.left - o.left + (inBody ? 0 : op.scrollLeft - op.clientLeft)) + "px";
      bd.style.top = (r.top - o.top + (inBody ? 0 : op.scrollTop - op.clientTop)) + "px";
      bd.style.width = (ta.clientWidth + bw) + "px";    // la largeur utile (sans la barre de défilement) : mêmes retours à la ligne
      bd.style.height = (ta.clientHeight + bh) + "px";
      return true;
    }
    function sync() { bd.scrollTop = ta.scrollTop; bd.scrollLeft = ta.scrollLeft; }
    // Les corrections automatiques encore à montrer (texte toujours en place, moins de huit secondes).
    function liveAutos(text) {
      const now = Date.now();
      for (let i = autos.length - 1; i >= 0; i--) { const a = autos[i]; if (now - a.at > AUTO_SHOWN || text.slice(a.s, a.e) !== a.to) autos.splice(i, 1); }
      if (autos.length) { clearTimeout(autoTimer); autoTimer = setTimeout(render, Math.max(50, AUTO_SHOWN - (now - autos[0].at) + 20)); }
      return autos;
    }

    function render() {
      if (!geometry()) return;
      const text = ta.value;
      const spelling = enabled && !off, grammar = genabled && gurl && gtext === text, recent = liveAutos(text);
      if (!text || (!spelling && !grammar && !recent.length)) { bd.textContent = text ? text + (text.endsWith("\n") ? " " : "") : ""; sync(); return; }
      const caret = document.activeElement === ta ? ta.selectionStart : -1;
      const ranges = [], unknown = [];
      for (let i = 0; i < recent.length; i++) ranges.push({ s: recent[i].s, e: recent[i].e, kind: "a", i });
      if (spelling) {
        for (const t of tokens(text, markdown)) {
          const k = known.get(t.w);
          if (k === undefined) { if (!known.has(t.w.toLowerCase()) || known.get(t.w.toLowerCase()) === false) unknown.push(t.w); else continue; }
          if (k !== false) continue;
          ranges.push({ s: t.s, e: t.e, kind: "sp" });
        }
      }
      if (grammar) for (let i = 0; i < gerrs.length; i++) { const g = gerrs[i]; if (g.e > g.s && !gignored.has(g.rule + "|" + text.slice(g.s, g.e))) ranges.push({ s: g.s, e: g.e, kind: "g", i }); }
      ranges.sort((a, b) => a.s - b.s || (a.kind === "a" ? -1 : b.kind === "a" ? 1 : a.kind === "sp" ? -1 : 1));
      let html = "", last = 0;
      for (const r of ranges) {
        if (r.s < last) continue;                                        // chevauchement : le premier l'emporte (correction récente, orthographe, grammaire)
        if (r.kind !== "a" && caret >= r.s && caret <= r.e) continue;    // en cours de frappe
        const open = r.kind === "sp" ? `<mark data-s="${r.s}" data-e="${r.e}">` : `<mark class="${r.kind}" data-s="${r.s}" data-e="${r.e}" data-i="${r.i}">`;
        html += esc(text.slice(last, r.s)) + open + esc(text.slice(r.s, r.e)) + "</mark>";
        last = r.e;
      }
      html += esc(text.slice(last)); if (text.endsWith("\n")) html += " ";
      bd.innerHTML = html; sync();
      if (unknown.length) check([...new Set(unknown)]);
    }
    function check(words) {
      if (pendingBatch) { for (const w of words) pendingBatch.add(w); return; }
      pendingBatch = new Set(words);
      const batch = [...pendingBatch];
      ask("sp.check", { words: batch }, m => {
        const rest = [...pendingBatch].filter(w => !batch.includes(w)); pendingBatch = null;
        if (!m) return;
        const bad = new Set(m.bad || []);
        for (const w of batch) known.set(w, !bad.has(w));
        instances.forEach(i => i.render());
        if (rest.length) check(rest);
      });
    }
    const schedule = () => { clearTimeout(timer); timer = setTimeout(render, 150); };
    // La grammaire : le texte entier (parties masquées comprises, en espaces) au worker, 600 ms après la dernière frappe ; une réponse d'un texte dépassé est ignorée.
    function parseGrammar() {
      if (!gurl || !genabled) return;
      const g = grammarWorker(gurl); if (!g || g.failed) return;
      const text = ta.value; if (text === gtext) return;
      if (!text.trim()) { gtext = text; gerrs = []; return; }
      const id = "g" + (++gseq);
      g.waiting.set(id, errors => { if (!errors || ta.value !== text) return; gtext = text; gerrs = errors; render(); });
      g.w.postMessage({ type: "parse", id, text: masked(text, markdown) });
    }
    const scheduleGrammar = () => { clearTimeout(gtimer); gtimer = setTimeout(parseGrammar, 600); };

    // La correction automatique : à la fin d'un mot (ce qui vient d'être tapé est un espace, une ponctuation ou un retour à la ligne), le mot qui précède
    // est vérifié ; s'il est inconnu et qu'une suggestion est sûre, elle le remplace (insertText : Ctrl+Z la défait).
    const BOUNDARY = /^[\s.,;:!?)\]»…]$/;
    function autoCorrect(e) {
      if (!auto || !enabled || off || !webview || document.activeElement !== ta) return;
      if (e.inputType === "historyUndo") {   // Ctrl+Z après une correction : le mot tapé est rendu, on ne le corrige plus
        const last = autos[autos.length - 1];
        if (last && ta.value.slice(last.s, last.s + last.original.length) === last.original) { known.set(last.original, true); autos.pop(); }
        return;
      }
      const data = e.data || "";
      const boundary = e.inputType === "insertLineBreak" || e.inputType === "insertParagraph" || (e.inputType === "insertText" && BOUNDARY.test(data));
      if (!boundary || ta.selectionStart !== ta.selectionEnd) return;
      const pos = ta.selectionStart - (e.inputType === "insertText" ? data.length : 1);
      const text = ta.value, tok = tokens(text, markdown).find(t => t.e === pos);
      if (!tok) return;
      const w = tok.w;
      if (/['’-]/.test(w) || /\p{Lu}/u.test(w.slice(1)) || known.get(w) === true) return;
      ask("sp.suggest", { word: w }, m => {
        if (!m) return;
        if (m.known) { known.set(w, true); return; }
        known.set(w, false);
        const c = pick(w, m.list || []);
        if (!c) { render(); return; }
        if (ta.value !== text || document.activeElement !== ta) return;   // le texte a bougé entre-temps
        const caret = ta.selectionStart, selEnd = ta.selectionEnd, d = c.length - w.length;
        ta.setSelectionRange(tok.s, tok.e); document.execCommand("insertText", false, c);
        ta.setSelectionRange(caret + d, selEnd + d);
        autos.push({ s: tok.s, e: tok.s + c.length, original: w, to: c, at: Date.now() });
        render();
      });
    }

    // Clic droit : les suggestions du mot ou de l'erreur sous le pointeur, puis les actions d'édition.
    function menu(e) {
      if (!window.CdxFrame || !CdxFrame.menu) return;
      e.preventDefault();
      const mark = document.elementsFromPoint(e.clientX, e.clientY).find(el => el.tagName === "MARK" && el.parentElement === bd);
      const x = e.clientX, y = e.clientY;
      const edit = () => [["Annuler", () => { ta.focus(); document.execCommand("undo"); }, { key: "Ctrl+Z" }], ["Rétablir", () => { ta.focus(); document.execCommand("redo"); }, { key: "Ctrl+Y" }], null,
        ["Couper", () => { ta.focus(); document.execCommand("cut"); }, { key: "Ctrl+X", disabled: ta.selectionStart === ta.selectionEnd }], ["Copier", () => { ta.focus(); document.execCommand("copy"); }, { key: "Ctrl+C", disabled: ta.selectionStart === ta.selectionEnd }],
        ["Tout sélectionner", () => { ta.focus(); ta.select(); }, { key: "Ctrl+A" }]];
      if (!mark) { CdxFrame.menu(edit(), x, y); return; }
      const s = +mark.dataset.s, en = +mark.dataset.e, word = ta.value.slice(s, en);
      const replace = w => { ta.focus(); ta.setSelectionRange(s, en); document.execCommand("insertText", false, w); };
      if (mark.classList.contains("a")) {
        const a = autos[+mark.dataset.i]; if (!a) { CdxFrame.menu(edit(), x, y); return; }
        const restore = () => { autos.splice(autos.indexOf(a), 1); known.set(a.original, true); replace(a.original); render(); };
        CdxFrame.menu([["Rétablir « " + a.original + " »", restore], ["Ajouter « " + a.original + " » au dictionnaire", () => { post({ cmd: "sp.add", word: a.original }); known.set(a.original.toLowerCase(), true); restore(); }], null, ...edit()], x, y, "Corrigé : " + a.original + " → " + a.to);
        return;
      }
      if (mark.classList.contains("g")) {
        const g = gerrs[+mark.dataset.i]; if (!g) { CdxFrame.menu(edit(), x, y); return; }
        const sugg = (g.sugg || []).filter(w => w !== word);
        const items = sugg.length ? sugg.slice(0, 8).map(w => [w, () => replace(w)]) : [["Aucune suggestion", null, { disabled: true }]];
        items.push(null, ["Ignorer ici", () => { gignored.add(g.rule + "|" + word); render(); }], null, ...edit());
        CdxFrame.menu(items, x, y, g.msg.length > 90 ? g.msg.slice(0, 88) + "…" : g.msg);
        return;
      }
      ask("sp.suggest", { word }, m => {
        const list = (m && m.list) || [];
        const items = list.length ? list.slice(0, 8).map(w => [w, () => replace(w)]) : [["Aucune suggestion", null, { disabled: true }]];
        items.push(null, ["Ajouter au dictionnaire", () => { post({ cmd: "sp.add", word }); known.set(word, true); known.set(word.toLowerCase(), true); instances.forEach(i => i.render()); }], ["Ignorer ici", () => { known.set(word, true); instances.forEach(i => i.render()); }], null, ...edit());
        CdxFrame.menu(items, x, y, word);
      });
    }

    const onInput = e => { schedule(); scheduleGrammar(); if (e && e.inputType) autoCorrect(e); };
    const onSel = () => { if (document.activeElement === ta) schedule(); };
    const ro = window.ResizeObserver ? new ResizeObserver(() => render()) : null;
    function start() {
      ta.parentNode.insertBefore(bd, ta);
      ta.addEventListener("input", onInput); ta.addEventListener("scroll", sync); ta.addEventListener("contextmenu", menu); ta.addEventListener("blur", schedule);
      document.addEventListener("selectionchange", onSel); window.addEventListener("resize", schedule);
      if (ro) { ro.observe(ta); if (ta.parentElement) ro.observe(ta.parentElement); }
      instances.add(api); render(); scheduleGrammar();
    }
    function destroy() {
      ta.removeEventListener("input", onInput); ta.removeEventListener("scroll", sync); ta.removeEventListener("contextmenu", menu); ta.removeEventListener("blur", schedule);
      document.removeEventListener("selectionchange", onSel); window.removeEventListener("resize", schedule);
      if (ro) ro.disconnect(); clearTimeout(timer); clearTimeout(gtimer); clearTimeout(autoTimer); bd.remove(); instances.delete(api);
      ta.style.background = bg0; ta.style.position = pos0; ta.style.zIndex = z0; bg = ""; delete ta.__cdxSpell;
    }
    const api = {
      render, refresh: () => { gtext = null; render(); scheduleGrammar(); },
      enable(on) { enabled = !!on; render(); }, get enabled() { return enabled; },
      auto(on) { auto = !!on; }, get autoEnabled() { return auto; },
      grammar(on) { genabled = !!on; if (genabled) { gtext = null; scheduleGrammar(); } render(); }, get grammarEnabled() { return genabled; },
      destroy, tokens: text => tokens(text, markdown),
    };
    ta.__cdxSpell = api; start();
    return api;
  }

  window.CdxSpell = { attach, tokens, masked, distance, pick, get off() { return off; } };
})();
