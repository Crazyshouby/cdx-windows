// YouTube (cdx-youtube.exe) : la page de l'interface. Elle ne parle jamais à YouTube : elle demande au programme (cdx.send) et affiche ce qu'il renvoie, déjà converti
// par la couche de lecture (YtLecture.cs) : { id, titre, chaine, chaineId, duree, miniature, vues, date, genre (video|short|direct|liste|mix), listeId, index }.
// Le lecteur : l'image vient de la page YouTube cachée (une vue native que le programme pose sur le rectangle .lecteur de la fiche, d'après le message
// « yt.lecteur »), dont toute l'interface est masquée ; les commandes sont les nôtres (.commandes, sous la vidéo), qui pilotent l'API du lecteur de YouTube
// (« yt.cmd ») et suivent son état (« lecture », 4 fois par seconde). Tout ce qui recouvrirait la vue native (menus, dialogues) l'efface un instant (win.overlay).
//   demandes :  ready · yt.accueil · yt.abonnements · yt.recherche { q } · yt.video { id, liste, index } · yt.chaine { id, url, params } · yt.liste { id }
//               yt.plus { vue } · yt.connexion · yt.deconnexion · yt.changer · yt.lecteur { visible, x, y, w, h } · yt.cmd { action, valeur } (dont confirmer : un bouton de l'écran de YouTube)
//               (et celles de host.js : log, suite.accent, win.*)
//   réponses :  etat { pret, connecte, compte, bloqueur, extraction, veille } · liste { vue, requete, ajout, reinit, videos, suite, messages, pubs, chaine, entete }
//               fiche (spontanee : le lecteur est passé seul à une autre vidéo) · lecture (état du lecteur) · pleinecran · occupe · erreur · theme
(() => {
  'use strict';
  // host.js (chargé avant) : thème de l'adresse, journal des erreurs JS, menu « Réglages » avec le thème de la suite
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const post = (cmd, args) => cdx.send(cmd, args || {});
  const LISTES = { accueil: 'Accueil', abonnements: 'Abonnements', recherche: 'Recherche', chaine: 'Chaîne', liste: 'Liste' };
  // les onglets d'une chaîne qu'on sait afficher (repérés par la fin de leur adresse : leurs titres suivent la langue)
  const ONGLETS = ['featured', 'videos', 'shorts', 'streams', 'playlists', 'podcasts', 'shows'];

  // ---------- état ----------
  let etat = { pret: false, connecte: false };
  let cur = { vue: 'attente' };          // la vue affichée : { vue, requete, videos, suite, messages, fiche, chaine, entete }
  const pile = [];                       // les vues précédentes, pour « Retour » (avec leur position de défilement)
  let occupe = false, plusEnCours = false;
  let recu = 0, derniereErreur = null;   // pour l'essai automatique
  let rectEnvoye = '';                   // le dernier rectangle du lecteur envoyé au programme

  function statut(texte, couleur) {
    $('st1').textContent = texte;
    $('orb').className = 'orb' + (couleur ? ' ' + couleur : '') + (occupe ? ' pulse' : '');
  }
  function majEtat() {
    $('compte').textContent = !etat.pret ? '…' : etat.connecte ? (etat.compte ? etat.compte.replace(/ \(.*\)$/, '') : 'Connecté') : 'Se connecter';
    $('bCompte').title = etat.connecte ? 'Compte Google : ' + (etat.compte || 'connecté') + ' (changer de compte, se déconnecter)' : 'Ouvrir la fenêtre de connexion Google';
    const b = etat.bloqueur ? etat.bloqueur : 'SANS bloqueur';
    $('st2').textContent = etat.pret ? `${etat.connecte ? 'connecté' : 'non connecté'} · ${b} · extraction : ${etat.extraction}${etat.veille ? ' · veille : ' + etat.veille : ''}` : '';
    if (!occupe && !etat.pret) statut('Chargement de YouTube…', '');
    $('bRetour').disabled = pile.length === 0;
    $('bAccueil').classList.toggle('on', cur.vue === 'accueil');
    $('bAbos').classList.toggle('on', cur.vue === 'abonnements');
  }

  // ---------- rendu ----------
  const badge = v => v.genre === 'direct' ? '<span class="duree direct">EN DIRECT</span>'
    : v.genre === 'liste' ? `<span class="duree liste">${esc(v.duree || 'Liste')}</span>` : v.genre === 'mix' ? '<span class="duree liste">Mix</span>'
    : v.duree ? `<span class="duree">${esc(v.duree)}</span>` : v.genre === 'short' ? '<span class="duree">Short</span>' : '';
  const vign = v => `<div class="vign${v.genre === 'liste' || v.genre === 'mix' ? ' pile' : ''}">${v.miniature ? `<img loading="lazy" referrerpolicy="no-referrer" alt="" src="${esc(v.miniature)}">` : ''}${badge(v)}</div>`;
  const meta = v => v.genre === 'liste' ? 'Liste de lecture' : v.genre === 'mix' ? 'Mix : YouTube enchaîne des vidéos proches' : [v.vues, v.date].filter(Boolean).map(esc).join(' · ');
  const chaine = (nom, id) => nom ? (id ? `<span class="lien" data-chaine="${esc(id)}" title="Ouvrir la chaîne">${esc(nom)}</span>` : esc(nom)) : '';
  // les attributs d'un élément cliquable : une vidéo (et sa liste), une liste, un mix
  const cible = v => `data-id="${esc(v.id || '')}" data-genre="${esc(v.genre)}"${v.listeId ? ` data-liste="${esc(v.listeId)}"` : ''}${v.index != null ? ` data-index="${v.index}"` : ''}`;
  const carte = v => `<div class="carte" tabindex="0" ${cible(v)} title="${esc(v.titre)}">${vign(v)}<div class="t">${esc(v.titre)}</div><div class="c">${chaine(v.chaine, v.chaineId)}</div><div class="m">${meta(v)}</div></div>`;

  function enteteChaine(c) {
    const onglets = (c.onglets || []).filter(o => ONGLETS.includes(o.url.split('/').pop()));
    return `<div class="tete">
        ${c.avatar ? `<img class="avatar" alt="" referrerpolicy="no-referrer" src="${esc(c.avatar)}">` : ''}
        <div class="qui"><div class="nom">${esc(c.nom || '')}</div><div class="faint">${[c.pseudo, c.abonnes, c.nbVideos].filter(Boolean).map(esc).join(' · ')}</div>
        ${c.description ? `<div class="apropos">${esc(c.description)}</div>` : ''}</div>
      </div>
      <div class="tabs onglets">${onglets.map(o => `<button class="tab${o.actif ? ' on' : ''}" data-onglet="${esc(o.url)}" data-params="${esc(o.params || '')}">${esc(o.titre)}</button>`).join('')}</div>`;
  }
  function enteteListe(e) {
    return `<div class="tete">
        <div class="qui"><div class="nom">${esc(e.titre || '(liste sans titre)')}</div>
        <div class="faint">${e.auteur ? 'de ' + chaine(e.auteur, e.auteurId) + ' · ' : ''}${esc(e.infos || '')}</div>
        ${e.description ? `<div class="apropos">${esc(e.description)}</div>` : ''}</div>
        ${e.premierId ? `<button class="btn primary" data-a="toutlire">Tout lire</button>` : ''}
      </div>`;
  }

  function rendre() {
    const z = $('vue');
    $('zone').classList.toggle('fige', cur.vue === 'video');
    if (pleinEcran && cur.vue !== 'video') cmd('pleinecran');   // on quitte la fiche : on quitte aussi le plein écran   // la fiche ne défile pas en bloc : le lecteur garde sa place, ses colonnes défilent
    majEtat();
    if (cur.vue === 'attente') { z.innerHTML = '<div class="vide">Chargement de YouTube dans la page cachée…</div>'; placerLecteur(); return; }
    if (cur.vue === 'video') { rendreFiche(z, cur.fiche); return; }
    if (document.activeElement !== $('q')) $('q').value = cur.vue === 'recherche' ? cur.requete : '';
    let haut = '', titre;
    if (cur.vue === 'chaine') { haut = enteteChaine(cur.chaine || {}); titre = (cur.chaine && cur.chaine.nom) || 'Chaîne'; }
    else if (cur.vue === 'liste') { haut = enteteListe(cur.entete || {}); titre = (cur.entete && cur.entete.titre) || 'Liste'; }
    else titre = cur.vue === 'recherche' ? `Résultats pour « ${cur.requete} »` : LISTES[cur.vue];
    let corps;
    if (!cur.videos.length) {
      const msg = (cur.messages || []).filter(Boolean).map(esc).join('<br>');
      corps = (cur.vue === 'accueil' || cur.vue === 'abonnements') && !etat.connecte
        ? `<div class="vide">Sans compte, ${cur.vue === 'accueil' ? "l'accueil de YouTube est vide" : "il n'y a pas d'abonnements"}.${msg ? '<br>' + msg : ''}<br><button class="btn" data-a="connexion">Se connecter</button></div>`
        : `<div class="vide">Rien à afficher ici.${msg ? '<br>' + msg : ''}</div>`;
    } else corps = `<div class="grille">${cur.videos.map(carte).join('')}</div>` + (cur.suite ? `<div class="plus"><button class="btn" data-a="plus">${plusEnCours ? 'Chargement…' : 'Plus'}</button></div>` : '');
    const n = cur.videos.length;
    z.innerHTML = haut + (cur.vue === 'chaine' || cur.vue === 'liste' ? '' : `<div class="entete"><span>${esc(titre)}</span><span class="n">${n} élément${n > 1 ? 's' : ''}</span></div>`) + corps;
    CdxFrame.title(cur.vue === 'accueil' ? 'YouTube' : 'YouTube — ' + titre);
    placerLecteur();
  }

  function rendreFiche(z, f) {
    const ligne = (v, on) => `<div class="sug${on ? ' on' : ''}" tabindex="0" ${cible(v)} title="${esc(v.titre)}">${vign(v)}<div class="tx"><div class="t">${esc(v.titre)}</div><div class="c">${chaine(v.chaine, v.chaineId)}</div><div class="m">${meta(v)}</div></div></div>`;
    const sug = (f.suggestions || []).map(v => ligne(v, false)).join('');
    const l = f.liste;
    const liste = l ? `<div class="sec">Liste : <span class="lien" data-ouvreliste="${esc(l.id)}" title="Ouvrir la liste">${esc(l.titre || '')}</span> — ${l.index + 1} / ${l.total || l.videos.length}${l.auteur ? ' · ' + chaine(l.auteur, l.auteurId) : ''}</div>
      <div class="sugs encours" id="encours">${l.videos.map(v => ligne(v, v.id === f.id)).join('')}</div>` : '';
    z.innerHTML = `<div class="fiche">
      <div class="principal">
        <div class="lecteur" id="lecteur"><span>Chargement du lecteur…</span></div>
        ${COMMANDES}
        <div class="infos">
          <h2>${esc(f.titre || '(sans titre)')}</h2>
          <div class="chaine${f.chaineId ? ' lien' : ''}"${f.chaineId ? ` data-chaine="${esc(f.chaineId)}" title="Ouvrir la chaîne"` : ''}>${f.avatar ? `<img alt="" referrerpolicy="no-referrer" src="${esc(f.avatar)}">` : ''}<span>${esc(f.chaine || '')}</span><span class="ab">${esc(f.abonnes || '')}</span></div>
          <div class="meta">${[f.vues, f.date].filter(Boolean).map(esc).join(' · ')}</div>
          <div class="desc">${esc(f.description || 'Pas de description.')}</div>
        </div>
      </div>
      <div class="cote">${liste}<div class="sec">Suggestions (${(f.suggestions || []).length})</div><div class="sugs">${sug || '<div class="vide">Aucune suggestion.</div>'}</div></div>
    </div>`;
    CdxFrame.title('YouTube — ' + (f.titre || ''));
    blocageAffiche = '';   // un nouveau rectangle du lecteur : le message éventuel sera remis par le prochain état du lecteur
    if (lecture && lecture.blocage && lecture.id === f.id) majBlocage(lecture.blocage);
    const on =document.querySelector('#encours .sug.on'); if (on) on.scrollIntoView({ block: 'nearest' });
    suivreLecteur();
  }

  // ---------- nos commandes du lecteur ----------
  const svg = d => `<svg viewBox="0 0 10 10" width="10" height="10" shape-rendering="crispEdges"><path d="${d}" fill="currentColor"/></svg>`;
  const IC = {
    lire: svg('M2 1H3V9H2ZM3 2H4V8H3ZM4 3H5V7H4ZM5 3H6V7H5ZM6 4H7V6H6ZM7 4H8V6H7Z'),
    pause: svg('M2 1H4V9H2ZM6 1H8V9H6Z'),
    prec: svg('M1 1H3V9H1ZM3 5H4V5ZM4 4H5V6H4ZM5 3H6V7H5ZM6 2H7V8H6ZM7 1H9V9H7Z'),
    suiv: svg('M7 1H9V9H7ZM1 1H3V9H1ZM3 2H4V8H3ZM4 3H5V7H4ZM5 4H6V6H5Z'),
    son: svg('M1 3H3V7H1ZM3 2H4V8H3ZM4 1H5V9H4ZM6 3H7V7H6ZM8 1H9V9H8Z'),
    muet: svg('M1 3H3V7H1ZM3 2H4V8H3ZM4 1H5V9H4ZM6 3H7V4H6ZM7 4H8V6H7ZM8 3H9V4H8ZM6 6H7V7H6ZM8 6H9V7H8Z'),
    plein: svg('M1 1H4V2H2V4H1ZM6 1H9V4H8V2H6ZM1 6H2V8H4V9H1ZM8 6H9V9H6V8H8Z'),
    sortir: svg('M3 1H4V4H1V3H3ZM6 1H7V3H9V4H6ZM1 6H4V9H3V7H1ZM6 6H9V7H7V9H6Z'),
    reglages: svg('M4 0H6V2H4ZM4 8H6V10H4ZM0 4H2V6H0ZM8 4H10V6H8ZM1 1H3V3H1ZM7 1H9V3H7ZM1 7H3V9H1ZM7 7H9V9H7ZM3 2H7V3H3ZM2 3H8V7H2ZM3 7H7V8H3ZM4 4V6H6V4Z'),
  };
  const COMMANDES = `<div class="commandes" id="commandes">
      <button class="btn ic" data-c="precedente" title="Précédente (ou début de la vidéo)">${IC.prec}</button>
      <button class="btn ic" data-c="basculer" id="cLire" title="Lecture / pause (Espace)">${IC.lire}</button>
      <button class="btn ic" data-c="suivante" title="Vidéo suivante">${IC.suiv}</button>
      <span class="temps" id="cTemps">0:00 / 0:00</span>
      <input type="range" id="cPos" min="0" max="0" step="1" value="0" title="Position (← →)">
      <button class="btn ic" data-c="muet" id="cMuet" title="Couper le son (M)">${IC.son}</button>
      <input type="range" id="cVol" min="0" max="100" step="1" value="100" title="Volume (↑ ↓)">
      <button class="btn ic" data-c="reglages" id="cReglages" title="Vitesse, qualité, sous-titres">${IC.reglages}</button>
      <button class="btn ic" data-c="pleinecran" id="cPlein" title="Plein écran (F, F11 ; Échap pour sortir)">${IC.plein}</button>
    </div>`;
  const QUALITES = { highres: '4320p', hd2880: '2880p', hd2160: '2160p', hd1440: '1440p', hd1080: '1080p', hd720: '720p', large: '480p', medium: '360p', small: '240p', tiny: '144p', auto: 'Auto' };
  const temps = s => { if (s == null || !isFinite(s)) return '0:00'; s = Math.floor(s); const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = String(s % 60).padStart(2, '0'); return h ? `${h}:${String(m).padStart(2, '0')}:${x}` : `${m}:${x}`; };
  let lecture = null, glisse = false, pleinEcran = false, barreVisible = true;
  const BARRE = 30;   // hauteur de nos commandes en plein écran (youtube.css : body.plein .commandes)
  const cmd = (action, valeur) => post('yt.cmd', valeur === undefined ? { action } : { action, valeur });
  // le menu de l'engrenage : vitesse, qualité, sous-titres (sous-menus, la valeur active cochée), d'après le dernier état du lecteur
  const nomVitesse = v => v === 1 ? 'Normale' : String(v).replace('.', ',') + '×';
  function menuReglages() {
    const s = lecture || {}, r = $('cReglages').getBoundingClientRect();
    const pistes = (s.pistes || []).map(p => [p.code, p.nom]);
    const piste = pistes.find(p => p[0] === s.piste);
    menu([
      ['Vitesse : ' + nomVitesse(s.vitesse || 1), null, { sub: (s.vitesses || [1]).map(v => [nomVitesse(v), () => cmd('vitesse', v), { check: v === (s.vitesse || 1) }]) }],
      ['Qualité : ' + (QUALITES[s.qualite] || s.qualite || 'Auto'), null, { sub: (s.qualites || ['auto']).map(q => [QUALITES[q] || q, () => cmd('qualite', q), { check: q === s.qualite }]), disabled: !(s.qualites || []).length }],
      ['Sous-titres : ' + (piste ? piste[1] : 'aucun'), null, { sub: [['Sans sous-titres', () => cmd('soustitres', ''), { check: !piste }], ...pistes.map(([c, n]) => [n, () => cmd('soustitres', c), { check: piste && piste[0] === c }])] }],
    ], r.left, r.bottom + 2, 'Lecture');
  }
  // la charte dessine la barre d'accent du curseur à la taille de background-size : chaque page la règle selon la valeur
  const remplir = r => { const mx = +r.max || 0, mn = +r.min || 0; r.style.backgroundSize = (mx > mn ? Math.round((r.value - mn) / (mx - mn) * 1000) / 10 : 0) + '% 100%'; };
  // Un écran de YouTube à la place de la vidéo (avertissement d'âge ou de contenu sensible, vidéo indisponible, connexion demandée) : le programme efface
  // la vue native, et le rectangle du lecteur montre le message de YouTube avec ses boutons ; un clic actionne le vrai bouton dans la page cachée.
  let blocageAffiche = '';
  function majBlocage(b) {
    const l = $('lecteur'); if (!l) return;
    const cle = b ? JSON.stringify(b) + etat.connecte : '';
    if (cle === blocageAffiche) return;
    blocageAffiche = cle;
    l.classList.toggle('bloque', !!b);
    if (!b) { l.innerHTML = '<span>Chargement du lecteur…</span>'; return; }
    // le détail répète parfois le titre, et finit par un lien « En savoir plus » (vers l'aide de Google) qu'on ne suit pas
    const detail = (b.detail || '').replace(/\s*(En savoir plus|Learn more)\.?\s*$/i, '').replace(b.raison ? new RegExp('^' + b.raison.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\.?\\s*') : /^$/, '');
    const lignes = b.raison ? [b.raison, detail].filter(Boolean) :(b.texte || '').split('\n').filter(x => x.trim() && !(b.boutons || []).includes(x.trim()));
    const titre = lignes.shift() || 'YouTube ne montre pas cette vidéo.';
    const boutons = (b.boutons || []).map((t, i) => `<button class="btn${i === 0 ? ' primary' : ''}" data-a="confirmer" data-i="${i}">${esc(t)}</button>`);
    if (b.connexion && !etat.connecte) boutons.push('<button class="btn" data-a="connexion">Se connecter</button>');
    l.innerHTML = `<div class="blocage"><div class="titre">${esc(titre)}</div>${lignes.length ? `<div class="detail">${esc(lignes.join('\n'))}</div>` : ''}`
      + (boutons.length ? `<div class="actions">${boutons.join('')}</div>` : '') + '</div>';
    statut(b.boutons && b.boutons.length ? 'Confirmation demandée par YouTube' : 'Vidéo non lue (voir le message)', 'amber');
  }

  function majCommandes(s) {
    lecture = s;
    if (cur.vue !== 'video' || !$('commandes')) return;
    majBlocage(s.blocage || null);
    $('cLire').innerHTML = s.pause === false ? IC.pause : IC.lire;
    if (!glisse) { $('cPos').max = s.duree || 0; $('cPos').value = s.t || 0; $('cTemps').textContent = `${temps(s.t)} / ${temps(s.duree)}`; remplir($('cPos')); }
    $('cPos').title = `Position (← →) · chargé : ${Math.round((s.charge || 0) * 100)} %`;
    $('cPos').style.setProperty('--charge', Math.round((s.charge || 0) * 100) + '%');
    $('cMuet').innerHTML = s.muet ? IC.muet : IC.son;
    if (document.activeElement !== $('cVol') && s.volume != null) { $('cVol').value = s.muet ? 0 : s.volume; remplir($('cVol')); }
    $('cReglages').title = `Vitesse ${nomVitesse(s.vitesse || 1)} · qualité ${QUALITES[s.qualite] || s.qualite || 'Auto'} · sous-titres ${s.piste && (s.pistes || []).some(p => p.code === s.piste) ? s.piste : 'aucun'}`;
    $('cPlein').innerHTML = pleinEcran ? IC.sortir : IC.plein;
    if (s.pub) statut('Publicité dans le lecteur : uBOL l\'a laissée passer (notée dans la veille)', 'amber');
  }
  $('vue').addEventListener('input', e => {
    if (e.target.id === 'cPos') { glisse = true; remplir(e.target); $('cTemps').textContent = `${temps(+e.target.value)} / ${temps(lecture && lecture.duree)}`; }
    else if (e.target.id === 'cVol') { remplir(e.target); cmd('volume', +e.target.value); }
  });
  $('vue').addEventListener('change', e => {
    if (e.target.id === 'cPos') { cmd('aller', +e.target.value); glisse = false; }
  });
  cdx.on('lecture', s => majCommandes(s));
  cdx.on('pleinecran', v => { pleinEcran = !!v; barreVisible = true; document.body.classList.toggle('plein', pleinEcran); document.body.classList.remove('sans-barre'); if (lecture) majCommandes(lecture); requestAnimationFrame(placerLecteur); });
  // en plein écran, le programme (qui voit la souris, même au-dessus de la vidéo) efface la barre après un moment sans mouvement, et la rappelle
  cdx.on('barre', v => { barreVisible = !!v; document.body.classList.toggle('sans-barre', !barreVisible); placerLecteur(); });

  // ---------- le lecteur : le rectangle réservé dans la fiche, envoyé au programme qui y pose la vue native ----------
  const observateur = new ResizeObserver(() => placerLecteur());
  function suivreLecteur() { observateur.disconnect(); const l = $('lecteur'); if (l) observateur.observe(l); placerLecteur(); }
  function placerLecteur() {
    const l = cur.vue === 'video' ? $('lecteur') : null;
    let m = { visible: false };
    if (l && pleinEcran) m = { visible: true, x: 0, y: 0, w: innerWidth, h: innerHeight - (barreVisible ? BARRE : 0) };   // plein écran : toute la fenêtre, moins nos commandes si elles sont là
    else if (l) { const r = l.getBoundingClientRect(); m = { visible: true, x: Math.round(r.left) + 1, y: Math.round(r.top) + 1, w: Math.round(r.width) - 2, h: Math.round(r.height) - 2 }; }
    const cle = JSON.stringify(m);
    if (cle === rectEnvoye) return;
    rectEnvoye = cle; post('yt.lecteur', m);
  }
  addEventListener('resize', placerLecteur);
  // un menu ou un dialogue de cette page passerait sous le lecteur (vue native) : il s'efface le temps qu'ils sont ouverts
  // (un menu qui ne passe pas sur la vidéo, celui de l'engrenage hors plein écran par exemple, la laisse affichée)
  let recouvert = false;
  const recouvrir = on => { if (on !== recouvert) { recouvert = on; post('win.overlay', { on }); } };
  function surLaVideo() {
    const v = JSON.parse(rectEnvoye || '{}'); if (!v.visible) return false;
    return [...document.querySelectorAll('.menu[data-cdx]')].some(m => { const r = m.getBoundingClientRect(); return r.left < v.x + v.w && r.right > v.x && r.top < v.y + v.h && r.bottom > v.y; });
  }
  function menu(items, x, y, titre) {
    CdxFrame.menu(items, x, y, titre); recouvrir(surLaVideo());
    const w = new MutationObserver(() => { if (!document.querySelector('.menu[data-cdx]')) { w.disconnect(); recouvrir(false); } else recouvrir(surLaVideo()); });   // les sous-menus aussi
    w.observe(document.body, { childList: true });
  }
  async function confirmer(o) { recouvrir(true); try { return await CdxFrame.dialog(o); } finally { recouvrir(false); } }

  // ---------- navigation de l'interface ----------
  function aller(vue) {
    if (cur.vue !== 'attente') pile.push(Object.assign({}, cur, { defil: $('zone').scrollTop }));
    if (pile.length > 50) pile.shift();
    cur = vue; rendre(); $('zone').scrollTop = 0;
  }
  function retour() {
    if (!pile.length) return;
    cur = pile.pop(); rendre(); $('zone').scrollTop = cur.defil || 0;
    // revenir à une fiche : son lecteur doit rejouer cette vidéo-là (la page cachée a pu changer de vidéo entre-temps)
    if (cur.vue === 'video') demander('yt.video', { id: cur.fiche.id, ...(cur.fiche.liste ? { liste: cur.fiche.liste.id } : {}) });
  }
  function demander(cmd, args) {
    occupe = true; plusEnCours = cmd === 'yt.plus';
    statut(cmd === 'yt.plus' ? 'Chargement de la suite…' : 'Demande envoyée…', 'amber');
    post(cmd, args);
  }

  // ---------- messages du programme ----------
  cdx.on('etat', v => { const avant = etat.pret; etat = v; if (cur.vue === 'attente' && !v.pret) rendre(); else majEtat(); if (v.pret && !avant && !occupe) statut(v.bloqueur ? 'Prêt' : 'Prêt, mais SANS bloqueur de publicités', v.bloqueur ? 'green' : 'amber'); });
  cdx.on('occupe', v => { occupe = true; statut(v, 'amber'); });
  cdx.on('erreur', v => { derniereErreur = v; occupe = false; plusEnCours = false; statut(v, 'red'); });
  cdx.on('liste', (v, m) => {
    occupe = false; recu++;
    if (m.ajout) {
      plusEnCours = false;
      if (cur.vue !== m.vue) return;   // la suite d'une liste qu'on a quittée
      const cle = x => x.genre === 'liste' || x.genre === 'mix' ? 'L:' + x.listeId : x.id;
      const deja = new Set(cur.videos.map(cle));
      cur.videos.push(...(m.videos || []).filter(x => !deja.has(cle(x))));
      cur.suite = m.suite; const d = $('zone').scrollTop; rendre(); $('zone').scrollTop = d;
      return;
    }
    const nouvelle = { vue: m.vue, requete: m.requete, videos: m.videos || [], suite: m.suite, messages: m.messages || [], chaine: m.chaine, entete: m.entete };
    if (m.reinit) { pile.length = 0; cur = nouvelle; rendre(); }   // le compte a changé : l'historique de l'ancien n'a plus de sens
    else if (cur.vue === 'attente' || (cur.vue === 'accueil' && m.vue === 'accueil' && !pile.length)) { cur = nouvelle; rendre(); }
    else if (m.vue === 'chaine' && cur.vue === 'chaine' && cur.chaine && m.chaine && cur.chaine.id === m.chaine.id) { cur = nouvelle; rendre(); $('zone').scrollTop = 0; }   // un autre onglet de la même chaîne
    else aller(nouvelle);
    statut(`${nouvelle.videos.length} éléments${m.pubs ? ` (${m.pubs} pub${m.pubs > 1 ? 's' : ''} retirée${m.pubs > 1 ? 's' : ''} des données)` : ''}`, 'green');
  });
  cdx.on('fiche', (f, m) => {
    if (m.spontanee) {
      // le lecteur a changé de vidéo de lui-même : la fiche suit, si on la regarde encore
      if (cur.vue !== 'video' || cur.fiche.id === f.id) return;
      aller({ vue: 'video', fiche: f }); statut('Vidéo suivante : ' + (f.titre || ''), 'green');
      return;
    }
    occupe = false; recu++;
    if (cur.vue === 'video' && cur.fiche.id === f.id) { cur.fiche = f; rendre(); }   // la même fiche (retour) : remplacée sur place
    else aller({ vue: 'video', fiche: f });
    statut(f.liste ? `Liste « ${f.liste.titre} » : ${f.liste.index + 1} / ${f.liste.total}` : `${(f.suggestions || []).length} suggestions`, 'green');
  });

  // ---------- gestes ----------
  $('nav').addEventListener('submit', e => { e.preventDefault(); const q = $('q').value.trim(); if (q) demander('yt.recherche', { q }); });
  $('bAccueil').onclick = () => demander('yt.accueil');
  $('bAbos').onclick = () => demander('yt.abonnements');
  $('bRetour').onclick = retour;
  $('bCompte').onclick = () => {
    if (!etat.pret) return;
    if (!etat.connecte) { post('yt.connexion'); return; }
    const r = $('bCompte').getBoundingClientRect();
    menu([
      [etat.compte || 'Compte Google connecté', null, { disabled: true }],
      null,
      ['Changer de compte…', async () => { if (await confirmer({ title: 'Changer de compte', text: 'Le compte actuel sera déconnecté de la page YouTube, puis la fenêtre de connexion Google s\'ouvrira pour l\'autre compte.', yes: 'Continuer', no: 'Annuler' })) demander('yt.changer'); }],
      ['Se déconnecter…', async () => { if (await confirmer({ title: 'Se déconnecter', text: 'Se déconnecter de ' + (etat.compte || 'ce compte') + ' ? La page YouTube cachée oubliera la session Google (ses cookies sont effacés) ; l\'accueil et les abonnements seront vides jusqu\'à la prochaine connexion.', yes: 'Se déconnecter', no: 'Annuler' })) demander('yt.deconnexion'); }],
    ], r.left, r.bottom + 2, 'Compte');
  };
  // un clic sur une vignette, une ligne, un nom de chaîne, un onglet
  function ouvrir(el) {
    const ch = el.closest('[data-chaine]'); if (ch) { demander('yt.chaine', { id: ch.dataset.chaine }); return; }
    const ol = el.closest('[data-ouvreliste]'); if (ol) { demander('yt.liste', { id: ol.dataset.ouvreliste }); return; }
    const c = el.closest('[data-genre]'); if (!c) return;
    const d = c.dataset;
    if (d.genre === 'liste') demander('yt.liste', { id: d.liste });
    else if (d.genre === 'mix') { if (d.id) demander('yt.video', { id: d.id, liste: d.liste }); }
    else if (d.id) demander('yt.video', { id: d.id, ...(d.liste ? { liste: d.liste } : {}), ...(d.index ? { index: +d.index } : {}) });
  }
  $('vue').addEventListener('click', e => {
    const a = e.target.closest('[data-a]');
    if (a) {
      if (a.dataset.a === 'plus' && !plusEnCours) { demander('yt.plus', { vue: cur.vue }); rendre(); }
      else if (a.dataset.a === 'connexion') post('yt.connexion');
      else if (a.dataset.a === 'confirmer') { a.disabled = true; statut('Confirmation envoyée à YouTube…', 'amber'); cmd('confirmer', +a.dataset.i); }
      else if (a.dataset.a === 'toutlire' && cur.entete) demander('yt.video', { id: cur.entete.premierId, liste: cur.entete.id, index: 0 });
      return;
    }
    const c = e.target.closest('[data-c]');
    if (c) { const a = c.dataset.c; if (a === 'muet') cmd('muet', !(lecture && lecture.muet)); else if (a === 'reglages') menuReglages(); else if (a === 'pleinecran') cmd('pleinecran'); else cmd(a); return; }
    const o = e.target.closest('[data-onglet]');
    if (o && cur.chaine) { if (!o.classList.contains('on')) demander('yt.chaine', { id: cur.chaine.id, url: o.dataset.onglet, params: o.dataset.params }); return; }
    ouvrir(e.target);
  });
  $('vue').addEventListener('keydown', e => { if (e.key === 'Enter') ouvrir(e.target); });
  // la suite se charge seule quand on arrive près du bas
  $('zone').addEventListener('scroll', () => {
    const z = $('zone');
    if (LISTES[cur.vue] && cur.suite && !plusEnCours && z.scrollTop + z.clientHeight > z.scrollHeight - 400) { demander('yt.plus', { vue: cur.vue }); rendre(); }
  });
  document.addEventListener('keydown', e => {
    // les raccourcis du lecteur, quand on regarde une vidéo et qu'on n'écrit pas
    const saisie = e.target.closest && e.target.closest('input[type=search], input[type=text], textarea, select');
    if (cur.vue === 'video' && !saisie && !e.altKey && !e.ctrlKey) {
      const k = e.key, t = { ' ': () => cmd('basculer'), k: () => cmd('basculer'), ArrowLeft: () => cmd('avancer', -5), ArrowRight: () => cmd('avancer', 5),
        j: () => cmd('avancer', -10), l: () => cmd('avancer', 10), ArrowUp: () => cmd('volume', Math.min(100, (lecture && lecture.volume || 0) + 5)),
        ArrowDown: () => cmd('volume', Math.max(0, (lecture && lecture.volume || 0) - 5)), m: () => cmd('muet', !(lecture && lecture.muet)),
        f: () => cmd('pleinecran'), F11: () => cmd('pleinecran'), Escape: () => { if (pleinEcran) cmd('pleinecran'); } }[k.length === 1 ? k.toLowerCase() === k ? k : k.toLowerCase() : k];
      if (t && !(e.target.type === 'range' && (k === 'ArrowLeft' || k === 'ArrowRight' || k === 'ArrowUp' || k === 'ArrowDown'))) { e.preventDefault(); t(); return; }
    }
    if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); retour(); }
    else if (e.altKey && e.key === 'Home') { e.preventDefault(); demander('yt.accueil'); }
    else if (e.ctrlKey && (e.key === 'l' || e.key === 'f' || e.key === 'k')) { e.preventDefault(); $('q').focus(); $('q').select(); }
  });
  document.addEventListener('mouseup', e => { if (e.button === 3) { e.preventDefault(); retour(); } });

  // menu « Réglages » (bouton hachuré) : le compte ; host.js y ajoute le thème de la suite
  windowSettings(() => etat.connecte ? [
    ['Changer de compte…', () => $('bCompte').click()],
    ['Se déconnecter…', () => $('bCompte').click()],
  ] : [
    ['Se connecter au compte Google…', () => post('yt.connexion'), { disabled: !etat.pret }],
  ]);

  // ---------- pour l'essai automatique (--essai) ----------
  window.__demander = m => { const { cmd, ...args } = m; derniereErreur = null; demander(cmd, args); };
  window.__etat = () => ({
    vue: cur.vue, occupe, recu, erreur: derniereErreur, n: (cur.videos || []).length, premier: cur.videos && cur.videos.length ? cur.videos[0].id : null,
    id: cur.fiche ? cur.fiche.id : null, titre: cur.fiche ? cur.fiche.titre : null, description: cur.fiche ? (cur.fiche.description || '').length : 0,
    suggestions: cur.fiche ? (cur.fiche.suggestions || []).length : 0, premiereSuggestion: cur.fiche && cur.fiche.suggestions && cur.fiche.suggestions.length ? cur.fiche.suggestions[0].id : null,
    chaineId: cur.fiche ? cur.fiche.chaineId : null,
    liste: cur.fiche && cur.fiche.liste ? { id: cur.fiche.liste.id, titre: cur.fiche.liste.titre, index: cur.fiche.liste.index, total: cur.fiche.liste.total, n: cur.fiche.liste.videos.length, deuxieme: cur.fiche.liste.videos[1] ? cur.fiche.liste.videos[1].id : null } : null,
    chaine: cur.chaine ? { id: cur.chaine.id, nom: cur.chaine.nom, onglets: (cur.chaine.onglets || []).map(o => ({ titre: o.titre, url: o.url, params: o.params, actif: o.actif })) } : null,
    entete: cur.entete || null,
    elements: (cur.videos || []).slice(0, 40).map(v => ({ id: v.id, genre: v.genre, listeId: v.listeId, index: v.index })),
    lecteur: JSON.parse(rectEnvoye || '{}'), messages: cur.messages || [], plein: pleinEcran, commandes: !!$('commandes'),
    affichage: $('commandes') ? { temps: $('cTemps').textContent, pos: +$('cPos').value, max: +$('cPos').max, vol: +$('cVol').value, boutons: $('commandes').querySelectorAll('button, select').length } : null,
    barre: barreVisible, recouvert, menu: [...document.querySelectorAll('.menu[data-cdx] .menu-item')].map(x => x.innerText.trim()), compte: etat.compte || null, connecte: etat.connecte,
    texteVue: $('vue').innerText.slice(0, 400),
    blocage: $('lecteur') && $('lecteur').classList.contains('bloque') ? { texte: $('lecteur').innerText.trim(), boutons: [...$('lecteur').querySelectorAll('button')].map(b => b.innerText.trim()) } : null,
  });

  fillPix();
  CdxFrame.init().edges();
  rendre();
  post('ready');
})();
