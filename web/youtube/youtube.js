// YouTube (cdx-youtube.exe) : la page de l'interface. Elle ne parle jamais à YouTube : elle demande au programme (cdx.send) et affiche ce qu'il renvoie, déjà converti
// par la couche de lecture (YtLecture.cs) : { id, titre, chaine, chaineId, duree, miniature, vues, date, genre (video|short|direct|liste|mix), listeId, index, vu }.
// Le lecteur : l'image vient de la page YouTube cachée (une vue native que le programme pose sur le rectangle .lecteur de la fiche, d'après le message
// « yt.lecteur »), dont toute l'interface est masquée ; les commandes sont les nôtres (la platine, en bas), qui pilotent l'API du lecteur de YouTube
// (« yt.cmd ») et suivent son état (« lecture », 4 fois par seconde). Tout ce qui recouvrirait la vue native (menus, dialogues) l'efface un instant (win.overlay).
// Mise en page (maquette du 03/11/2026) : accueil en rubans par moment, abonnements en règle du temps, recherche en liste (ou vignettes), vidéo en mode cinéma
// (colonne À suivre / Description et chapitres / Liste), la platine en bas : l'afficheur de la charte, et le transport pendant une vidéo.
//   demandes :  ready · yt.accueil · yt.abonnements · yt.recherche { q } · yt.video { id, liste, index } · yt.chaine { id, url, params } · yt.liste { id }
//               yt.plus { vue } · yt.connexion · yt.deconnexion · yt.changer · yt.lecteur { visible, x, y, w, h } · yt.cmd { action, valeur } (dont confirmer : un bouton de l'écran de YouTube)
//               (et celles de host.js : log, suite.accent, win.*)
//   réponses :  etat { pret, connecte, compte, bloqueur, extraction, veille } · liste { vue, requete, ajout, reinit, videos, suite, messages, pubs, chaine, entete }
//               fiche (spontanee : le lecteur est passé seul à une autre vidéo ; chapitres) · lecture (état du lecteur) · pleinecran · occupe · erreur · theme
(() => {
  'use strict';
  // host.js (chargé avant) : thème de l'adresse, journal des erreurs JS, menu « Réglages » avec le thème de la suite
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const post = (cmd, args) => cdx.send(cmd, args || {});
  const LISTES = { accueil: 'Accueil', abonnements: 'Abonnements', recherche: 'Recherche', chaine: 'Chaîne', liste: 'Liste' };
  // les onglets d'une chaîne qu'on sait afficher (repérés par la fin de leur adresse : leurs titres suivent la langue)
  const ONGLETS = ['featured', 'videos', 'shorts', 'streams', 'playlists', 'podcasts', 'shows'];
  const lire = k => { try { return localStorage.getItem(k); } catch { return null; } };
  const ecrire = (k, v) => { try { localStorage.setItem(k, v); } catch { } };

  // ---------- état ----------
  let etat = { pret: false, connecte: false };
  let cur = { vue: 'attente' };          // la vue affichée : { vue, requete, videos, suite, messages, fiche, chaine, entete, onglet }
  const pile = [];                       // les vues précédentes, pour « Retour » (avec leur position de défilement)
  let occupe = false, plusEnCours = false;
  let recu = 0, derniereErreur = null;   // pour l'essai automatique
  let rectEnvoye = '';                   // le dernier rectangle du lecteur envoyé au programme
  let vueRecherche = lire('yt.recherche.vue') === 'vignettes' ? 'vignettes' : 'liste';
  let sansCote = lire('yt.cote') === 'masquee';   // la colonne de droite de la fiche (À suivre, Description, Liste) masquée : la vidéo prend la place
  let mini = null;                       // le mini-lecteur : { fiche } de la vidéo qui continue pendant qu'on parcourt YouTube

  // ---------- l'afficheur (à gauche de la platine) ----------
  // Au repos, il dit l'état ; pendant une vidéo, il montre son temps et son titre, et un message le remplace quelques secondes.
  let minuteurMessage = 0;
  function statut(texte, couleur) {
    $('st1').textContent = texte;
    $('orb').className = 'orb' + (couleur ? ' ' + couleur : '') + (occupe ? ' pulse' : '');
    clearTimeout(minuteurMessage);
    if (cur.vue === 'video' || mini) minuteurMessage = setTimeout(titreAfficheur, couleur === 'red' ? 8000 : 4000);
  }
  function titreAfficheur() { const f = cur.vue === 'video' ? cur.fiche : mini && mini.fiche; if (f) { $('st1').textContent = f.titre || ''; if (!occupe) $('orb').className = 'orb green'; } }
  function majEtat() {
    const b = etat.bloqueur ? etat.bloqueur : 'SANS bloqueur';
    const qui = etat.connecte ? 'connecté : ' + (etat.compte ? etat.compte.replace(/ \(.*\)$/, '') : 'compte Google') : 'non connecté';
    $('st2').textContent = etat.pret ? `${qui} · ${b}${etat.veille ? ' · veille : ' + etat.veille : ''}` : '';
    $('afficheur').title = etat.pret ? `${etat.connecte ? 'Compte Google : ' + (etat.compte || 'connecté') : 'Pas de compte Google (menu de la barre de titre pour se connecter)'} · bloqueur : ${b} · extraction : ${etat.extraction}` : '';
    if (!occupe && !etat.pret) statut('Chargement de YouTube…', '');
    $('bRetour').disabled = pile.length === 0;
    $('bAccueil').classList.toggle('on', cur.vue === 'accueil');
    $('bAbos').classList.toggle('on', cur.vue === 'abonnements');
  }

  // ---------- les dates relatives de YouTube (« il y a 3 heures », « il y a 2 j », « 5 days ago ») ----------
  const UNITES = [[/^(s|sec|secondes?|seconds?)$/, 1 / 60], [/^(min|mins|minutes?)$/, 1], [/^(h|heures?|hours?|hrs?)$/, 60], [/^(j|jours?|days?|d)$/, 1440],
    [/^(sem|semaines?|weeks?|w)$/, 10080], [/^(mois|months?)$/, 43200], [/^(an|ans|années?|years?|y)$/, 525600]];
  function age(d) {   // { min (minutes écoulées), unite (minutes par unité) } ou null
    if (!d || /\bdans\b|\bin \d/i.test(d)) return null;   // une première à venir
    const m = /(\d+)\s*([a-zàâéèêîôûç]+)\.?/i.exec(d); if (!m) return null;
    const u = m[2].toLowerCase();
    for (const [re, k] of UNITES) if (re.test(u)) return { min: +m[1] * k, unite: k };
    return null;
  }
  const debutDuJour = t => { const d = new Date(t); d.setHours(0, 0, 0, 0); return d.getTime(); };
  const quand = a => new Date(Date.now() - a.min * 60000);
  const joursDepuis = a => Math.round((debutDuJour(Date.now()) - debutDuJour(quand(a))) / 864e5);
  const JOURS = ['dimanche', 'lundi', 'mardi', 'mercredi', 'jeudi', 'vendredi', 'samedi'];
  const MOIS = ['janvier', 'février', 'mars', 'avril', 'mai', 'juin', 'juillet', 'août', 'septembre', 'octobre', 'novembre', 'décembre'];
  const dateLongue = d => `${JOURS[d.getDay()]} ${d.getDate()} ${MOIS[d.getMonth()]}`;
  const maj1 = s => s ? s[0].toUpperCase() + s.slice(1) : s;

  // ---------- éléments communs ----------
  const badge = v => v.genre === 'direct' ? '<span class="duree direct">EN DIRECT</span>'
    : v.genre === 'liste' ? `<span class="duree liste">${esc(v.duree || 'Liste')}</span>` : v.genre === 'mix' ? '<span class="duree liste">Mix</span>'
    : v.duree ? `<span class="duree">${esc(v.duree)}</span>` : v.genre === 'short' ? '<span class="duree">Short</span>' : '';
  const vign = v => `<div class="vign${v.genre === 'liste' || v.genre === 'mix' ? ' pile' : ''}">${v.miniature ? `<img loading="lazy" referrerpolicy="no-referrer" alt="" src="${esc(v.miniature)}">` : ''}${badge(v)}${v.vu ? `<span class="vu" title="Déjà vue à ${v.vu} %"><i style="width:${Math.min(100, v.vu)}%"></i></span>` : ''}</div>`;
  const meta = v => v.genre === 'liste' ? 'Liste de lecture' : v.genre === 'mix' ? 'Mix : YouTube enchaîne des vidéos proches' : [v.vues, v.date].filter(Boolean).map(esc).join(' · ');
  const chaine = (nom, id) => nom ? (id ? `<span class="lien" data-chaine="${esc(id)}" title="Ouvrir la chaîne">${esc(nom)}</span>` : esc(nom)) : '';
  // les attributs d'un élément cliquable : une vidéo (et sa liste), une liste, un mix
  const cible = v => `data-id="${esc(v.id || '')}" data-genre="${esc(v.genre)}"${v.listeId ? ` data-liste="${esc(v.listeId)}"` : ''}${v.index != null ? ` data-index="${v.index}"` : ''}`;
  const carte = v => `<div class="carte" tabindex="0" ${cible(v)} title="${esc(v.titre)}">${vign(v)}<div class="t">${esc(v.titre)}</div><div class="c">${[chaine(v.chaine, v.chaineId), meta(v)].filter(Boolean).join(' · ')}</div></div>`;
  const boutonPlus = () => cur.suite ? `<div class="plus"><button class="btn" data-a="plus">${plusEnCours ? 'Chargement…' : 'Plus'}</button></div>` : '';

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

  // ACCUEIL : des rubans par moment (en direct, aujourd'hui, hier, cette semaine…, puis les Shorts et les listes)
  const MOMENTS = [['direct', 'En direct'], ['j0', 'Aujourd\'hui'], ['j1', 'Hier'], ['sem', 'Cette semaine'], ['mois', 'Ce mois-ci'], ['vieux', 'Plus ancien'],
    ['autres', 'Autres vidéos'], ['shorts', 'Shorts'], ['listes', 'Listes et mix']];
  function moment(v) {
    if (v.genre === 'direct') return 'direct';
    if (v.genre === 'short') return 'shorts';
    if (v.genre === 'liste' || v.genre === 'mix') return 'listes';
    const a = age(v.date); if (!a) return 'autres';
    const j = joursDepuis(a);
    return j <= 0 ? 'j0' : j === 1 ? 'j1' : j < 7 ? 'sem' : j < 31 ? 'mois' : 'vieux';
  }
  function rubans(videos) {
    const g = new Map(MOMENTS.map(([k]) => [k, []]));
    for (const v of videos) g.get(moment(v)).push(v);
    return MOMENTS.filter(([k]) => g.get(k).length).map(([k, titre]) => {
      const l = g.get(k), n = l.length;
      return `<div class="ruban${k === 'shorts' ? ' shorts' : ''}" data-r="${k}"><div class="ruban-tete"><span>${titre}</span><span class="n">${n} ${k === 'listes' ? 'élément' : 'vidéo'}${n > 1 ? 's' : ''}</span>
          <span class="fl"><button class="btn" data-a="ruban" data-d="-1" title="Précédentes">◀</button><button class="btn" data-a="ruban" data-d="1" title="Suivantes">▶</button></span></div>
        <div class="ruban-corps">${l.map(carte).join('')}</div></div>`;
    }).join('');
  }
  function majFleches() {
    for (const r of document.querySelectorAll('.ruban')) {
      const c = r.querySelector('.ruban-corps'), [p, s] = r.querySelectorAll('.fl .btn');
      p.disabled = c.scrollLeft <= 1; s.disabled = c.scrollLeft + c.clientWidth >= c.scrollWidth - 2;
    }
  }

  // ABONNEMENTS : la règle du temps — les sorties à leur heure (vers 14 h), groupées par jour ; « pas encore vue » sans barre de progression
  function regle(videos) {
    const jours = [], parCle = new Map();
    for (const v of videos) {
      const a = age(v.date);
      let cle, titre, n = '';
      if (!a) { cle = 'autres'; titre = 'Sans date'; }
      else if (a.unite >= 10080) { cle = 'g:' + v.date; titre = maj1(v.date); }   // « il y a 2 semaines » : YouTube n'en dit pas plus
      else {
        const j = joursDepuis(a), d = quand(a);
        cle = 'j' + j; titre = j <= 0 ? 'Aujourd\'hui' : j === 1 ? 'Hier' : maj1(JOURS[d.getDay()]); n = dateLongue(d);
      }
      if (!parCle.has(cle)) { const x = { titre, n, l: [] }; parCle.set(cle, x); jours.push(x); }
      parCle.get(cle).l.push([v, a]);
    }
    const heure = a => {
      if (!a || a.unite > 60) return '';
      if (a.min < 60) return a.min < 2 ? 'à l\'instant' : `il y a ${Math.round(a.min)} min`;
      const d = quand(a); return `vers ${d.getMinutes() >= 30 ? (d.getHours() + 1) % 24 : d.getHours()} h`;
    };
    const sortie = ([v, a]) => {
      const neuve = etat.connecte && !v.vu && v.genre !== 'liste' && v.genre !== 'mix';
      return `<div class="sortie${neuve ? ' neuve' : ''}" tabindex="0" ${cible(v)} title="${esc(v.titre)}"><span class="h">${heure(a)}</span><span class="fil"></span>${vign(v)}
        <div class="tx"><span class="t">${esc(v.titre)}</span><span class="c">${chaine(v.chaine, v.chaineId)}</span><span class="m">${[v.duree && v.genre === 'video' ? esc(v.duree) : '', meta(v)].filter(Boolean).join(' · ')}${neuve ? ' · <span class="neuf">pas encore vue</span>' : ''}</span></div></div>`;
    };
    return `<div class="regle">${jours.map(j => `<div class="jour"><span>${esc(j.titre)}</span><span class="n">${[j.n, j.l.length + ' sortie' + (j.l.length > 1 ? 's' : '')].filter(Boolean).join(' · ')}</span></div>${j.l.map(sortie).join('')}`).join('')}</div>`;
  }

  // RECHERCHE : la liste à colonnes de la charte (Titre, Chaîne, Durée, Publiée, Vues), ou les vignettes
  function listeRecherche(videos) {
    const duree = v => v.genre === 'direct' ? 'en direct' : v.genre === 'liste' ? esc(v.duree || 'liste') : v.genre === 'mix' ? 'mix' : v.genre === 'short' ? 'short' : esc(v.duree || '');
    const ligne = v => `<div class="ligne" tabindex="0" ${cible(v)} title="${esc(v.titre)}">${vign(v)}<span>${esc(v.titre)}</span><span class="f">${chaine(v.chaine, v.chaineId)}</span><span>${duree(v)}</span><span class="f">${esc(v.date || '')}</span><span class="f">${esc(v.vues || '')}</span></div>`;
    return `<div class="lhead"><span class="lh"></span><span class="lh">Titre</span><span class="lh">Chaîne</span><span class="lh">Durée</span><span class="lh">Publiée</span><span class="lh">Vues</span></div>
      <div class="list" id="rlist">${videos.map(ligne).join('')}${boutonPlus()}</div>`;
  }

  function rendre() {
    const z = $('vue');
    // ce qu'on garde d'un rendu au suivant : la place de chaque ruban, le défilement de la liste de recherche
    const places = new Map([...document.querySelectorAll('.ruban')].map(r => [r.dataset.r, r.querySelector('.ruban-corps').scrollLeft]));
    const defilListe = $('rlist') ? $('rlist').scrollTop : 0;
    const enListe = cur.vue === 'recherche' && vueRecherche === 'liste' && cur.videos && cur.videos.length;
    $('zone').classList.toggle('fige', cur.vue === 'video');
    $('zone').classList.toggle('rl', !!enListe);
    majPlatine();
    if (pleinEcran && cur.vue !== 'video') cmd('pleinecran');   // on quitte la fiche : on quitte aussi le plein écran
    majEtat();
    if (cur.vue === 'attente') { z.innerHTML = '<div class="vide">Chargement de YouTube dans la page cachée…</div>'; placerLecteur(); return; }
    if (cur.vue === 'video') { rendreFiche(z, cur.fiche); return; }
    if (document.activeElement !== $('q')) $('q').value = cur.vue === 'recherche' ? cur.requete : '';
    let haut = '', titre;
    if (cur.vue === 'chaine') { haut = enteteChaine(cur.chaine || {}); titre = (cur.chaine && cur.chaine.nom) || 'Chaîne'; }
    else if (cur.vue === 'liste') { haut = enteteListe(cur.entete || {}); titre = (cur.entete && cur.entete.titre) || 'Liste'; }
    else titre = cur.vue === 'recherche' ? `Résultats pour « ${cur.requete} »` : LISTES[cur.vue];
    const n = cur.videos.length;
    let corps;
    if (!n) {
      const msg = (cur.messages || []).filter(Boolean).map(esc).join('<br>');
      corps = (cur.vue === 'accueil' || cur.vue === 'abonnements') && !etat.connecte
        ? `<div class="vide">Sans compte, ${cur.vue === 'accueil' ? "l'accueil de YouTube est vide" : "il n'y a pas d'abonnements"}.${msg ? '<br>' + msg : ''}<br><button class="btn" data-a="connexion">Se connecter</button></div>`
        : `<div class="vide">Rien à afficher ici.${msg ? '<br>' + msg : ''}</div>`;
    }
    if (cur.vue === 'recherche') {
      const bascule = `<span class="droite"><button class="btn${vueRecherche === 'vignettes' ? ' on' : ''}" data-a="vuerech" data-v="vignettes">Vignettes</button><button class="btn${vueRecherche === 'liste' ? ' on' : ''}" data-a="vuerech" data-v="liste">Liste</button></span>`;
      const tete = `<div class="titre-page"><span>${esc(titre)}</span><span class="n">${n} élément${n > 1 ? 's' : ''}</span>${n ? bascule : ''}</div>`;
      z.innerHTML = enListe ? `<div class="rliste">${tete}${listeRecherche(cur.videos)}</div>` : `<div class="page">${tete}${corps || `<div class="grille">${cur.videos.map(carte).join('')}</div>${boutonPlus()}`}</div>`;
    }
    else if (cur.vue === 'accueil') z.innerHTML = `<div class="page">${corps || rubans(cur.videos) + boutonPlus()}</div>`;
    else if (cur.vue === 'abonnements') z.innerHTML = `<div class="page">${corps || regle(cur.videos) + boutonPlus()}</div>`;
    else z.innerHTML = `<div class="page">${haut}${corps || `<div class="grille">${cur.videos.map(carte).join('')}</div>${boutonPlus()}`}</div>`;
    for (const r of document.querySelectorAll('.ruban')) if (places.has(r.dataset.r)) r.querySelector('.ruban-corps').scrollLeft = places.get(r.dataset.r);
    if ($('rlist')) { CdxColumns.init({ head: document.querySelector('.rliste .lhead'), cols: [88, '1fr', 210, 84, 130, 120], key: 'youtube-recherche' }); $('rlist').scrollTop = defilListe; }
    majFleches();
    CdxFrame.title(cur.vue === 'accueil' ? 'YouTube' : 'YouTube — ' + titre);
    placerLecteur();
  }
  // la platine : le transport pendant une vidéo, sur sa fiche ou dans le mini-lecteur ; le mini-lecteur hors de la fiche
  function majPlatine() {
    const enLecture = cur.vue === 'video' || !!mini, montrerMini = !!mini && cur.vue !== 'video' && cur.vue !== 'attente';
    document.body.classList.toggle('lecture', enLecture);
    document.body.classList.toggle('avec-mini', montrerMini);
    $('commandes').hidden = !enLecture;
    $('mini').hidden = !montrerMini;
    if (montrerMini) { $('miniTitre').textContent = mini.fiche.titre || ''; $('miniTitre').title = 'Revenir à la vidéo : ' + (mini.fiche.titre || ''); }
    if (!enLecture) { clearTimeout(minuteurMessage); $('cTemps').textContent = ''; }
    else if (!occupe) titreAfficheur();
  }
  // quitter la fiche d'une vidéo qui est à l'écran : elle continue dans le mini-lecteur (le programme ouvre celui de YouTube)
  function quitterFiche(vers) {
    if (cur.vue === 'video' && vers && vers.vue !== 'video' && !mini && JSON.parse(rectEnvoye || '{}').visible && !(lecture && lecture.blocage)) mini = { fiche: cur.fiche };
  }
  function revenirVideo() { if (!mini) return; const f = mini.fiche; mini = null; aller({ vue: 'video', fiche: f }); }
  function fermerMini() { if (!mini) return; mini = null; majPlatine(); placerLecteur(); statut('Mini-lecteur fermé', ''); }

  // ---------- VIDÉO : le mode cinéma ----------
  const secondes = s => s.split(':').reduce((a, x) => a * 60 + +x, 0);
  // Les chapitres : ceux de la barre de lecture de YouTube (fiche.chapitres), sinon les horodatages de la description (« 0:00 Intro », au moins deux, le premier à 0:00).
  function chapitresDe(f) {
    if (f.chapitres && f.chapitres.length > 1) return f.chapitres.map(c => ({ titre: c.titre, debut: c.debut }));
    const res = [];
    for (const l of (f.description || '').split('\n')) {
      const m = /^\s*[[(]?((?:\d{1,2}:)?\d{1,2}:\d{2})[\])]?\s*[-–—:|.]?\s*(.+?)\s*$/.exec(l);
      if (m) res.push({ debut: secondes(m[1]), titre: m[2] });
    }
    return res.length >= 2 && res[0].debut === 0 ? res : [];
  }
  function rendreFiche(z, f) {
    const l = f.liste;
    cur.onglet = cur.onglet || (l ? 'liste' : 'suite');
    const sug = f.suggestions || [];
    const qui = [f.avatar ? `<img class="av" alt="" referrerpolicy="no-referrer" src="${esc(f.avatar)}">` : '',
      f.chaine ? `<span class="nom${f.chaineId ? ' lien' : ''}"${f.chaineId ? ` data-chaine="${esc(f.chaineId)}" title="Ouvrir la chaîne"` : ''}>${esc(f.chaine)}</span>` : '',
      f.abonnes ? `<span>${esc(f.abonnes)}</span>` : '', `<span class="m">${[f.vues, f.date].filter(Boolean).map(esc).join(' · ')}</span>`,
      f.chaineId ? `<button class="btn" data-chaine="${esc(f.chaineId)}">Chaîne</button>` : '',
      `<button class="btn" data-a="cote" id="bCote"></button>`].filter(Boolean).join('');
    z.innerHTML = `<div class="cinema">
      <div class="scene">
        <div class="lecteur" id="lecteur"><span>Chargement du lecteur…</span></div>
        <div class="cartouche"><span class="t" title="${esc(f.titre || '')}">${esc(f.titre || '(sans titre)')}</span><span class="qui">${qui}</span></div>
      </div>
      <div class="cote">
        <div class="tabs" id="ongletsFiche"><button class="tab" data-ofiche="suite">À suivre (${sug.length})</button><button class="tab" data-ofiche="desc">Description</button>${l ? `<button class="tab" data-ofiche="liste">Liste (${l.index + 1} / ${l.total || l.videos.length})</button>` : ''}</div>
        <div class="volet" id="volet"></div>
      </div>
    </div>`;
    const suivante = l ? l.videos[l.index + 1] : sug[0];
    $('cEnsuite').textContent = suivante ? 'ensuite : ' + suivante.titre : '';
    $('cEnsuite').title = suivante ? 'Ensuite : ' + suivante.titre : '';
    majCote();
    rendreVolet();
    CdxFrame.title('YouTube — ' + (f.titre || ''));
    if (!occupe) titreAfficheur();
    blocageAffiche = '';   // un nouveau rectangle du lecteur : le message éventuel sera remis par le prochain état du lecteur
    if (lecture && lecture.blocage && lecture.id === f.id) majBlocage(lecture.blocage);
    suivreLecteur();
  }
  // la colonne de droite : À suivre (les suggestions, numérotées), Description (chapitres et texte), Liste (la liste en cours)
  function rendreVolet() {
    const f = cur.fiche, l = f.liste, o = cur.onglet === 'liste' && !l ? 'suite' : cur.onglet;
    for (const b of document.querySelectorAll('#ongletsFiche .tab')) b.classList.toggle('on', b.dataset.ofiche === o);
    const pas = (v, k, on, ensuite) => `<div class="pas${on ? ' on' : ''}${ensuite && o === 'suite' ? ' ensuite' : ''}" tabindex="0" ${cible(v)} title="${esc(v.titre)}"><span class="num">${String(k + 1).padStart(2, '0')}</span>${vign(v)}
        <div class="tx"><div class="t">${esc(v.titre)}</div><div class="c">${ensuite ? '<span class="lab">ensuite</span> · ' : ''}${chaine(v.chaine, v.chaineId) || meta(v)}</div></div></div>`;
    let h;
    if (o === 'desc') {
      const ch = chapitresDe(f);
      h = (ch.length ? `<div class="chaps">${ch.map(c => `<div class="chap" data-t="${c.debut}" title="Aller à ${temps(c.debut)}"><span class="h">${temps(c.debut)}</span><span>${esc(c.titre)}</span></div>`).join('')}</div>` : '')
        + `<div class="desc">${esc(f.description || 'Pas de description.')}</div>`;
    }
    else if (o === 'liste') h = `<div class="bobine"><div class="sec">Liste : <span class="lien" data-ouvreliste="${esc(l.id)}" title="Ouvrir la liste">${esc(l.titre || '')}</span>${l.auteur ? ' · ' + chaine(l.auteur, l.auteurId) : ''}</div>
        <div id="encours">${l.videos.map((v, k) => pas(v, k, v.id === f.id, k === l.index + 1)).join('')}</div></div>`;
    else h = (f.suggestions || []).length ? `<div class="bobine">${f.suggestions.map((v, k) => pas(v, k, false, k === 0 && !l)).join('')}</div>` : '<div class="vide">Aucune suggestion.</div>';
    $('volet').innerHTML = h;
    const on = document.querySelector('#encours .pas.on'); if (on) on.scrollIntoView({ block: 'nearest' });
    if (lecture) majChapitre(lecture.t);
  }
  function majCote() {
    document.body.classList.toggle('sans-cote', sansCote);
    const b = $('bCote'); if (!b) return;
    b.textContent = sansCote ? 'Afficher la colonne' : 'Masquer la colonne';
    b.title = sansCote ? 'Afficher À suivre, la description et la liste à droite de la vidéo' : 'Masquer la colonne de droite : la vidéo prend la place';
  }
  function majChapitre(t) {
    const ch = [...document.querySelectorAll('.chap')]; if (!ch.length) return;
    let i = -1; ch.forEach((c, k) => { if (+c.dataset.t <= (t || 0) + 0.5) i = k; });
    ch.forEach((c, k) => c.classList.toggle('on', k === i));
  }

  // ---------- nos commandes du lecteur (la platine) ----------
  const svg = d => `<svg viewBox="0 0 10 10" width="10" height="10" shape-rendering="crispEdges"><path d="${d}" fill="currentColor"/></svg>`;
  const IC = {
    lire: svg('M2 1H3V9H2ZM3 2H4V8H3ZM4 3H5V7H4ZM5 3H6V7H5ZM6 4H7V6H6ZM7 4H8V6H7Z'),
    pause: svg('M2 1H4V9H2ZM6 1H8V9H6Z'),
    prec: svg('M1 1H3V9H1ZM3 5H4V5ZM4 4H5V6H4ZM5 3H6V7H5ZM6 2H7V8H6ZM7 1H9V9H7Z'),
    agrandir: svg('M1 1H9V2H1ZM1 2H2V9H1ZM8 2H9V9H8ZM2 8H8V9H2ZM5 3H7V5H6V4H5ZM3 5H4V6H5V7H3Z'),
    fermer: svg('M1 1H3V2H1ZM2 2H4V3H2ZM3 3H7V4H3ZM4 4H6V6H4ZM3 6H7V7H3ZM2 7H4V8H2ZM1 8H3V9H1ZM7 1H9V2H7ZM6 2H8V3H6ZM6 7H8V8H6ZM7 8H9V9H7Z'),
    suiv: svg('M7 1H9V9H7ZM1 1H3V9H1ZM3 2H4V8H3ZM4 3H5V7H4ZM5 4H6V6H5Z'),
    son: svg('M1 3H3V7H1ZM3 2H4V8H3ZM4 1H5V9H4ZM6 3H7V7H6ZM8 1H9V9H8Z'),
    muet: svg('M1 3H3V7H1ZM3 2H4V8H3ZM4 1H5V9H4ZM6 3H7V4H6ZM7 4H8V6H7ZM8 3H9V4H8ZM6 6H7V7H6ZM8 6H9V7H8Z'),
    plein: svg('M1 1H4V2H2V4H1ZM6 1H9V4H8V2H6ZM1 6H2V8H4V9H1ZM8 6H9V9H6V8H8Z'),
    sortir: svg('M3 1H4V4H1V3H3ZM6 1H7V3H9V4H6ZM1 6H4V9H3V7H1ZM6 6H9V7H7V9H6Z'),
    reglages: svg('M4 0H6V2H4ZM4 8H6V10H4ZM0 4H2V6H0ZM8 4H10V6H8ZM1 1H3V3H1ZM7 1H9V3H7ZM1 7H3V9H1ZM7 7H9V9H7ZM3 2H7V3H3ZM2 3H8V7H2ZM3 7H7V8H3ZM4 4V6H6V4Z'),
  };
  $('cPrec').innerHTML = IC.prec; $('cLire').innerHTML = IC.lire; $('cSuiv').innerHTML = IC.suiv; $('cMuet').innerHTML = IC.son; $('cReglages').innerHTML = IC.reglages; $('cPlein').innerHTML = IC.plein;
  $('miniRevenir').innerHTML = IC.agrandir; $('miniFermer').innerHTML = IC.fermer;
  const QUALITES = { highres: '4320p', hd2880: '2880p', hd2160: '2160p', hd1440: '1440p', hd1080: '1080p', hd720: '720p', large: '480p', medium: '360p', small: '240p', tiny: '144p', auto: 'Auto' };
  const temps = s => { if (s == null || !isFinite(s)) return '0:00'; s = Math.floor(s); const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = String(s % 60).padStart(2, '0'); return h ? `${h}:${String(m).padStart(2, '0')}:${x}` : `${m}:${x}`; };
  const texteTemps = (t, d, pause) => `${pause ? '' : '▶ '}${temps(t)} / ${temps(d)}${pause ? ' · pause' : ''}`;
  let lecture = null, glisse = false, pleinEcran = false, barreVisible = true;
  const cmd = (action, valeur) => post('yt.cmd', valeur === undefined ? { action } : { action, valeur });
  // le menu de l'engrenage : vitesse, qualité, sous-titres (sous-menus, la valeur active cochée), d'après le dernier état du lecteur
  const nomVitesse = v => v === 1 ? 'Normale' : String(v).replace('.', ',') + '×';
  // en plein écran, la page n'est qu'une bande de la hauteur de la platine : elle demande d'abord toute la fenêtre (la vidéo s'efface le temps du menu)
  const pageEntiere = () => new Promise(res => {
    if (innerHeight > 100) { res(); return; }
    const fin = () => { removeEventListener('resize', vu); clearTimeout(t); res(); }, vu = () => { if (innerHeight > 100) fin(); }, t = setTimeout(fin, 800);
    addEventListener('resize', vu);
  });
  async function menuReglages() {
    if (pleinEcran) { recouvrir(true); await pageEntiere(); }
    const s = lecture || {}, r = $('cReglages').getBoundingClientRect();
    const pistes = (s.pistes || []).map(p => [p.code, p.nom]);
    const piste = pistes.find(p => p[0] === s.piste);
    const items = [
      ['Vitesse : ' + nomVitesse(s.vitesse || 1), null, { sub: (s.vitesses || [1]).map(v => [nomVitesse(v), () => cmd('vitesse', v), { check: v === (s.vitesse || 1) }]) }],
      ['Qualité : ' + (QUALITES[s.qualite] || s.qualite || 'Auto'), null, { sub: (s.qualites || ['auto']).map(q => [QUALITES[q] || q, () => cmd('qualite', q), { check: q === s.qualite }]), disabled: !(s.qualites || []).length }],
      ['Sous-titres : ' + (piste ? piste[1] : 'aucun'), null, { sub: [['Sans sous-titres', () => cmd('soustitres', ''), { check: !piste }], ...pistes.map(([c, n]) => [n, () => cmd('soustitres', c), { check: piste && piste[0] === c }])] }],
    ];
    menu(items, r.left, r.top, 'Lecture');
    // la platine est en bas : le menu s'ouvre au-dessus du bouton, calé sur son bord droit (sur la colonne de droite, pas sur la vidéo)
    const m = document.querySelector('.menu[data-cdx]');
    if (m) { const t = m.getBoundingClientRect(); m.style.left = Math.max(2, r.right - t.width) + 'px'; m.style.top = Math.max(2, r.top - t.height - 2) + 'px'; recouvrir(surLaVideo()); }
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
    const lignes = b.raison ? [b.raison, detail].filter(Boolean) : (b.texte || '').split('\n').filter(x => x.trim() && !(b.boutons || []).includes(x.trim()));
    const titre = lignes.shift() || 'YouTube ne montre pas cette vidéo.';
    const boutons = (b.boutons || []).map((t, i) => `<button class="btn${i === 0 ? ' primary' : ''}" data-a="confirmer" data-i="${i}">${esc(t)}</button>`);
    if (b.connexion && !etat.connecte) boutons.push('<button class="btn" data-a="connexion">Se connecter</button>');
    l.innerHTML = `<div class="blocage"><div class="titre">${esc(titre)}</div>${lignes.length ? `<div class="detail">${esc(lignes.join('\n'))}</div>` : ''}`
      + (boutons.length ? `<div class="actions">${boutons.join('')}</div>` : '') + '</div>';
    statut(b.boutons && b.boutons.length ? 'Confirmation demandée par YouTube' : 'Vidéo non lue (voir le message)', 'amber');
  }

  function majCommandes(s) {
    lecture = s;
    if (cur.vue !== 'video' && !mini) return;
    if (cur.vue === 'video') majBlocage(s.blocage || null);
    $('cLire').innerHTML = s.pause === false ? IC.pause : IC.lire;
    if (!glisse) { $('cPos').max = s.duree || 0; $('cPos').value = s.t || 0; $('cTemps').textContent = texteTemps(s.t, s.duree, s.pause !== false); remplir($('cPos')); }
    $('cPos').title = `Position (← →) · chargé : ${Math.round((s.charge || 0) * 100)} %`;
    $('cPos').style.setProperty('--charge', Math.round((s.charge || 0) * 100) + '%');
    $('cMuet').innerHTML = s.muet ? IC.muet : IC.son;
    if (document.activeElement !== $('cVol') && s.volume != null) { $('cVol').value = s.muet ? 0 : s.volume; remplir($('cVol')); }
    $('cReglages').title = `Vitesse ${nomVitesse(s.vitesse || 1)} · qualité ${QUALITES[s.qualite] || s.qualite || 'Auto'} · sous-titres ${s.piste && (s.pistes || []).some(p => p.code === s.piste) ? s.piste : 'aucun'}`;
    $('cPlein').innerHTML = pleinEcran ? IC.sortir : IC.plein;
    majChapitre(s.t);
    if (s.pub) statut('Publicité dans le lecteur : uBOL l\'a laissée passer (notée dans la veille)', 'amber');
  }
  $('platine').addEventListener('input', e => {
    if (e.target.id === 'cPos') { glisse = true; remplir(e.target); $('cTemps').textContent = texteTemps(+e.target.value, lecture && lecture.duree, true); }
    else if (e.target.id === 'cVol') { remplir(e.target); cmd('volume', +e.target.value); }
  });
  $('platine').addEventListener('change', e => {
    if (e.target.id === 'cPos') { cmd('aller', +e.target.value); glisse = false; }
  });
  $('platine').addEventListener('click', e => {
    const c = e.target.closest('[data-c]');
    if (c) {
      const a = c.dataset.c;
      if (a === 'muet') cmd('muet', !(lecture && lecture.muet)); else if (a === 'reglages') menuReglages();
      else if (a === 'pleinecran') { if (mini && cur.vue !== 'video') { revenirVideo(); setTimeout(() => cmd('pleinecran'), 500); } else cmd('pleinecran'); }
      else cmd(a);
      return;
    }
    if (e.target.closest('#afficheur') && mini && cur.vue !== 'video') revenirVideo();   // l'afficheur montre la vidéo du mini-lecteur : un clic y ramène
  });
  $('miniTitre').onclick = revenirVideo; $('miniRevenir').onclick = revenirVideo; $('miniFermer').onclick = fermerMini;
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
    if (!l && mini && !$('mini').hidden) { const r = $('miniEcran').getBoundingClientRect(); m = { visible: true, mini: true, x: Math.round(r.left) + 1, y: Math.round(r.top) + 1, w: Math.round(r.width) - 2, h: Math.round(r.height) - 2 }; }
    if (l && pleinEcran) m = { visible: true, plein: true };   // plein écran : la vidéo prend toute la fenêtre, la page n'est plus qu'une bande devant elle (le programme la dispose)
    else if (l) { const r = l.getBoundingClientRect(); m = { visible: true, x: Math.round(r.left) + 1, y: Math.round(r.top) + 1, w: Math.round(r.width) - 2, h: Math.round(r.height) - 2 }; }
    const cle = JSON.stringify(m);
    if (cle === rectEnvoye) return;
    rectEnvoye = cle; post('yt.lecteur', m);
  }
  addEventListener('resize', () => { placerLecteur(); majFleches(); });
  // un menu ou un dialogue de cette page passerait sous le lecteur (vue native) : il s'efface le temps qu'ils sont ouverts
  // (un menu qui ne passe pas sur la vidéo, celui de l'engrenage hors plein écran par exemple, la laisse affichée)
  let recouvert = false;
  const recouvrir = on => { if (on !== recouvert) { recouvert = on; post('win.overlay', { on }); } };
  function surLaVideo() {
    const v = JSON.parse(rectEnvoye || '{}'); if (!v.visible) return false;
    if (v.plein) return true;   // en plein écran, la page n'est qu'une bande : tout menu demande la fenêtre entière
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
    quitterFiche(vue);
    if (cur.vue !== 'attente') pile.push(Object.assign({}, cur, { defil: $('zone').scrollTop, defilListe: $('rlist') ? $('rlist').scrollTop : 0 }));
    if (pile.length > 50) pile.shift();
    cur = vue; rendre(); $('zone').scrollTop = 0;
  }
  function retour() {
    if (!pile.length) return;
    quitterFiche(pile[pile.length - 1]);
    cur = pile.pop();
    // revenir à la fiche de la vidéo du mini-lecteur : elle reprend sa place, sans être rechargée
    const memeVideo = cur.vue === 'video' && mini && mini.fiche.id === cur.fiche.id;
    if (cur.vue === 'video') mini = null;
    rendre(); $('zone').scrollTop = cur.defil || 0; if ($('rlist')) $('rlist').scrollTop = cur.defilListe || 0;
    // revenir à une autre fiche : son lecteur doit rejouer cette vidéo-là (la page cachée a pu changer de vidéo entre-temps)
    if (cur.vue === 'video' && !memeVideo) demander('yt.video', { id: cur.fiche.id, ...(cur.fiche.liste ? { liste: cur.fiche.liste.id } : {}) });
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
  // le programme a ouvert (ou n'a pas pu ouvrir) le mini-lecteur de YouTube avant de quitter la page de la vidéo
  cdx.on('mini', v => {
    if (v && !mini && cur.vue === 'video' && cur.fiche) mini = { fiche: cur.fiche };
    else if (!v) mini = null;
    majPlatine(); placerLecteur();
  });
  cdx.on('fiche', (f, m) => {
    if (m.spontanee && mini && cur.vue !== 'video') { mini.fiche = f; majPlatine(); return; }   // le mini-lecteur est passé à la vidéo suivante
    if (m.spontanee) {
      // le lecteur a changé de vidéo de lui-même : la fiche suit, si on la regarde encore
      if (cur.vue !== 'video' || cur.fiche.id === f.id) return;
      aller({ vue: 'video', fiche: f, onglet: cur.onglet }); statut('Vidéo suivante : ' + (f.titre || ''), 'green');
      return;
    }
    occupe = false; recu++;
    mini = null;   // une vidéo ouverte : le mini-lecteur s'est refermé (le programme l'a fait avant de l'ouvrir)
    if (cur.vue === 'video' && cur.fiche.id === f.id) { cur.fiche = f; rendre(); }   // la même fiche (retour) : remplacée sur place
    else aller({ vue: 'video', fiche: f });
    statut(f.liste ? `Liste « ${f.liste.titre} » : ${f.liste.index + 1} / ${f.liste.total}` : `${(f.suggestions || []).length} suggestions`, 'green');
  });

  // ---------- gestes ----------
  $('nav').addEventListener('submit', e => { e.preventDefault(); const q = $('q').value.trim(); if (q) demander('yt.recherche', { q }); });
  $('bAccueil').onclick = () => demander('yt.accueil');
  $('bAbos').onclick = () => demander('yt.abonnements');
  $('bRetour').onclick = retour;
  // le compte Google : dans le menu de la barre de titre
  const changerCompte = async () => { if (await confirmer({ title: 'Changer de compte', text: 'Le compte actuel sera déconnecté de la page YouTube, puis la fenêtre de connexion Google s\'ouvrira pour l\'autre compte.', yes: 'Continuer', no: 'Annuler' })) demander('yt.changer'); };
  const deconnecter = async () => { if (await confirmer({ title: 'Se déconnecter', text: 'Se déconnecter de ' + (etat.compte || 'ce compte') + ' ? La page YouTube cachée oubliera la session Google (ses cookies sont effacés) ; l\'accueil et les abonnements seront vides jusqu\'à la prochaine connexion.', yes: 'Se déconnecter', no: 'Annuler' })) demander('yt.deconnexion'); };
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
      const k = a.dataset.a;
      if (k === 'plus' && !plusEnCours) { demander('yt.plus', { vue: cur.vue }); rendre(); }
      else if (k === 'connexion') post('yt.connexion');
      else if (k === 'confirmer') { a.disabled = true; statut('Confirmation envoyée à YouTube…', 'amber'); cmd('confirmer', +a.dataset.i); }
      else if (k === 'toutlire' && cur.entete) demander('yt.video', { id: cur.entete.premierId, liste: cur.entete.id, index: 0 });
      else if (k === 'vuerech') { vueRecherche = a.dataset.v; ecrire('yt.recherche.vue', vueRecherche); rendre(); $('zone').scrollTop = 0; }
      else if (k === 'cote') { sansCote = !sansCote; ecrire('yt.cote', sansCote ? 'masquee' : ''); majCote(); requestAnimationFrame(placerLecteur); }
      else if (k === 'ruban') { const c = a.closest('.ruban').querySelector('.ruban-corps'); c.scrollLeft += +a.dataset.d * (c.clientWidth + 18); setTimeout(majFleches, 450); }
      return;
    }
    const of = e.target.closest('[data-ofiche]'); if (of) { cur.onglet = of.dataset.ofiche; rendreVolet(); return; }
    const chp = e.target.closest('.chap[data-t]'); if (chp) { cmd('aller', +chp.dataset.t); return; }
    const o = e.target.closest('[data-onglet]');
    if (o && cur.chaine) { if (!o.classList.contains('on')) demander('yt.chaine', { id: cur.chaine.id, url: o.dataset.onglet, params: o.dataset.params }); return; }
    ouvrir(e.target);
  });
  $('vue').addEventListener('keydown', e => { if (e.key === 'Enter') ouvrir(e.target); });
  // la suite se charge seule quand on arrive près du bas (de la zone, ou de la liste de recherche qui défile seule)
  function presDuBas(el) {
    if (LISTES[cur.vue] && cur.suite && !plusEnCours && el.scrollTop + el.clientHeight > el.scrollHeight - 400) { demander('yt.plus', { vue: cur.vue }); rendre(); }
  }
  $('zone').addEventListener('scroll', () => presDuBas($('zone')));
  $('vue').addEventListener('scroll', e => { if (e.target.id === 'rlist') presDuBas(e.target); }, true);
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
  windowSettings(() => !etat.pret ? [['YouTube se charge…', null, { disabled: true }]] : etat.connecte ? [
    [etat.compte || 'Compte Google connecté', null, { disabled: true }],
    null,
    ['Changer de compte…', changerCompte],
    ['Se déconnecter…', deconnecter],
  ] : [
    ['Se connecter au compte Google…', () => post('yt.connexion')],
  ]);

  // ---------- pour l'essai automatique (--essai) ----------
  window.__demander = m => { const { cmd, ...args } = m; derniereErreur = null; demander(cmd, args); };
  window.__etat = () => ({
    vue: cur.vue, occupe, recu, erreur: derniereErreur, n: (cur.videos || []).length, premier: cur.videos && cur.videos.length ? cur.videos[0].id : null,
    id: cur.fiche ? cur.fiche.id : null, titre: cur.fiche ? cur.fiche.titre : null, description: cur.fiche ? (cur.fiche.description || '').length : 0,
    suggestions: cur.fiche ? (cur.fiche.suggestions || []).length : 0, premiereSuggestion: cur.fiche && cur.fiche.suggestions && cur.fiche.suggestions.length ? cur.fiche.suggestions[0].id : null,
    chaineId: cur.fiche ? cur.fiche.chaineId : null, chapitres: cur.fiche ? chapitresDe(cur.fiche).length : 0,
    liste: cur.fiche && cur.fiche.liste ? { id: cur.fiche.liste.id, titre: cur.fiche.liste.titre, index: cur.fiche.liste.index, total: cur.fiche.liste.total, n: cur.fiche.liste.videos.length, deuxieme: cur.fiche.liste.videos[1] ? cur.fiche.liste.videos[1].id : null } : null,
    chaine: cur.chaine ? { id: cur.chaine.id, nom: cur.chaine.nom, onglets: (cur.chaine.onglets || []).map(o => ({ titre: o.titre, url: o.url, params: o.params, actif: o.actif })) } : null,
    entete: cur.entete || null,
    elements: (cur.videos || []).slice(0, 40).map(v => ({ id: v.id, genre: v.genre, listeId: v.listeId, index: v.index })),
    lecteur: JSON.parse(rectEnvoye || '{}'), messages: cur.messages || [], plein: pleinEcran, commandes: !$('commandes').hidden, mini: !$('mini').hidden, miniId: mini ? mini.fiche.id : null,
    affichage: !$('commandes').hidden ? { temps: $('cTemps').textContent, pos: +$('cPos').value, max: +$('cPos').max, vol: +$('cVol').value, boutons: $('commandes').querySelectorAll('button, select').length } : null,
    barre: barreVisible, recouvert, menu: [...document.querySelectorAll('.menu[data-cdx] .menu-item')].map(x => x.innerText.trim()), compte: etat.compte || null, connecte: etat.connecte,
    texteVue: $('vue').innerText.slice(0, 400),
    rubans: [...document.querySelectorAll('.ruban')].map(r => r.dataset.r), jours: document.querySelectorAll('.jour').length, lignes: document.querySelectorAll('#rlist .ligne').length,
    vus: (cur.videos || []).filter(v => v.vu).length,
    blocage: $('lecteur') && $('lecteur').classList.contains('bloque') ? { texte: $('lecteur').innerText.trim(), boutons: [...$('lecteur').querySelectorAll('button')].map(b => b.innerText.trim()) } : null,
  });

  fillPix();
  CdxFrame.init().edges();
  rendre();
  post('ready');
})();
