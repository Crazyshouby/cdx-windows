// Le socle de la page de l'Explorateur : aides (sélecteur, icônes, types, chemins). Chargé en premier ; les fichiers suivants partagent ses noms (scripts classiques).
  fillPix();
  const $ = id => document.getElementById(id);
  const sep = '\\';
  // Les réglages (éléments cachés, extensions, vue par dossier, épinglés...) vivent dans l'hôte (explorer.json) et sont partagés par toutes les fenêtres ; onSettings() est appelé à chaque changement.
  let settings = {};
  const setSetting = (key, value) => { settings[key] = value; post({ cmd: 'fs.settings.set', key, value: value === undefined ? null : value }); };
  handlers.settings = m => { settings = m.values || {}; if (typeof onSettings === 'function') onSettings(); };
  const IC = { image: 'png jpg jpeg gif bmp webp svg ico', musique: 'mp3 wav flac ogg m4a aac', video: 'mp4 mkv avi mov webm wmv', journal: 'txt md log json xml csv ini cfg yml yaml toml', apps: 'exe msi bat cmd lnk' };
  const TYPES = { png: 'Image PNG', jpg: 'Image JPEG', jpeg: 'Image JPEG', gif: 'Image GIF', svg: 'Image SVG', webp: 'Image WebP', bmp: 'Image BMP', ico: 'Icône', pdf: 'Document PDF', txt: 'Document texte', md: 'Document Markdown', json: 'Fichier JSON', xml: 'Fichier XML', csv: 'Fichier CSV',
    mp3: 'Fichier MP3', wav: 'Fichier WAV', flac: 'Fichier FLAC', mp4: 'Vidéo MP4', mkv: 'Vidéo MKV', avi: 'Vidéo AVI', zip: 'Dossier compressé', exe: 'Application', msi: 'Installeur', lnk: 'Raccourci', docx: 'Document Word', xlsx: 'Feuille de calcul', pptx: 'Présentation', css: 'Feuille de style', js: 'Fichier JavaScript', html: 'Page HTML', rs: 'Source Rust', cs: 'Source C#', py: 'Script Python' };
  const extOf = n => { const i = n.lastIndexOf('.'); return i > 0 ? n.slice(i + 1).toLowerCase() : ''; };
  const iconOf = it => { if (it.drive) return 'disque'; if (it.d) return 'dossier'; const e = extOf(it.n); for (const k in IC) if (IC[k].split(' ').includes(e)) return k; return 'fichier'; };
  const typeOf = it => it.drive ? 'Disque local' : it.d ? 'Dossier de fichiers' : (TYPES[extOf(it.n)] || (extOf(it.n) ? extOf(it.n).toUpperCase() + ' fichier' : 'Fichier'));
  const pad = n => String(n).padStart(2, '0');
  const dateOf = ms => { if (!ms) return ''; const d = new Date(ms); return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`; };
  const sizeOf = b => { if (b == null || b < 0) return ''; const u = ['o', 'Ko', 'Mo', 'Go', 'To']; let i = 0, v = b; while (v >= 1024 && i < 4) { v /= 1024; i++; } return (i === 0 || v >= 10 ? Math.round(v) : v.toFixed(1).replace('.', ',')) + ' ' + u[i]; };
  const joinP = (d, n) => d ? (d.endsWith(sep) ? d + n : d + sep + n) : n;
  // Dossier parent : « C:\ » a pour parent la liste des disques (''), qui n'en a pas.
  const parentOf = p => { if (!p) return null; if (/^[A-Za-z]:\\?$/.test(p)) return ''; const t = p.replace(/\\$/, ''), i = t.lastIndexOf(sep); if (i < 0) return null; return i === 2 && t[1] === ':' ? t.slice(0, 3) : t.slice(0, i); };
  const baseOf = p => !p ? 'Ce PC' : (p.replace(/\\$/, '').split(sep).pop() || p);

