// Aperçu et boutons latéraux de la souris de l'Explorateur KDX. Chargé après le script de explorer.html dont il utilise les fonctions
// ($, T, selected, pathOf, typeOf, iconOf, extOf, sizeOf, dateOf, esc, post, handlers).

// ---------------- aperçu ----------------
const MEDIA = { img: 'png jpg jpeg gif bmp webp svg ico avif', vid: 'mp4 webm m4v mov mkv ogv', aud: 'mp3 wav flac ogg m4a aac opus', pdf: 'pdf' };
const kindOf = it => { if (it.d) return 'dir'; const e = extOf(it.b ?? it.n); for (const k in MEDIA) if (MEDIA[k].split(' ').includes(e)) return k; return 'txt'; };
// Les disques sont servis en lecture seule sous x.drive.kdx.shell (voir ExplorerForm.MapExtraHosts) : images, sons, vidéos et PDF s'y chargent.
const fileUrl = p => { const m = /^([A-Za-z]):\\(.+)$/.exec(p); return m ? `https://${m[1].toLowerCase()}.drive.kdx.shell/` + m[2].split('\\').map(encodeURIComponent).join('/') : null; };

let pvOn = false, pvKey = null;
try { pvOn = localStorage.getItem('kdx.pv') === '1'; } catch { /* stockage indisponible : l'aperçu reste fermé */ }

function setPreview(on) {
  pvOn = on; pvKey = null;
  try { localStorage.setItem('kdx.pv', on ? '1' : '0'); } catch { }
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
  if (m.kind === 'text') {
    box.innerHTML = '<div class="log pvtext"></div>';
    box.firstChild.textContent = m.text + (m.truncated ? '\n\n… aperçu limité aux 64 premiers Ko' : '');
  } else if (m.kind === 'binary') box.innerHTML = `<div class="pvmid">${pix(iconOf(s[0]), 64)}</div><div class="empty">Pas d’aperçu pour ce type de fichier</div>`;
  else box.innerHTML = `<div class="pvmid">${pix(iconOf(s[0]), 64)}</div><div class="empty">${esc(m.error || 'Aperçu impossible')}</div>`;
};

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
