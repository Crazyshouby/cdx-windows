/* cdx-spell.js : le correcteur orthographique des zones de texte (textarea), pour toute la suite CDX.

   La page ne corrige rien elle-même : elle découpe le texte en mots, demande à l'hôte lesquels sont inconnus et les souligne ; au clic droit sur un mot
   souligné, elle demande des suggestions et les propose dans le menu de la charte (CdxFrame.menu), avec « Ajouter au dictionnaire ». Le dictionnaire,
   la langue et les mots ajoutés vivent dans l'hôte (cdx-windows : Hunspell et le dictionnaire français de Dicollecte, Cdx.Host/Spell.cs).

   Usage :  const sp = CdxSpell.attach(textarea, { markdown: true, enabled: true });   sp.enable(false);  sp.refresh();  sp.destroy();
   - markdown : ne pas vérifier le code (``` ... ```, `code`), les liens [[Nom]] et [texte](cible), les #étiquettes.
   - Jamais vérifiés : adresses web et courriel, mots avec chiffres, sigles en capitales, lettres seules, et le mot en cours de frappe (celui du curseur) ;
     une élision (l', d', qu'…) est retirée avant la vérification.

   Contrat avec l'hôte (messages JSON de WebView2, window.chrome.webview) :
     page → hôte : { cmd: "sp.check", id, words: [...] }   { cmd: "sp.suggest", id, word }   { cmd: "sp.add", word }
     hôte → page : { type: "sp.result", id, bad: [...] }   { type: "sp.suggestions", id, word, list: [...] }   { type: "sp.added", word }   { type: "sp.off" }
   Sans hôte, ou si l'hôte répond sp.off (pas de dictionnaire), rien n'est souligné.

   Le soulignement : une zone de texte ne se colore pas par morceaux. Un calque (div) est posé sous elle, même police, même largeur utile, mêmes retours
   à la ligne, texte transparent, les mots inconnus dans des <mark> soulignés en rouge ; la zone de texte devient transparente (son fond passe au calque)
   et défile avec lui. Le calque est recalé à chaque changement de taille ; il ne change pas la mise en page de la page. */
(() => {
  const webview = window.chrome && window.chrome.webview;
  const known = new Map();          // mot → true (connu) | false (inconnu) ; partagé par toutes les zones de la page
  const waiting = new Map();        // id de demande → fonction de rappel
  const instances = new Set();
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

  const COPY = ["fontFamily", "fontSize", "fontWeight", "fontStyle", "fontVariant", "fontKerning", "lineHeight", "letterSpacing", "wordSpacing", "textTransform", "textIndent", "textRendering",
    "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth", "boxSizing", "whiteSpace", "overflowWrap", "wordBreak", "tabSize", "direction"];

  function attach(ta, opts = {}) {
    if (!ta || ta.tagName !== "TEXTAREA") return null;
    if (ta.__cdxSpell) return ta.__cdxSpell;
    listen();
    const markdown = !!opts.markdown; let enabled = opts.enabled !== false, pendingBatch = null, timer = 0;
    const bd = document.createElement("div"); bd.className = "cdx-spell"; bd.setAttribute("aria-hidden", "true");
    const bg0 = ta.style.background, pos0 = ta.style.position, z0 = ta.style.zIndex;
    Object.assign(bd.style, { position: "absolute", overflow: "hidden", zIndex: "0", margin: "0", color: "transparent", userSelect: "none", borderStyle: "solid", borderColor: "transparent" });
    if (!document.getElementById("cdx-spell-style")) {
      const st = document.createElement("style"); st.id = "cdx-spell-style";
      st.textContent = ".cdx-spell mark { background: none; color: transparent; text-decoration: underline wavy var(--red, #ff5a4a); text-decoration-skip-ink: none; text-underline-offset: 2px; }";
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

    function render() {
      if (!geometry()) return;
      const text = ta.value;
      if (!enabled || off || !text) { bd.textContent = text ? text + (text.endsWith("\n") ? " " : "") : ""; sync(); return; }
      const toks = tokens(text, markdown), unknown = [];
      const caret = document.activeElement === ta ? ta.selectionStart : -1;
      let html = "", last = 0;
      for (const t of toks) {
        const k = known.get(t.w);
        if (k === undefined) { if (!known.has(t.w.toLowerCase()) || known.get(t.w.toLowerCase()) === false) unknown.push(t.w); else continue; }
        if (k !== false) continue;
        if (caret >= t.s && caret <= t.e) continue;   // le mot en cours de frappe
        html += esc(text.slice(last, t.s)) + `<mark data-s="${t.s}" data-e="${t.e}">${esc(t.w)}</mark>`; last = t.e;
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

    // Clic droit : les suggestions du mot souligné sous le pointeur, puis les actions d'édition.
    function menu(e) {
      if (!window.CdxFrame || !CdxFrame.menu) return;
      e.preventDefault();
      const mark = enabled && !off ? document.elementsFromPoint(e.clientX, e.clientY).find(el => el.tagName === "MARK" && el.parentElement === bd) : null;
      const x = e.clientX, y = e.clientY;
      const edit = () => [["Annuler", () => { ta.focus(); document.execCommand("undo"); }, { key: "Ctrl+Z" }], ["Rétablir", () => { ta.focus(); document.execCommand("redo"); }, { key: "Ctrl+Y" }], null,
        ["Couper", () => { ta.focus(); document.execCommand("cut"); }, { key: "Ctrl+X", disabled: ta.selectionStart === ta.selectionEnd }], ["Copier", () => { ta.focus(); document.execCommand("copy"); }, { key: "Ctrl+C", disabled: ta.selectionStart === ta.selectionEnd }],
        ["Tout sélectionner", () => { ta.focus(); ta.select(); }, { key: "Ctrl+A" }]];
      if (!mark) { CdxFrame.menu(edit(), x, y); return; }
      const s = +mark.dataset.s, en = +mark.dataset.e, word = ta.value.slice(s, en);
      const replace = w => { ta.focus(); ta.setSelectionRange(s, en); document.execCommand("insertText", false, w); };
      ask("sp.suggest", { word }, m => {
        const list = (m && m.list) || [];
        const items = list.length ? list.slice(0, 8).map(w => [w, () => replace(w)]) : [["Aucune suggestion", null, { disabled: true }]];
        items.push(null, ["Ajouter au dictionnaire", () => { post({ cmd: "sp.add", word }); known.set(word, true); known.set(word.toLowerCase(), true); instances.forEach(i => i.render()); }], ["Ignorer ici", () => { known.set(word, true); instances.forEach(i => i.render()); }], null, ...edit());
        CdxFrame.menu(items, x, y, word);
      });
    }

    const onSel = () => { if (document.activeElement === ta) schedule(); };
    const ro = window.ResizeObserver ? new ResizeObserver(() => render()) : null;
    function start() {
      ta.parentNode.insertBefore(bd, ta);
      ta.addEventListener("input", schedule); ta.addEventListener("scroll", sync); ta.addEventListener("contextmenu", menu); ta.addEventListener("blur", schedule);
      document.addEventListener("selectionchange", onSel); window.addEventListener("resize", schedule);
      if (ro) { ro.observe(ta); if (ta.parentElement) ro.observe(ta.parentElement); }
      instances.add(api); render();
    }
    function destroy() {
      ta.removeEventListener("input", schedule); ta.removeEventListener("scroll", sync); ta.removeEventListener("contextmenu", menu); ta.removeEventListener("blur", schedule);
      document.removeEventListener("selectionchange", onSel); window.removeEventListener("resize", schedule);
      if (ro) ro.disconnect(); clearTimeout(timer); bd.remove(); instances.delete(api);
      ta.style.background = bg0; ta.style.position = pos0; ta.style.zIndex = z0; bg = ""; delete ta.__cdxSpell;
    }
    const api = { render, refresh: render, enable(on) { enabled = !!on; render(); }, get enabled() { return enabled; }, destroy, tokens: text => tokens(text, markdown) };
    ta.__cdxSpell = api; start();
    return api;
  }

  window.CdxSpell = { attach, tokens, masked, get off() { return off; } };
})();
