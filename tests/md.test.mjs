// Les essais du rendu Markdown de l'application Notes (web/notes/js/md.js). Aucune dépendance : node tests/md.test.mjs
import { createRequire } from 'node:module';
const Md = createRequire(import.meta.url)('../web/notes/js/md.js');
let bad = 0;
const check = (what, ok, extra = '') => { console.log((ok ? 'OK     ' : 'ÉCHEC  ') + what + (ok ? '' : '  ' + extra)); if (!ok) bad++; };
const notes = { Alpha: 'Alpha.md', Beta: 'Projets/Beta.md', 'Projets/Beta': 'Projets/Beta.md' };
const ctx = { resolve: t => notes[t] || null, imageUrl: (t) => (t === 'photo.png' || t === 'img/a.png') ? 'https://vault.kdx.notes/' + t : null };
const r = (t, c = ctx) => Md.render(t, c).html;
const has = (h, s) => h.includes(s);

// ---- blocs de base
check('titres h1 à h6 avec ancre et ligne source', has(r('# Un\n\n###### Six'), '<h1 id="h-un" data-line="0">Un</h1>') && has(r('# Un\n\n###### Six'), '<h6 id="h-six" data-line="2">Six</h6>'));
check('titre : l\'ancre ignore accents et ponctuation', has(r('## Été & Co !'), 'id="h-ete-co"'));
check('paragraphes : deux blocs ; un retour à la ligne devient <br>', r('a\nb\n\nc') === '<p>a<br>b</p>\n<p>c</p>');
check('séparateur ---, ***, _ _ _', (r('---').match(/<hr>/g) || []).length === 1 && has(r('***'), '<hr>') && has(r('_ _ _'), '<hr>'));
check('emphase : gras, italique, barré, surligné, gras+italique', r('**g** *i* __g2__ _i2_ ~~b~~ ==s== ***gi***') === '<p><strong>g</strong> <em>i</em> <strong>g2</strong> <em>i2</em> <del>b</del> <mark>s</mark> <strong><em>gi</em></strong></p>');
check('emphase : pas de mise en forme dans un mot (snake_case_mot, 2*3*4 reste intact aux espaces)', r('un_mot_snake et a * b * c') === '<p>un_mot_snake et a * b * c</p>');
check('code en ligne : le contenu est échappé et sans emphase', r('`<b>*x*</b>`') === '<p><code>&lt;b&gt;*x*&lt;/b&gt;</code></p>');
check('bloc de code clôturé : langue, contenu échappé, rien d\'interprété', r('```js\nconst a = "<x>"; // *non*\n```') === '<pre><code class="lang-js">const a = &quot;&lt;x&gt;&quot;; // *non*</code></pre>');
check('bloc de code : une clôture plus longue permet des ``` dedans', has(r('````\n```\ndedans\n```\n````'), '```\ndedans\n```'));
check('bloc de code jamais fermé : jusqu\'à la fin', has(r('```\nabc'), '<pre><code>abc</code></pre>'));
check('échappement : \\* \\_ \\# littéraux', r('\\*pas\\* \\#pas') === '<p>*pas* #pas</p>');
check('citation, avec emphase dedans', r('> une **citation**\n> suite') === '<blockquote><p>une <strong>citation</strong><br>suite</p></blockquote>');
check('encart (callout) avec titre et type', has(r('> [!warning] Attention\n> corps'), '<div class="callout warning"><div class="ct">Attention</div><p>corps</p></div>'));
check('encart sans titre : le type, en majuscule initiale', has(r('> [!note]\n> x'), '<div class="ct">Note</div>'));

// ---- listes
check('liste à puces et ordonnée (départ à 3)', r('- a\n- b') === '<ul><li>a</li><li>b</li></ul>' && r('3. a\n4. b') === '<ol start="3"><li>a</li><li>b</li></ol>');
check('liste imbriquée', r('- a\n  - b\n    - c\n- d') === '<ul><li>a\n<ul><li>b\n<ul><li>c</li></ul></li></ul></li><li>d</li></ul>');
check('liste : les * et + font aussi des puces ; changer de type ouvre une autre liste', (r('* a\n+ b').match(/<ul>/g) || []).length === 1 && (r('- a\n1. b').match(/<(ul|ol)/g) || []).length === 2);
const t = r('- [ ] ouverte\n- [x] faite\n  - [ ] imbriquée');
check('tâches : cases, état coché, numéro de la ligne source', has(t, '<input type="checkbox" class="task" data-line="0">') && has(t, 'data-line="1" checked') && has(t, 'data-line="2">') && has(t, 'task-item done'));
check('tâches : le numéro de ligne tient compte de l\'en-tête de propriétés', has(r('---\ntype: x\n---\n\n- [ ] a'), 'data-line="4"'));
check('élément de liste sur plusieurs paragraphes (liste « lâche »)', has(r('- a\n\n  b\n\n- c'), '<p>a</p>'));
check('code dans un élément de liste', has(r('- a\n  ```\n  x\n  ```'), '<pre><code>x</code></pre>'));
check('tabulations d\'indentation comprises comme des niveaux', has(r('- a\n\t- b'), '<ul><li>b</li></ul>'));

// ---- tableaux
const tb = r('| A | B | C |\n|:--|:-:|--:|\n| 1 | `x|y` | **3** |\n| 4 | 5 |');
check('tableau : en-têtes, alignements, code avec barre verticale, ligne courte', has(tb, '<th style="text-align:center">B</th>') && has(tb, '<th style="text-align:right">C</th>') && has(tb, '<code>x|y</code>') && has(tb, '<strong>3</strong>') && (tb.match(/<tr>/g) || []).length === 3);
check('tableau : une barre échappée \\| reste dans la cellule (alias de lien)', has(r('| a |\n|---|\n| [[Alpha\\|alias]] |'), 'alias</a>'));

// ---- liens, images, étiquettes
const w = r('[[Alpha]] [[Beta|le projet]] [[Alpha#Titre]] [[Inconnue]] ![[photo.png]] ![[absente.png]] ![[Alpha]]');
check('[[lien]] résolu : cible, chemin', has(w, '<a class="wl" data-target="Alpha" data-path="Alpha.md">Alpha</a>'));
check('[[lien|alias]] : le texte est l\'alias, le chemin est celui de la note', has(w, '<a class="wl" data-target="Beta" data-path="Projets/Beta.md">le projet</a>'));
check('[[lien#section]] : la section est gardée, le libellé la montre', has(w, 'data-heading="Titre">Alpha &gt; Titre</a>'));
check('[[lien]] sans note : marqué « miss » (à créer)', has(w, '<a class="wl miss" data-target="Inconnue">Inconnue</a>'));
check('![[image]] : l\'image est affichée depuis le coffre ; introuvable : un message', has(w, '<img class="emb" src="https://vault.kdx.notes/photo.png" alt="photo.png">') && has(w, 'image introuvable : absente.png'));
check('![[note]] : un lien d\'incrustation, pas de transclusion', has(w, 'class="wl emb-note"') && has(w, '↪ Alpha'));
check('lien Markdown vers une note du coffre : devient un lien interne', has(r('[ici](Alpha.md#Titre) et [là](Projets/Beta)'), 'data-path="Alpha.md" data-heading="Titre">ici</a>') && has(r('[là](Projets/Beta)'), 'data-path="Projets/Beta.md"'));
check('lien Markdown vers rien : le texte seul, marqué « miss »', r('[x](Nulle.md)') === '<p><span class="miss">x</span></p>');
check('image Markdown du coffre (chemin relatif encodé)', has(r('![une photo](img/a.png)'), '<img src="https://vault.kdx.notes/img/a.png" alt="une photo">'));
const tg = r('Du #projet et #a/b/c, pas #123 ni a#b ni http://x.org/#ancre ni `#code`.');
check('étiquettes : #projet et #a/b/c ; pas #123, ni a#b, ni l\'ancre d\'une adresse, ni le code', has(tg, 'data-tag="projet">#projet</a>') && has(tg, 'data-tag="a/b/c">#a/b/c</a>') && !has(tg, 'data-tag="123"') && !has(tg, 'data-tag="b"') && !has(tg, 'data-tag="ancre"') && !has(tg, 'data-tag="code"'));
check('étiquette avec accents', has(r('#idée'), 'data-tag="idée"'));
check('adresses : [texte](https://…), <https://…> et adresse nue deviennent des liens externes (sans href)', (r('[t](https://a.org/x?y=1&z=2) <https://b.org> et https://c.org/d.').match(/class="ext"/g) || []).length === 3 && !has(r('[t](https://a.org)'), 'href'));
check('adresse nue : la ponctuation finale n\'en fait pas partie', has(r('voir https://c.org/d.'), 'data-ext="https://c.org/d"') && has(r('voir https://c.org/d.'), '</a>.</p>'));

// ---- sécurité : rien du HTML ni d\'actif ne passe
const evil = '<script>alert(1)</script> <img src=x onerror=alert(1)> <iframe src="x"></iframe> <a href="javascript:alert(1)">x</a> <style>*{}</style>';
const eh = r(evil);
check('HTML brut : entièrement échappé (script, img, iframe, a, style)', !/<(script|img|iframe|a |style)/i.test(eh) && has(eh, '&lt;script&gt;') && has(eh, '&lt;img src=x onerror=alert(1)&gt;'));
check('[texte](javascript:…) : le texte seul, aucun lien', r('[clic](javascript:alert(1))') === '<p>clic</p>' && r('[clic](JaVaScRiPt:alert(1))') === '<p>clic</p>');
check('[texte](data:…), (file:…), (vbscript:…) : le texte seul', r('[a](data:text/html,<b>x</b>)') === '<p>a</p>' && r('[a](file:///C:/x)') === '<p>a</p>' && r('[a](vbscript:x)') === '<p>a</p>');
check('image distante : jamais chargée (aucun <img>), seulement signalée', !has(r('![x](https://evil.example/p.png)'), '<img') && has(r('![x](https://evil.example/p.png)'), 'image externe non chargée') && !has(r('![x](//evil.example/p.png)'), '<img'));
check('image Markdown avec un schéma data: ou javascript: : refusée', !has(r('![x](data:image/svg+xml,<svg onload=alert(1)>)'), '<img') && !has(r('![x](javascript:alert(1))'), '<img'));
check('attributs : guillemets et chevrons dans une cible ou un texte restent inertes', !/"\s+on\w+=/.test(r('[[a" onclick="x]] [[b"><script>x</script>]]')) && !has(r('[[b"><script>x</script>]]'), '<script'));
check('titre et texte de lien échappés', has(r('# <b>x</b>'), '&lt;b&gt;x&lt;/b&gt;') && has(r('[<i>a</i>](https://x.org)'), '&lt;i&gt;a&lt;/i&gt;'));
check('caractère nul dans le texte : retiré (il sert de repère interne)', !has(r('a\u00001\u0000b'), '\u0000') && has(r('a\u00001\u0000b'), 'a1b'));
check('un faux repère interne dans le texte n\'injecte rien', !has(r('\u00000\u0000 `<x>`'), '<x>'));
check('tableau : HTML des cellules échappé', has(r('| <b>x</b> |\n|---|\n| <i>y</i> |'), '&lt;b&gt;x&lt;/b&gt;') && !has(r('| <b>x</b> |\n|---|\n| <i>y</i> |'), '<b>'));
check('encart : un type qui n\'est pas un mot n\'ouvre pas d\'encart et ne sort pas d\'un attribut', !has(r('> [!x" onload="y] t\n> c'), 'class="callout') && !/<[^>]*onload/.test(r('> [!x" onload="y] t\n> c')));

// ---- en-tête, fins de ligne, entrées pathologiques
const fm = Md.render('---\ntype: projet\nstatut: "en pause"\ntags: [a, b]\nliste:\n  - un\n  - deux\n---\n# Corps');
check('en-tête : tableau de propriétés (type, statut entre guillemets, tags en liens, liste), corps rendu après', has(fm.html, '<table class="props">') && has(fm.html, '<th>statut</th><td>en pause</td>') && has(fm.html, 'data-tag="a"') && has(fm.html, '<td>un, deux</td>') && has(fm.html, '<h1 id="h-corps" data-line="8">Corps</h1>') && fm.props.length === 4);
check('en-tête jamais refermé : c\'est du texte ordinaire', !has(Md.render('---\ntype: x\nsuite').html, 'props'));
check('fins de ligne Windows : mêmes résultats', Md.render('# A\r\n\r\n- [ ] t\r\n').html === Md.render('# A\n\n- [ ] t\n').html);
check('texte vide, que des espaces : rien', r('') === '' && r('  \n \n') === '');
check('imbrication profonde de citations : sans plantage', (() => { try { return r('>'.repeat(60) + ' x').length > 0; } catch (e) { return false; } })());
check('une très longue ligne de caractères spéciaux : pas de blocage', (() => { const t0 = Date.now(); r('*'.repeat(20000) + '_'.repeat(20000) + '[['.repeat(5000)); return Date.now() - t0 < 3000; })(), 'trop lent');
check('un document de 4 000 lignes : moins de 2 s', (() => { const t0 = Date.now(); const d = Array.from({ length: 1000 }, (_, i) => `## Titre ${i}\n\nUn paragraphe avec **gras**, [[Alpha]] et #tag${i}.\n\n- [ ] tâche ${i}`).join('\n'); r(d); return Date.now() - t0 < 2000; })(), 'trop lent');

console.log(bad === 0 ? 'TOUT EST BON' : bad + ' vérification(s) en échec');
process.exit(bad === 0 ? 0 : 1);

