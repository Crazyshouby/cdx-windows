// Le rendu Markdown de l'application Notes, écrit pour elle : le sous-ensemble qu'on écrit dans un coffre (titres, emphase, code, citations et encarts, listes et tâches,
// tableaux, liens, images, [[liens]] et #étiquettes). Aucune dépendance. Le HTML des notes n'est JAMAIS repris : tout ce qui n'est pas du Markdown est échappé, les adresses
// ne peuvent être que http, https ou mailto, les images distantes ne sont pas chargées. Une note écrite par un tiers (ou par Claude) ne peut donc rien exécuter.
//   Md.render(texte, ctx) -> { html, props }
//   ctx : { resolve(cible) -> chemin de la note ou null,  imageUrl(cible, depuisUneNote) -> adresse de l'image ou null }
(function (root) {
  'use strict';
  const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const slug = s => String(s).toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const IMG = /\.(png|jpe?g|gif|webp|svg|bmp)$/i;
  const indentOf = l => /^ */.exec(l)[0].length;
  const isBlank = l => /^\s*$/.test(l);

  // ---------- en-tête de propriétés ----------
  // Rend les propriétés à afficher et le corps ; `bodyLine` : combien de lignes l'en-tête occupe (les numéros de ligne restent ceux du fichier).
  function frontmatter(text) {
    const lines = text.split('\n');
    if (lines[0].trim() !== '---') return { props: [], body: text, bodyLine: 0 };
    for (let i = 1; i < lines.length; i++) {
      if (lines[i].trim() === '---' || lines[i].trim() === '...') {
        const props = []; let last = null;
        for (const l of lines.slice(1, i)) {
          const li = /^\s+-\s+(.*)$/.exec(l);
          if (li && last) { last[1] += (last[1] ? ', ' : '') + unq(li[1]); continue; }
          const kv = /^([^\s:#][^:]*?)\s*:\s*(.*)$/.exec(l);
          if (kv) { let v = kv[2].trim(); if (v.startsWith('[') && v.endsWith(']')) v = v.slice(1, -1).split(',').map(x => unq(x.trim())).filter(Boolean).join(', '); else v = unq(v); last = [kv[1].trim(), v]; props.push(last); }
        }
        return { props, body: lines.slice(i + 1).join('\n'), bodyLine: i + 1 };
      }
    }
    return { props: [], body: text, bodyLine: 0 };
  }
  function unq(v) {
    if (v.length >= 2 && v[0] === "'" && v[v.length - 1] === "'") return v.slice(1, -1).replace(/''/g, "'");
    if (v.length >= 2 && v[0] === '"' && v[v.length - 1] === '"') return v.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, '\\');
    return v;
  }

  // ---------- inline ----------
  const TAG = /(^|[^\p{L}\p{N}_/#&;)\]])#([\p{L}\p{N}_-]+(?:\/[\p{L}\p{N}_-]+)*)/gu;

  // Emphase et étiquettes sur du texte DÉJÀ échappé (les emplacements \u0000N\u0000 sont gardés tels quels).
  function fmt(s, withTags) {
    s = s.replace(/\*\*\*(?=\S)([\s\S]*?\S)\*\*\*/g, '<strong><em>$1</em></strong>')
      .replace(/\*\*(?=\S)([\s\S]*?\S)\*\*/g, '<strong>$1</strong>')
      .replace(/(^|[^\w])__(?=\S)([\s\S]*?\S)__(?!\w)/g, '$1<strong>$2</strong>')
      .replace(/\*(?=[^\s*])([^*\n]*?[^\s*])\*/g, '<em>$1</em>')
      .replace(/(^|[^\w])_(?=[^\s_])([^_\n]*?[^\s_])_(?!\w)/g, '$1<em>$2</em>')
      .replace(/~~(?=\S)([\s\S]*?\S)~~/g, '<del>$1</del>')
      .replace(/==(?=\S)([\s\S]*?\S)==/g, '<mark>$1</mark>');
    if (withTags) s = s.replace(TAG, (m, pre, t) => /^\d+$/.test(t) ? m : `${pre}<a class="tg" data-tag="${t}">#${t}</a>`);
    return s;
  }

  function inline(src, ctx) {
    ctx = ctx || {};
    const slots = [];
    const hold = html => { slots.push(html); return '\u0000' + (slots.length - 1) + '\u0000'; };
    let s = String(src).replace(/\u0000/g, '');
    s = s.replace(/(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)/g, (m, t, code) => hold('<code>' + esc(code.trim()) + '</code>'));
    s = s.replace(/\\([\\`*_{}\[\]()#+\-.!|~=<>])/g, (m, c) => hold(esc(c)));
    s = s.replace(/(!?)\[\[([^\[\]|#^\r\n]+?)(?:#([^\[\]|\r\n]*))?(?:\|([^\[\]\r\n]*))?\]\]/g, (m, bang, target, heading, alias) => hold(wiki(!!bang, target.trim(), heading, alias, ctx)));
    s = s.replace(/!\[([^\]]*)\]\(\s*((?:[^()\s]|\([^()\s]*\))+)(?:\s+"[^"]*")?\s*\)/g, (m, alt, url) => hold(image(alt, url, ctx)));
    s = s.replace(/\[([^\]]+)\]\(\s*((?:[^()\s]|\([^()\s]*\))+)(?:\s+"[^"]*")?\s*\)/g, (m, text, url) => hold(link(text, url, ctx)));
    s = s.replace(/<((?:https?|mailto):[^\s<>\u0000]+)>/g, (m, u) => hold(ext(u, u)));
    s = s.replace(/(^|[\s(])(https?:\/\/[^\s<\u0000]*[^\s<.,;:!?)\]\u0000])/g, (m, pre, u) => pre + hold(ext(u, u)));
    s = fmt(esc(s), true);
    for (let n = 0; n < 6 && /\u0000\d+\u0000/.test(s); n++) s = s.replace(/\u0000(\d+)\u0000/g, (m, i) => slots[+i] ?? '');
    return s.replace(/\n/g, '<br>');
  }

  function ext(url, text) { return `<a class="ext" data-ext="${esc(url)}" title="${esc(url)}">${esc(text)}</a>`; }

  function wiki(bang, target, heading, alias, ctx) {
    if (bang && IMG.test(target)) {
      const u = ctx.imageUrl ? ctx.imageUrl(target, true) : null;
      return u ? `<img class="emb" src="${esc(u)}" alt="${esc(alias || target)}">` : `<span class="miss">image introuvable : ${esc(target)}</span>`;
    }
    const path = ctx.resolve ? ctx.resolve(target) : null;
    const label = alias ? alias : heading ? target + ' > ' + heading : target;
    return `<a class="wl${path ? '' : ' miss'}${bang ? ' emb-note' : ''}" data-target="${esc(target)}"${path ? ` data-path="${esc(path)}"` : ''}${heading ? ` data-heading="${esc(heading)}"` : ''}>${bang ? '↪ ' : ''}${esc(label)}</a>`;
  }

  function link(text, url, ctx) {
    const m = /^([a-z][a-z0-9+.-]*):/i.exec(url);
    const inner = fmt(esc(text), false);
    if (m) {
      const sch = m[1].toLowerCase();
      return sch === 'http' || sch === 'https' || sch === 'mailto' ? `<a class="ext" data-ext="${esc(url)}" title="${esc(url)}">${inner}</a>` : inner;   // javascript:, data:, file:… : le texte seul
    }
    let u = url; try { u = decodeURIComponent(url); } catch (e) { /* adresse mal encodée : telle quelle */ }
    const h = u.indexOf('#'); const heading = h >= 0 ? u.slice(h + 1) : ''; if (h >= 0) u = u.slice(0, h);
    const target = u.replace(/\.md$/i, '');
    const path = ctx.resolve && target ? ctx.resolve(target) : null;
    return path ? `<a class="wl" data-target="${esc(target)}" data-path="${esc(path)}"${heading ? ` data-heading="${esc(heading)}"` : ''}>${inner}</a>` : `<span class="miss">${inner}</span>`;
  }

  function image(alt, url, ctx) {
    if (/^[a-z][a-z0-9+.-]*:/i.test(url)) return `<span class="blocked" title="${esc(url)}">image externe non chargée (${esc(alt || url)})</span>`;   // aucune requête vers l'extérieur
    let u = url; try { u = decodeURIComponent(url); } catch (e) { /* telle quelle */ }
    const src = IMG.test(u) && ctx.imageUrl ? ctx.imageUrl(u, false) : null;
    return src ? `<img src="${esc(src)}" alt="${esc(alt)}">` : `<span class="miss">image introuvable : ${esc(alt || url)}</span>`;
  }

  // ---------- blocs ----------
  const blockStart = l => /^\s{0,3}(```|~~~|#{1,6}\s|>)/.test(l) || /^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(l);
  const listStart = l => /^(\s*)([-*+]|\d{1,9}[.)])\s+\S/.test(l) || /^(\s*)([-*+]|\d{1,9}[.)])\s*$/.test(l);
  const TABLE_SEP = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;

  function splitRow(l) {
    let t = l.trim(); if (t.startsWith('|')) t = t.slice(1); if (t.endsWith('|') && !t.endsWith('\\|')) t = t.slice(0, -1);
    const cells = []; let cur = '', code = false;
    for (let i = 0; i < t.length; i++) {
      const c = t[i];
      if (c === '`') code = !code;
      if (c === '\\' && t[i + 1] === '|') { cur += '|'; i++; continue; }
      if (c === '|' && !code) { cells.push(cur.trim()); cur = ''; continue; }
      cur += c;
    }
    cells.push(cur.trim()); return cells;
  }

  function blocks(lines, base, ctx) {
    const out = []; let i = 0;
    while (i < lines.length) {
      const ln = lines[i];
      if (isBlank(ln)) { i++; continue; }
      let m = /^(\s{0,3})(```+|~~~+)\s*([\w+#.-]*)[^\n]*$/.exec(ln);
      if (m) {
        const ch = m[2][0], len = m[2].length, buf = []; i++;
        const close = new RegExp('^\\s{0,3}' + (ch === '`' ? '`' : '~') + '{' + len + ',}\\s*$');
        while (i < lines.length && !close.test(lines[i])) { buf.push(lines[i].replace(new RegExp('^ {0,' + m[1].length + '}'), '')); i++; }
        i++;
        out.push(`<pre><code${m[3] ? ` class="lang-${esc(m[3])}"` : ''}>${esc(buf.join('\n'))}</code></pre>`); continue;
      }
      m = /^(#{1,6})\s+(.*?)\s*#*\s*$/.exec(ln);
      if (m) { out.push(`<h${m[1].length} id="h-${slug(m[2])}" data-line="${base + i}">${inline(m[2], ctx)}</h${m[1].length}>`); i++; continue; }
      if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(ln)) { out.push('<hr>'); i++; continue; }
      if (/^\s{0,3}>/.test(ln)) {
        const start = i, buf = [];
        while (i < lines.length && /^\s{0,3}>/.test(lines[i])) { buf.push(lines[i].replace(/^\s{0,3}> ?/, '')); i++; }
        const c = /^\[!([\w-]+)\][+-]?\s*(.*)$/.exec(buf[0]);
        if (c) {
          const type = c[1].toLowerCase(), title = c[2] || (type[0].toUpperCase() + type.slice(1));
          out.push(`<div class="callout ${esc(type.replace(/[^a-z-]/g, ''))}"><div class="ct">${inline(title, ctx)}</div>${blocks(buf.slice(1), base + start + 1, ctx)}</div>`);
        } else out.push(`<blockquote>${blocks(buf, base + start, ctx)}</blockquote>`);
        continue;
      }
      if (i + 1 < lines.length && ln.includes('|') && TABLE_SEP.test(lines[i + 1]) && lines[i + 1].includes('-')) {
        const head = splitRow(ln), seps = splitRow(lines[i + 1]);
        const al = seps.map(s => /^:-+:$/.test(s) ? 'center' : /^-+:$/.test(s) ? 'right' : /^:-+$/.test(s) ? 'left' : '');
        const cell = (tag, t, k) => `<${tag}${al[k] ? ` style="text-align:${al[k]}"` : ''}>${inline(t, ctx)}</${tag}>`;
        let h = '<table><thead><tr>' + head.map((t, k) => cell('th', t, k)).join('') + '</tr></thead><tbody>'; i += 2;
        while (i < lines.length && !isBlank(lines[i]) && lines[i].includes('|')) { h += '<tr>' + splitRow(lines[i]).map((t, k) => cell('td', t, k)).join('') + '</tr>'; i++; }
        out.push(h + '</tbody></table>'); continue;
      }
      if (listStart(ln)) { const r = list(lines, i, base, ctx); out.push(r.html); i = r.next; continue; }
      // paragraphe : jusqu'à une ligne vide ou le début d'un autre bloc
      const buf = [ln]; i++;
      while (i < lines.length && !isBlank(lines[i]) && !blockStart(lines[i]) && !listStart(lines[i]) && !(lines[i].includes('|') && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1]) && lines[i + 1].includes('-'))) { buf.push(lines[i]); i++; }
      out.push(`<p>${inline(buf.map(x => x.replace(/\s+$/, '')).join('\n'), ctx)}</p>`);
    }
    return out.join('\n');
  }

  function list(lines, i, base, ctx) {
    const first = /^(\s*)([-*+]|\d{1,9}[.)])/.exec(lines[i]); const indent = first[1].length, ordered = /\d/.test(first[2]);
    const startNum = ordered ? parseInt(first[2], 10) : 1; const items = [];
    while (i < lines.length) {
      const mm = /^(\s*)([-*+]|\d{1,9}[.)])(\s+(.*)|\s*)$/.exec(lines[i]);
      if (!mm || mm[1].length !== indent || /\d/.test(mm[2]) !== ordered) break;
      const text = mm[4] || '', contentIndent = mm[1].length + mm[2].length + (mm[3].length - text.length || 1);
      const item = [text]; const at = i; i++;
      while (i < lines.length) {
        const l = lines[i];
        if (isBlank(l)) {
          let j = i; while (j < lines.length && isBlank(lines[j])) j++;
          if (j < lines.length && indentOf(lines[j]) >= contentIndent) { for (; i < j; i++) item.push(''); continue; }
          break;
        }
        if (indentOf(l) >= contentIndent) { item.push(l.slice(contentIndent)); i++; continue; }
        if (!listStart(l) && !blockStart(l) && !isBlank(item[item.length - 1])) { item.push(l.trim()); i++; continue; }   // suite « paresseuse » d'un paragraphe
        break;
      }
      items.push({ lines: item, line: base + at });
    }
    const tag = ordered ? 'ol' : 'ul';
    const body = items.map(it => {
      let ls = it.lines, task = null;
      const t = /^\[( |x|X)\]\s+(.*)$/.exec(ls[0]);
      if (t) { task = t[1] !== ' '; ls = [t[2], ...ls.slice(1)]; }
      let h = blocks(ls, it.line, ctx);
      if (!ls.some(isBlank)) h = h.replace(/^<p>([\s\S]*?)<\/p>/, '$1');   // liste « serrée » : pas de paragraphe autour du texte
      return task === null ? `<li>${h}</li>` : `<li class="task-item${task ? ' done' : ''}"><input type="checkbox" class="task" data-line="${it.line}"${task ? ' checked' : ''}> ${h}</li>`;
    }).join('');
    return { html: `<${tag}${ordered && startNum !== 1 ? ` start="${startNum}"` : ''}>${body}</${tag}>`, next: i };
  }

  function propsHtml(props) {
    return '<table class="props">' + props.map(([k, v]) => `<tr><th>${esc(k)}</th><td>${/^tags?$/i.test(k) ? v.split(',').map(t => t.trim()).filter(Boolean).map(t => `<a class="tg" data-tag="${esc(t.replace(/^#/, ''))}">#${esc(t.replace(/^#/, ''))}</a>`).join(' ') : esc(v)}</td></tr>`).join('') + '</table>';
  }

  function render(text, ctx) {
    text = String(text).replace(/\r\n?/g, '\n').replace(/\u0000/g, '');
    const fm = frontmatter(text);
    const lines = fm.body.split('\n').map(l => l.replace(/^\t+/, t => '    '.repeat(t.length)));
    return { html: (fm.props.length ? propsHtml(fm.props) : '') + blocks(lines, fm.bodyLine, ctx || {}), props: fm.props };
  }

  const Md = { render, inline, frontmatter, slug, esc };
  if (typeof module !== 'undefined' && module.exports) module.exports = Md; else root.Md = Md;
})(typeof window !== 'undefined' ? window : globalThis);

