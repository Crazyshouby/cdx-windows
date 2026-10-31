// Aperçu et boutons latéraux de la souris de l'Explorateur CDX. Chargé après le script de explorer.html dont il utilise les fonctions
// ($, T, selected, pathOf, typeOf, iconOf, extOf, sizeOf, dateOf, esc, post, handlers).

// ---------------- aperçu ----------------
const MEDIA = { img: 'png jpg jpeg gif bmp webp svg ico avif', vid: 'mp4 webm m4v mov mkv ogv', aud: 'mp3 wav flac ogg m4a aac opus', pdf: 'pdf' };
const kindOf = it => { if (it.d) return 'dir'; const e = extOf(it.b ?? it.n); for (const k in MEDIA) if (MEDIA[k].split(' ').includes(e)) return k; return 'txt'; };
// Les disques sont servis en lecture seule sous x.drive.cdx.shell (voir ExplorerForm.MapExtraHosts) : images, sons, vidéos et PDF s'y chargent.
const fileUrl = p => { const m = /^([A-Za-z]):\\(.+)$/.exec(p); return m ? `https://${m[1].toLowerCase()}.drive.cdx.shell/` + m[2].split('\\').map(encodeURIComponent).join('/') : null; };

let pvOn = false, pvKey = null;
try { pvOn = localStorage.getItem('cdx.pv') === '1'; } catch { /* stockage indisponible : l'aperçu reste fermé */ }

function setPreview(on) {
  pvOn = on; pvKey = null;
  try { localStorage.setItem('cdx.pv', on ? '1' : '0'); } catch { }
  $('pv').hidden = !on; $('cPv').classList.toggle('on', on);
  updatePreview();
}

function updatePreview() {
  if (!pvOn || !T() || !T().rows) return;
  const s = selected();
  const key = s.length === 1 ? pathOf(s[0]) + '|' + s[0].m : s.length ? '*' + s.map(x => x.n).join('|') : '';
  if (key === pvKey) return;   // même sélection : on ne recharge pas (un clic redessine la liste à chaque fois)
  pvKey = key;
  const box = $('pvbox'), info = $('pvinfo');
  if (!s.length) { box.innerHTML = '<div class="empty">Sélectionnez un fichier pour en voir l’aperçu</div>'; info.innerHTML = ''; return; }
  if (s.length > 1) {
    const bytes = s.reduce((a, i) => a + (i.s > 0 ? i.s : 0), 0);
    box.innerHTML = `<div class="pvmid">${pix('liste', 64)}</div>`;
    info.innerHTML = `<div class="pvn">${s.length} éléments</div>${bytes ? `<div class="faint">${sizeOf(bytes)}</div>` : ''}`; return;
  }
  const it = s[0], p = pathOf(it), k = it.drive ? 'dir' : kindOf(it), url = fileUrl(p);
  info.innerHTML = `<div class="pvn">${esc(it.n)}</div><div class="faint">${esc(typeOf(it))}${it.s >= 0 ? ' · ' + sizeOf(it.s) : ''}</div>${it.m ? `<div class="faint">Modifié le ${dateOf(it.m)}</div>` : ''}`;
  const icon = `<div class="pvmid">${pix(iconOf(it), 64)}</div>`;
  const none = msg => { box.innerHTML = icon + `<div class="empty">${msg}</div>`; };

  if (k === 'dir') box.innerHTML = icon;
  else if (!url) none('Aperçu indisponible pour ce chemin');
  else if (k === 'img') {
    box.innerHTML = `<div class="pvmid"><img src="${url}" alt=""></div>`;
    const img = box.querySelector('img');
    img.onload = () => { if (pvKey === key) info.insertAdjacentHTML('beforeend', `<div class="faint">${img.naturalWidth} × ${img.naturalHeight} px</div>`); };
    img.onerror = () => { if (pvKey === key) none('Image illisible'); };
  }
  else if (k === 'vid') { box.innerHTML = `<div class="pvmid"><video controls preload="metadata" src="${url}"></video></div>`; box.querySelector('video').onerror = () => { if (pvKey === key) none('Vidéo non prise en charge'); }; }
  else if (k === 'aud') { box.innerHTML = `${icon}<div class="pvmid" style="flex:none;padding:0 10px 14px"><audio controls preload="metadata" src="${url}" style="width:100%"></audio></div>`; }
  else if (k === 'pdf') box.innerHTML = `<iframe src="${url}"></iframe>`;
  else { box.innerHTML = '<div class="empty">Chargement…</div>'; post({ cmd: 'fs.preview', path: p, tab: T().id }); }
}

handlers['fs.preview'] = m => {
  const s = selected(); if (s.length !== 1 || pathOf(s[0]) !== m.path) return;   // réponse périmée : la sélection a changé
  const box = $('pvbox');
  const tail = m.truncated ? '\n\n… aperçu limité aux 64 premiers Ko' : '';
  if (m.kind === 'text') {
    box.innerHTML = '<div class="log pvtext"></div>';
    box.firstChild.textContent = m.text + tail;
  } else if (m.kind === 'md') {   // Markdown rendu, avec le moteur des Notes (md.js) ; les [[liens]] et images ne mènent nulle part ici
    box.innerHTML = '<div class="pvmd"></div>';
    box.firstChild.innerHTML = Md.render(m.text, { resolve: () => null, imageUrl: () => null }).html + (m.truncated ? '<p class="faint">… aperçu limité aux 64 premiers Ko</p>' : '');
  } else if (m.kind === 'code') {
    box.innerHTML = '<div class="log pvtext code"></div>';
    box.firstChild.innerHTML = highlight(m.text, m.lang) + esc(tail);
  } else if (m.kind === 'office') {
    box.innerHTML = '<div class="log pvtext"></div>';
    box.firstChild.textContent = (m.text.trim() ? m.text : '(aucun texte dans ce document)') + tail;
  } else if (m.kind === 'binary') box.innerHTML = `<div class="pvmid">${pix(iconOf(s[0]), 64)}</div><div class="empty">Pas d’aperçu pour ce type de fichier</div>`;
  else box.innerHTML = `<div class="pvmid">${pix(iconOf(s[0]), 64)}</div><div class="empty">${esc(m.error || 'Aperçu impossible')}</div>`;
};

// ---------------- code coloré : commentaires, chaînes, nombres, mots-clés, balises — une poignée de familles de langages, le reste reste du texte ----------------
const KW = {
  js: 'const let var function return if else for while do switch case break continue new class extends import export from default async await try catch finally throw typeof instanceof of in this null undefined true false yield static get set',
  cs: 'using namespace class struct interface enum record public private internal protected static readonly const var void int uint long string bool double float decimal char byte object new return if else for foreach while do switch case break continue try catch finally throw null true false this base async await out ref in is as get set override virtual abstract sealed partial where',
  py: 'def class return if elif else for while import from as pass break continue try except finally raise with lambda yield None True False and or not in is global nonlocal async await del assert',
  rs: 'fn let mut pub struct enum impl trait use mod crate self Self return if else match for while loop in as const static ref move async await unsafe where type dyn true false Some None Ok Err',
  ps1: 'function param if else elseif foreach for while do switch return try catch finally throw begin process end in using class enum break continue',
  go: 'func package import var const type struct interface map chan return if else for range switch case break continue go defer select nil true false',
  java: 'class interface enum extends implements public private protected static final void int long double float boolean char byte new return if else for while do switch case break continue try catch finally throw throws this super null true false import package fun val var when object',
  c: 'int char long short unsigned signed void float double struct union enum typedef static const extern return if else for while do switch case break continue sizeof include define ifdef ifndef endif class public private protected virtual template namespace using new delete this nullptr true false auto',
  sql: 'select from where insert into values update set delete create table drop alter index join left right inner outer on group by order having limit as and or not null primary key distinct union',
  sh: 'if then else elif fi for while do done case esac function return echo exit export local in',
  bat: 'echo set if else goto call exit for in do not exist errorlevel rem setlocal endlocal pause',
};
const FAM = { mjs: 'js', ts: 'js', tsx: 'js', jsx: 'js', json: 'js', hpp: 'c', h: 'c', cpp: 'c', kt: 'java', psm1: 'ps1', htm: 'html', svg: 'xml', yml: 'yaml', cfg: 'ini', cmd: 'bat', cmake: 'sh', iss: 'ini' };
function highlight(text, lang) {
  const fam = FAM[lang] || lang || '', kws = new Set((KW[fam] || '').split(' ').filter(Boolean));
  const parts = [];
  if (['js', 'cs', 'c', 'go', 'rs', 'java', 'css', 'sql'].includes(fam)) parts.push('(\\/\\*[\\s\\S]*?\\*\\/|\\/\\/[^\\n]*)');
  else if (['py', 'ps1', 'sh', 'yaml', 'toml', 'ini'].includes(fam)) parts.push(fam === 'ps1' ? '(<#[\\s\\S]*?#>|#[^\\n]*)' : fam === 'ini' ? '(^[ \\t]*[;#][^\\n]*)' : '(#[^\\n]*)');
  else if (['html', 'xml', 'md'].includes(fam)) parts.push('(<!--[\\s\\S]*?-->)');
  else if (fam === 'bat') parts.push('(^[ \\t]*(?:rem|::)[^\\n]*)');
  else parts.push('((?!x)x)');
  if (fam === 'sql') parts[0] = '(--[^\\n]*|\\/\\*[\\s\\S]*?\\*\\/)';
  parts.push('("(?:[^"\\\\\\n]|\\\\.)*"|\'(?:[^\'\\\\\\n]|\\\\.)*\'|`(?:[^`\\\\]|\\\\.)*`)');   // chaînes
  parts.push('(\\b\\d[\\w.]*\\b)');                                                       // nombres
  parts.push(['html', 'xml'].includes(fam) ? '(<\\/?[A-Za-z][\\w:.-]*|\\/?>)' : '((?!x)x)');   // balises
  parts.push('(\\b[A-Za-z_$][\\w$]*\\b)');                                                 // mots
  const re = new RegExp(parts.join('|'), 'gmi'); let out = '', last = 0, m;
  while ((m = re.exec(text))) {
    out += esc(text.slice(last, m.index)); last = re.lastIndex;
    const cls = m[1] != null ? 'cm' : m[2] != null ? 'st' : m[3] != null ? 'nu' : m[4] != null ? 'tg' : (kws.has(fam === 'sql' || fam === 'bat' ? m[5].toLowerCase() : m[5]) ? 'kw' : '');
    out += cls ? `<span class="${cls}">${esc(m[0])}</span>` : esc(m[0]);
    if (m[0].length === 0) re.lastIndex++;
  }
  return out + esc(text.slice(last));
}

$('cPv').onclick = () => setPreview(!pvOn);
$('pv').hidden = !pvOn; $('cPv').classList.toggle('on', pvOn);
document.addEventListener('keydown', e => { if (e.altKey && e.key.toLowerCase() === 'p') { e.preventDefault(); setPreview(!pvOn); } });

// ---------------- boutons latéraux de la souris : précédent (3) et suivant (4), comme dans l'Explorateur de Windows ----------------
let lastNav = 0;
const navigate = dir => { const n = Date.now(); if (n - lastNav < 250) return; lastNav = n; $(dir === 'back' ? 'back' : 'fwd').click(); };   // un bouton désactivé ne réagit pas
for (const type of ['mousedown', 'mouseup', 'auxclick', 'click', 'pointerdown', 'pointerup']) {
  addEventListener(type, e => { if (e.button === 3 || e.button === 4) { log('page : ' + type + ' bouton ' + e.button); e.preventDefault(); e.stopPropagation(); if (type === 'mouseup') navigate(e.button === 3 ? 'back' : 'fwd'); } }, true);
}
handlers.nav = m => navigate(m.dir);   // même geste arrivé par WM_APPCOMMAND (voir ExplorerForm.WndProc)

updatePreview();
