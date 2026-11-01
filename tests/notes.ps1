# Essai isolé de « Notes » dans sa vraie fenêtre : un coffre jetable sous %TEMP%, la page pilotée par le canal de test, des événements fabriqués (jamais de vraie souris ni de frappe sur
# ta session). Même le serveur MCP est lancé ici, contre le même coffre, pour voir ce que fait l'application quand Claude écrit.
#   pwsh -File tests\notes.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-nt-suite-$id"; $data = Join-Path $env:TEMP "cdx-nt-data-$id"; $vault = Join-Path $env:TEMP "cdx-nt-coffre-$id"
New-Item -ItemType Directory $suite, $data, $vault -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxnt$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data; $env:CDX_NOTES_DIR = $vault
$env:CDX_NO_FOCUS = '1'   # la fenêtre d'essai ne prend pas le focus : la frappe de celui qui travaille à côté n'y tombe pas
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}notes.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const') { $code = "(() => { $code })()" }
  $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function BusSend([string]$app, [string]$type, [string]$from, $dataObj) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "$prefix$app", [System.IO.Pipes.PipeDirection]::Out)
  try { $c.Connect(3000) } catch { return $false }
  $b = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ type = $type; from = $from; data = $dataObj } | ConvertTo-Json -Compress -Depth 6) + "`n"); $c.Write($b, 0, $b.Length); $c.Flush(); $c.Dispose(); $true
}
function W([string]$rel, [string]$text) { $f = Join-Path $vault $rel; New-Item -ItemType Directory (Split-Path $f) -Force | Out-Null; [IO.File]::WriteAllText($f, $text, (New-Object Text.UTF8Encoding($false))) }
function ReadV([string]$rel) {
  for ($i = 0; $i -lt 8; $i++) { try { return [IO.File]::ReadAllText((Join-Path $vault $rel), [Text.Encoding]::UTF8) } catch [System.IO.IOException] { Start-Sleep -Milliseconds 60 } }   # (l'application peut être en train d'écrire)
  return $null
}   # (« R » est un alias de PowerShell)
function Has([string]$s, [string]$sub) { if ($null -eq $s) { return $false }; $s.Contains($sub) }   # (-like prendrait [[…]] pour des jokers)
function Open-Notes { Start-Process "$Bin\cdx-notes.exe" -PassThru }
# édite le texte de l'éditeur comme le ferait la frappe (événement « input »)
function Type-Text([string]$text) { $j = $text | ConvertTo-Json -Compress; Js "const e = document.getElementById('ed'); e.value = $j; e.dispatchEvent(new Event('input', { bubbles: true }))" | Out-Null }
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==')

$started = @(); $mcp = $null
try {
  W 'Alpha.md' "---`ntype: projet`nstatut: actif`ntags: [exemple]`n---`n# Alpha`n`nVoir [[Beta]] et [[Inconnue]] et #vrai.`n`n- [ ] première`n- [x] faite`n"
  W 'Beta.md' "# Beta`nRetour vers [[Alpha]] et [[Projets/Gamma|le projet]].`n"
  W 'Projets/Gamma.md' "# Gamma`nTexte avec **gras** et ![[logo.png]].`n"
  W 'Liens.md' "# Liens`n[[Alpha]] et [[Projets/Gamma|le projet]] et [[Beta#Titre]].`n"
  W 'Crlf.md' "# Crlf`r`nligne un`r`n"
  W 'Mal.md' "# Mal`n<script>window.__pwn = 1</script> <img src=x onerror=`"window.__pwn = 2`"> [clic](javascript:window.__pwn=3) ![x](https://evil.example/p.png) [[Alpha]]`n"
  [IO.File]::WriteAllBytes("$vault\logo.png", $png)
  New-Item -ItemType Directory "$vault\.obsidian" -Force | Out-Null; [IO.File]::WriteAllBytes("$vault\.obsidian\cache.png", $png)

  $app = Open-Notes; $started += $app
  Check "la fenêtre s'ouvre, le coffre est lu : 6 notes dans l'index" (Until { (Js "notes.length") -eq '6' } 30000)
  Check "le titre porte le nom du coffre" ((Js "document.getElementById('ttl').textContent") -like 'Notes — cdx-nt-coffre-*')
  Check "l'arborescence montre la racine, le dossier Projets (déplié, avec sa note) et les cinq notes de la racine" ((Js "document.querySelectorAll('#tree .root').length") -eq '1' -and ((Js "[...document.querySelectorAll('#tree .fld')].map(x => x.dataset.f).join(',')") -eq 'Projets') -and ((Js "document.querySelectorAll('#tree .nt').length") -eq '6'))
  Check "aucune note ouverte au départ : message d'accueil et éditeur désactivé" (((Js "document.getElementById('pv').textContent") -like '*Récemment modifiées*') -and ((Js "document.getElementById('ed').disabled") -eq 'true'))

  # ---------- ouvrir, lire, aperçu ----------
  Js "openNote('Alpha.md')" | Out-Null
  Check "ouvrir une note : onglet, texte dans l'éditeur, titre dans l'aperçu" ((Until { (Js "cur") -eq 'Alpha.md' } 8000) -and (Has (Js "document.getElementById('ed').value") 'Voir [[Beta]]') -and ((Js "document.querySelector('#pv h1').textContent") -eq 'Alpha'))
  Check "l'en-tête de propriétés est rendu en tableau (type, statut), l'étiquette est un lien" ((Js "document.querySelector('#pv table.props').textContent") -like '*projet*actif*' -and ((Js "document.querySelectorAll('#pv a.tg').length") -ge 1))
  Check "[[Beta]] est un lien résolu, [[Inconnue]] un lien à créer" ((Js "document.querySelector('#pv a.wl[data-target=""Beta""]').dataset.path") -eq 'Beta.md' -and ((Js "document.querySelectorAll('#pv a.wl.miss').length") -eq '1'))
  Check "le bouton de mode « Double » est enfoncé : éditeur et aperçu côte à côte" ((Js "document.getElementById('pane').className") -like '*mode-split*')
  Js "document.querySelector('#pv a.wl[data-target=""Beta""]').click()" | Out-Null
  Check "cliquer un [[lien]] de l'aperçu ouvre la note dans un nouvel onglet" ((Until { (Js "cur") -eq 'Beta.md' } 8000) -and ((Js "tabs.length") -eq '2') -and ((Js "document.querySelectorAll('#tabs .tab').length") -eq '2'))
  Js "document.querySelector('#pv a.wl[data-target=""Alpha""]').click()" | Out-Null
  Check "un lien vers une note déjà ouverte active son onglet (pas de doublon)" ((Until { (Js "cur") -eq 'Alpha.md' } 5000) -and ((Js "tabs.length") -eq '2'))

  # ---------- éditer, enregistrer ----------
  Type-Text "---`ntype: projet`nstatut: actif`ntags: [exemple]`n---`n# Alpha`n`nVoir [[Beta]] et [[Inconnue]] et #vrai.`n`n- [ ] première`n- [x] faite`n`nAjouté à la main.`n"
  Check "taper dans l'éditeur : l'onglet est marqué non enregistré (●) et l'aperçu suit" ((Until { (Js "document.querySelectorAll('#tabs .tab.on .dirty').length") -eq '1' } 3000) -and (Until { (Js "document.getElementById('pv').textContent") -like '*Ajouté à la main*' } 3000))
  Check "enregistrement automatique : le fichier est écrit sur le disque (moins de 5 s)" (Until { (ReadV 'Alpha.md') -like '*Ajouté à la main*' } 6000)
  Check "l'en-tête de la note n'a pas été touché par la réécriture (propriétés relues telles quelles)" ((ReadV 'Alpha.md') -like "---`ntype: projet*")
  Check "après l'enregistrement : plus de marque ●, état « Enregistré »" ((Until { (Js "document.querySelectorAll('#tabs .dirty').length") -eq '0' } 4000) -and ((Js "document.getElementById('st1').textContent") -eq 'Enregistré'))
  Js "openNote('Crlf.md')" | Out-Null; Until { (Js "cur") -eq 'Crlf.md' } 8000 | Out-Null
  Type-Text "# Crlf`nligne un`nligne deux`n"
  Check "une note aux fins de ligne Windows les garde à l'enregistrement" (Until { (ReadV 'Crlf.md') -eq "# Crlf`r`nligne un`r`nligne deux`r`n" } 6000)

  # ---------- cases à cocher de l'aperçu ----------
  Js "openNote('Alpha.md')" | Out-Null; Until { (Js "cur") -eq 'Alpha.md' } 5000 | Out-Null
  Js "document.querySelector('#pv input.task').click()" | Out-Null
  Check "cocher une tâche dans l'aperçu change la ligne du texte, puis le fichier" ((Until { Has (ReadV 'Alpha.md') '- [x] première' } 6000) -and (Has (Js "document.getElementById('ed').value") '- [x] première'))

  # ---------- aides d'écriture ----------
  Js "const e = document.getElementById('ed'); e.value = '- un'; e.focus(); e.setSelectionRange(4, 4); e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))" | Out-Null
  Check "Entrée dans une liste : l'élément suivant est préparé" ((Until { (Js "document.getElementById('ed').value") -eq "- un`n- " } 3000))
  Js "const e = document.getElementById('ed'); e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))" | Out-Null
  Check "Entrée sur un élément vide : on sort de la liste" ((Until { (Js "document.getElementById('ed').value") -eq "- un`n" } 3000))
  Js "const e = document.getElementById('ed'); e.value = 'mot'; e.focus(); e.setSelectionRange(0, 3); e.dispatchEvent(new KeyboardEvent('keydown', { key: 'b', ctrlKey: true, bubbles: true, cancelable: true }))" | Out-Null
  Check "Ctrl+B met la sélection en gras" ((Until { (Js "document.getElementById('ed').value") -eq '**mot**' } 3000))
  function Key([string]$key, [string]$code = '', [bool]$shift = $false) { Js "const e = document.getElementById('ed'); e.dispatchEvent(new KeyboardEvent('keydown', { key: '$key', code: '$code', ctrlKey: true, shiftKey: $($shift.ToString().ToLower()), bubbles: true, cancelable: true }))" | Out-Null }
  function EdIs([string]$v) { Until { (Js "document.getElementById('ed').value") -eq $v } 3000 }
  Js "const e = document.getElementById('ed'); e.value = 'Titre'; e.focus(); e.setSelectionRange(2, 2)" | Out-Null
  Key '&' 'Digit1'; Check "Ctrl+1 (touche physique : « & » sur un clavier AZERTY) fait un titre de niveau 1" (EdIs '# Titre')
  Key 'é' 'Digit2'; Check "Ctrl+2 change le niveau du titre" (EdIs '## Titre')
  Key 'é' 'Digit2'; Check "le même raccourci une seconde fois retire le titre" (EdIs 'Titre')
  Js "const e = document.getElementById('ed'); e.value = 'un\ndeux'; e.setSelectionRange(0, 7)" | Out-Null
  Key 'l'; Check "Ctrl+L : une puce sur chaque ligne de la sélection" (EdIs "- un`n- deux")
  Key 'L' '' $true; Check "Ctrl+Maj+L : la liste devient numérotée" (EdIs "1. un`n2. deux")
  Key 'l'; Key 'l'; Check "Ctrl+L deux fois : puces, puis plus de liste" (EdIs "un`ndeux")
  Js "const e = document.getElementById('ed'); e.value = 'faire'; e.setSelectionRange(5, 5)" | Out-Null
  Key 'Enter'; Check "Ctrl+Entrée ajoute une case à cocher" (EdIs '- [ ] faire')
  Key 'Enter'; Check "Ctrl+Entrée sur une case la coche" (EdIs '- [x] faire')
  Key 'Enter'; Check "et la décoche" (EdIs '- [ ] faire')
  Key 'Q' '' $true; Check "Ctrl+Maj+Q : citation, à la place du préfixe précédent" (EdIs '> faire')
  Key ')' 'Digit0'; Check "Ctrl+0 : paragraphe sans préfixe" (EdIs 'faire')
  Js "const e = document.getElementById('ed'); e.value = 'mot'; e.setSelectionRange(0, 3)" | Out-Null
  Key 'X' '' $true; Check "Ctrl+Maj+X barre la sélection" (EdIs '~~mot~~')
  Check "le menu « Mise en forme » reprend les seize actions avec leurs raccourcis" (((Js "formatMenu().filter(Boolean).length") -eq 16) -and ((Js "formatMenu()[0][2].key") -eq 'Ctrl+B'))
  Type-Text (ReadV 'Alpha.md').Replace("`r`n", "`n")
  Start-Sleep -Milliseconds 1500

  # ---------- ce qui change sur le disque ----------
  Js "openNote('Beta.md')" | Out-Null; Until { (Js "cur") -eq 'Beta.md' } 5000 | Out-Null
  W 'Beta.md' "# Beta`nRetour vers [[Alpha]]. MODIFIÉE AILLEURS`n"
  Check "une note ouverte et propre, modifiée sur le disque (par Claude, un autre éditeur) : l'éditeur se met à jour tout seul" (Until { (Js "document.getElementById('ed').value") -like '*MODIFIÉE AILLEURS*' } 8000)
  Type-Text "# Beta`nMA VERSION non enregistrée`n"
  Start-Sleep -Milliseconds 100
  W 'Beta.md' "# Beta`nVERSION DU DISQUE 2`n"
  Check "modifiée sur le disque pendant que j'écris : un bandeau propose de garder ma version ou de recharger" ((Until { (Js "document.getElementById('notice').hidden") -eq 'false' } 8000) -and ((Js "document.querySelectorAll('#notice .btn').length") -eq '2'))
  Js "document.querySelector('#notice .btn[data-i=""0""]').click()" | Out-Null
  Check "« Garder ma version » : mon texte remplace celui du disque" (Until { (ReadV 'Beta.md') -like '*MA VERSION non enregistrée*' } 8000)
  Type-Text "# Beta`nENCORE MA VERSION`n"
  Start-Sleep -Milliseconds 80
  W 'Beta.md' "# Beta`nDISQUE 3 gagne`n"
  Until { (Js "document.getElementById('notice').hidden") -eq 'false' } 8000 | Out-Null
  Js "document.querySelector('#notice .btn[data-i=""1""]').click()" | Out-Null
  Check "« Recharger » : le texte du disque remplace le mien, plus de bandeau" ((Until { (Js "document.getElementById('ed').value") -like '*DISQUE 3 gagne*' } 6000) -and ((Js "document.getElementById('notice').hidden") -eq 'true'))
  Js "openNote('Projets/Gamma.md')" | Out-Null; Until { (Js "cur") -eq 'Projets/Gamma.md' } 8000 | Out-Null
  Remove-Item "$vault\Projets\Gamma.md"
  Check "une note ouverte et propre supprimée sur le disque : son onglet se ferme" (Until { (Js "docs.has('Projets/Gamma.md')") -eq 'false' } 8000)
  W 'Projets/Gamma.md' "# Gamma`nTexte avec **gras** et ![[logo.png]].`n"
  Check "une note ajoutée sur le disque apparaît dans l'arborescence sans rien faire" ((W 'Nouvelle venue.md' "# Venue`n") -eq $null -and (Until { (Js "notes.some(n => n.p === 'Nouvelle venue.md')") -eq 'true' } 8000) -and ((Js "!!document.querySelector('#tree .nt[data-p=""Nouvelle venue.md""]')") -eq 'true'))

  # ---------- créer, renommer, ranger, retirer ----------
  Js "post({ cmd: 'n.create', path: 'Idées/Piste.md', text: '# Piste\n\nVoir [[Alpha]].\n' })" | Out-Null
  Check "créer une note dans un dossier neuf : fichier, dossier dans l'arbre, onglet ouvert" ((Until { Test-Path "$vault\Idées\Piste.md" } 6000) -and (Until { (Js "cur") -eq 'Idées/Piste.md' } 6000) -and ((Js "folders.includes('Idées')") -eq 'true'))
  Js "post({ cmd: 'n.move', path: 'Alpha.md', to: 'Renommée.md' })" | Out-Null
  Check "renommer une note : le fichier change de nom, les liens des autres notes sont réécrits, l'onglet suit" ((Until { Test-Path "$vault\Renommée.md" } 6000) -and (Has (ReadV 'Liens.md') '[[Renommée]] et') -and (Has (ReadV 'Idées/Piste.md') '[[Renommée]]') -and (Until { (Js "docs.has('Renommée.md') && !docs.has('Alpha.md')") -eq 'true' } 6000))
  Js "const dt = new DataTransfer(); const n = document.querySelector('#tree .nt[data-p=""Idées/Piste.md""]'); n.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt })); const f = document.querySelector('#tree .fld[data-f=""Projets""]'); f.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt })); f.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt })); n.dispatchEvent(new DragEvent('dragend', { bubbles: true }))" | Out-Null
  Check "glisser une note sur un dossier de l'arbre : elle y est déplacée sur le disque" ((Until { Test-Path "$vault\Projets\Piste.md" } 6000) -and -not (Test-Path "$vault\Idées\Piste.md"))
  Js "post({ cmd: 'n.movefolder', path: 'Projets', to: 'Chantiers' })" | Out-Null
  Check "renommer un dossier : toutes ses notes suivent, l'onglet de la note ouverte aussi" ((Until { Test-Path "$vault\Chantiers\Piste.md" } 6000) -and (Test-Path "$vault\Chantiers\Gamma.md") -and -not (Test-Path "$vault\Projets") -and (Until { (Js "docs.has('Chantiers/Piste.md')") -eq 'true' } 6000))
  Check "après le renommage du dossier, [[Projets/Gamma|le projet]] est réécrit" (Has (ReadV 'Liens.md') '[[Chantiers/Gamma|le projet]]')
  Js "post({ cmd: 'n.trash', path: 'Nouvelle venue.md' })" | Out-Null
  Check "retirer une note : elle va dans .trash, n'est plus dans l'index" ((Until { Test-Path "$vault\.trash\Nouvelle venue.md" } 6000) -and -not (Test-Path "$vault\Nouvelle venue.md") -and (Until { (Js "notes.some(n => n.p === 'Nouvelle venue.md')") -eq 'false' } 6000))
  Js "post({ cmd: 'n.create', path: '../dehors.md', text: 'x' })" | Out-Null
  Check "un chemin qui sort du coffre est refusé, avec un message, et rien n'est écrit dehors" ((Until { (Js "document.getElementById('st1').textContent") -like '*sort du coffre*' -or (Js "document.getElementById('st1').textContent") -like '*relatif*' -or (Js "document.getElementById('st1').textContent") -like '*« . »*' } 5000) -and -not (Test-Path (Join-Path (Split-Path $vault) 'dehors.md')))

  # ---------- sécurité de l'aperçu ----------
  Js "openNote('Mal.md')" | Out-Null; Until { (Js "cur") -eq 'Mal.md' } 8000 | Out-Null
  Start-Sleep -Milliseconds 800
  Check "une note hostile (script, onerror, javascript:, image distante) : rien ne s'exécute" (((Js "String(window.__pwn)") -eq 'undefined') -and ((Js "document.querySelectorAll('#pv script, #pv iframe, #pv [onerror], #pv a[href]').length") -eq '0') -and ((Js "document.querySelectorAll('#pv img[src^=""http""]').length") -eq '0'))
  Check "le texte hostile est montré comme du texte, pas interprété" ((Js "document.getElementById('pv').textContent") -like '*<script>window.__pwn = 1</script>*')

  # ---------- images du coffre ----------
  Js "openNote('Chantiers/Gamma.md')" | Out-Null; Until { (Js "cur") -eq 'Chantiers/Gamma.md' } 8000 | Out-Null
  Check "une image du coffre ![[logo.png]] est affichée, chargée par le programme (largeur 1 px)" (Until { (Js "const i = document.querySelector('#pv img.emb'); return !!i && i.complete && i.naturalWidth === 1") -eq 'true' } 8000)
  $probe = 'const t = u => new Promise(r => { const i = new Image(); i.onload = () => r("ok"); i.onerror = () => r("refusé"); i.src = u; }); return Promise.all([t("https://vault.cdx.notes/logo.png"), t("https://vault.cdx.notes/.obsidian/cache.png"), t("https://vault.cdx.notes/Alpha.md"), t("https://vault.cdx.notes/..%2F..%2Fwindows%2Fsystem32%2Fx.png"), t("https://vault.cdx.notes/.trash/Nouvelle%20venue.md")]).then(a => window.__probe = a.join(","))'
  Js $probe | Out-Null
  Check "le serveur d'images ne sert que les images du coffre : logo.png oui ; dossier caché, note .md, chemin hors du coffre, corbeille : refusés" (Until { (Js "window.__probe") -eq 'ok,refusé,refusé,refusé,refusé' } 8000)

  # ---------- filtre, étiquettes ----------
  Js "const f = document.getElementById('filter'); f.value = '#exemple'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "filtrer par #étiquette : seule la note qui la porte reste" ((Js "[...document.querySelectorAll('#tree .nt')].map(x => x.dataset.p).join(',')") -eq 'Renommée.md')
  Js "const f = document.getElementById('filter'); f.value = 'chantiers/ga'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "filtrer par nom ou chemin (accents et casse ignorés)" ((Js "[...document.querySelectorAll('#tree .nt')].map(x => x.dataset.p).join(',')") -eq 'Chantiers/Gamma.md')
  Js "const f = document.getElementById('filter'); f.value = ''; f.dispatchEvent(new Event('input'))" | Out-Null

  # ---------- réglages ----------
  Js "document.querySelector('.tb-m').click()" | Out-Null
  Check "le bouton hachuré ouvre « Réglages » : Affichage, Mise en forme, Coffre, Claude et tes notes, Thème" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent.trim()).join('|')") -match '^Affichage\|Mise en forme\|Projets et tâches\|Graphe des liens\|Coffre\|Claude et tes notes…\|Thème$' } 4000)
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim() === 'Affichage').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim() === 'Aperçu seul').click()" | Out-Null
  Check "Affichage > Aperçu seul : l'éditeur disparaît" ((Js "document.getElementById('pane').className") -like '*mode-view*' -and ((Js "getComputedStyle(document.getElementById('ed')).display") -eq 'none'))
  Js "setMode('split')" | Out-Null
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim() === 'Coffre').click()" | Out-Null
  Check "Coffre : l'entrée « Changer de dossier » est grisée quand le dossier est imposé (variable d'essai)" ((Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Changer de dossier')).classList.contains('off')") -eq 'true')
  Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Claude et tes notes')).click()" | Out-Null
  Check "« Claude et tes notes… » montre la commande de déclaration du serveur MCP" (Until { (Js "document.querySelector('.dlg-code')?.textContent") -like 'claude mcp add --scope user cdx-notes -- "*cdx-notes-mcp.exe"' } 5000)
  Js "document.querySelector('.veil [data-y]').click()" | Out-Null

  # ---------- lot 4 : volet d'information, recherche, sélecteur, autocomplétion (sur des notes à part : Nav/) ----------
  W 'Nav/Soleil.md' "---`ntype: projet`nstatut: actif`n---`n# Soleil`n`n## Objectif`n`nTexte avec #soleil.`n`n- [ ] chauffer`n- [x] briller`n"
  W 'Nav/Lune.md' "# Lune`nLa lune est pâle ce soir. Voir [[Soleil]].`n`n## Phases`n`n### Pleine`n`nTexte.`n`n## Marées`n"
  W 'Nav/Comète.md' "# Comète`nPasse près de [[Soleil]] et de [[Lune]].`n"
  Check "trois notes de plus sur le disque entrent dans l'index" (Until { (Js "!!noteAt('Nav/Lune.md') && !!noteAt('Nav/Comète.md') && !!noteAt('Nav/Soleil.md')") -eq $true } 8000)
  Js "openNote('Nav/Lune.md')" | Out-Null
  Check "volet « Plan » : les titres de la note, dans l'ordre et en retrait" ((Until { (Js "[...document.querySelectorAll('#infobox .hd')].map(h => h.dataset.h).join('|')") -eq 'Lune|Phases|Pleine|Marées' } 6000) -and ((Js "document.querySelector('#infobox .hd[data-h=Pleine]').style.paddingLeft") -eq '32px'))
  Js "document.querySelector('#infobox .hd[data-h=Marées]').click()" | Out-Null
  Check "un clic sur un titre du plan place le curseur sur sa ligne dans l'éditeur (ligne 10)" (Until { (Js "ed.value.slice(0, ed.selectionStart).split('\n').length") -eq '10' } 4000)
  Js "document.querySelector('#info [data-i=liens]').click()" | Out-Null
  Check "volet « Liens » : le lien sortant [[Soleil]] est résolu" (Until { (Js "[...document.querySelectorAll('#infobox .lk')].map(l => l.dataset.path).join('|')") -eq 'Nav/Soleil.md' } 4000)
  Js "openNote('Nav/Soleil.md')" | Out-Null
  Check "volet « Liens » de Soleil : rétroliens depuis Lune et Comète, avec la ligne" (Until { $t = Js "[...document.querySelectorAll('#infobox .bl')].map(b => b.dataset.path + ':' + b.dataset.line).join('|')"; $t -like '*Nav/Lune.md:2*' -and $t -like '*Nav/Comète.md:2*' } 8000)
  Js "document.querySelector('#infobox .bl[data-path=`"Nav/Lune.md`"]').click()" | Out-Null
  Check "un rétrolien ouvre la note sur la ligne du lien" ((Until { (Js "cur") -eq 'Nav/Lune.md' } 6000) -and (Until { (Js "ed.value.slice(0, ed.selectionStart).split('\n').length") -eq '2' } 4000))
  Js "document.querySelector('#info [data-i=tags]').click()" | Out-Null
  Check "volet « Étiquettes » : #soleil compté une fois ; un clic filtre l'arborescence" ((Until { (Js "document.querySelector('#infobox .tgr[data-tag=soleil] .n')?.textContent") -eq '1' } 4000) -and ((Js "document.querySelector('#infobox .tgr[data-tag=soleil]').click(); document.getElementById('filter').value") -eq '#soleil'))
  Js "runSearch('pâle')" | Out-Null
  Check "recherche dans le texte (« pâle ») : un résultat, Lune, avec l'extrait surligné" (Until { (Js "[...document.querySelectorAll('#tree .hit')].map(h => h.dataset.p).join('|')") -eq 'Nav/Lune.md' -and (Js "document.querySelector('#tree .hit mark')?.textContent") -eq 'pâle' } 8000)
  Check "la recherche accepte les filtres : « type:projet in:Nav » rend Soleil seul" ((Js "runSearch('type:projet in:Nav'); 'ok'") -eq 'ok' -and (Until { (Js "[...document.querySelectorAll('#tree .hit')].map(h => h.dataset.p).join('|')") -eq 'Nav/Soleil.md' } 8000))
  Js "document.getElementById('filter').value = ''; renderTree(); openSwitcher()" | Out-Null
  Check "Ctrl+P : le sélecteur s'ouvre, « comè » propose Comète en premier" ((Until { (Js "!!document.querySelector('.veil.swv')") -eq $true } 4000) -and ((Js "var i = document.querySelector('.swv input'); i.value = 'comè'; i.dispatchEvent(new Event('input')); document.querySelector('.swv #swl .row-item.sel .grow')?.textContent") -eq 'Comète'))
  Js "document.querySelector('.swv').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))" | Out-Null
  Check "Entrée ouvre la note choisie et ferme le sélecteur" ((Until { (Js "cur") -eq 'Nav/Comète.md' } 6000) -and ((Js "!!document.querySelector('.veil.swv')") -eq $false))
  Js "openSwitcher(); var i = document.querySelector('.swv input'); i.value = 'Toute neuve'; i.dispatchEvent(new Event('input')); document.querySelector('.swv').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))" | Out-Null
  Check "un nom qui n'existe pas : Entrée crée la note et l'ouvre" ((Until { Test-Path "$vault\Toute neuve.md" } 8000) -and (Until { (Js "cur") -eq 'Toute neuve.md' } 6000))
  Type-Text "# Toute neuve`n`nVoir [[Com"
  Js "ed.setSelectionRange(ed.value.length, ed.value.length); ed.dispatchEvent(new Event('input', { bubbles: true }))" | Out-Null
  Check "taper [[Com dans l'éditeur ouvre l'autocomplétion avec Comète" (Until { (Js "!document.getElementById('ac').hidden && document.querySelector('#ac .it.on')?.firstChild.textContent") -eq 'Comète' } 4000)
  Js "ed.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))" | Out-Null
  Check "Entrée complète le lien : [[Comète]] dans le texte, liste fermée, note enregistrée" ((Until { (Js "ed.value.endsWith('[[Comète]]')") -eq $true } 4000) -and ((Js "document.getElementById('ac').hidden") -eq $true) -and (Until { Has (ReadV 'Toute neuve.md') '[[Comète]]' } 6000))

  # ---------- lot 5 : projets et tâches ----------
  Js "openProjects()" | Out-Null
  Check "vue Projets : la fiche Soleil (actif, 1 tâche ouverte) ; l'éditeur est masqué" ((Until { (Js "document.querySelector('#prlist .fr[data-p=`"Nav/Soleil.md`"]')?.textContent") -like '*actif*1 ouverte*' } 8000) -and ((Js "getComputedStyle(document.getElementById('pane')).display") -eq 'none'))
  Check "les tâches ouvertes du coffre sont listées (« chauffer »), pas les faites (« briller »)" ((Js "[...document.querySelectorAll('#tklist .tk1 .txt')].map(x => x.textContent).join('|')") -like '*chauffer*' -and (Js "[...document.querySelectorAll('#tklist .tk1 .txt')].map(x => x.textContent).join('|')") -notlike '*briller*')
  Js "document.querySelector('#tklist .tk1[data-p=`"Nav/Soleil.md`"] input').click()" | Out-Null
  Check "cocher une tâche écrit [x] dans la note sur le disque, et elle quitte la liste" ((Until { Has (ReadV 'Nav/Soleil.md') '- [x] chauffer' } 8000) -and (Until { (Js "[...document.querySelectorAll('#tklist .tk1 .txt')].map(x => x.textContent).join('|')") -notlike '*chauffer*' } 8000))
  Js "createProject('Zêta')" | Out-Null
  Check "« Nouveau projet » crée Projets/Zêta.md à partir du modèle et l'ouvre" ((Until { Test-Path "$vault\Projets\Zêta.md" } 8000) -and (Until { (Js "cur") -eq 'Projets/Zêta.md' } 6000) -and (Has (ReadV 'Projets/Zêta.md') 'type: projet'))
  Js "openProjects(); document.querySelector('#prchips [data-pf=actif]').click()" | Out-Null
  Check "le filtre « Actifs » ne garde que des fiches actives, Soleil comprise" (Until { $r = Js "[...document.querySelectorAll('#prlist .fr')].map(r => r.dataset.p + '=' + r.querySelector('.st').textContent.trim()).join('|')"; $r -like '*Nav/Soleil.md=actif*' -and ($r -split '\|' | Where-Object { $_ -notlike '*=actif' }).Count -eq 0 } 6000)
  Js "document.querySelector('#prchips [data-pf=tous]').click()" | Out-Null

  # ---------- lot 6 : graphe ----------
  Js "openGraph()" | Out-Null
  Check "vue Graphe : autant de nœuds que de notes, et les liens résolus" ((Until { (Js "__graph.nodes.length") -eq (Js "notes.length") -and ([int](Js "__graph.nodes.length")) -gt 0 } 8000) -and ([int](Js "__graph.edges.length")) -ge 3)
  Js "graphSettle(200); gFit()" | Out-Null
  Check "après la disposition, les notes liées sont en moyenne plus proches que les autres" ((Js "var by = new Map(__graph.nodes.map(n => [n.id, n])); var d = (a, b) => Math.hypot(by.get(a).x - by.get(b).x, by.get(a).y - by.get(b).y); var linked = __graph.edges.map(e => d(e.from, e.to)); var all = []; for (var i = 0; i < __graph.nodes.length; i++) for (var j = i + 1; j < __graph.nodes.length; j++) all.push(d(__graph.nodes[i].id, __graph.nodes[j].id)); var m = l => l.reduce((s, x) => s + x, 0) / l.length; m(linked) < m(all) ? 'oui' : 'non'") -eq 'oui')
  Check "le canevas est dessiné (fond noir, des pixels d'une autre couleur)" ((Js "var c = document.getElementById('gcanvas'); var d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; var n = 0; for (var i = 0; i < d.length; i += 4) if (d[i] + d[i + 1] + d[i + 2] > 0) n++; n > 50 ? 'oui' : 'non'") -eq 'oui')
  Js "graphOpenNode('Nav/Comète.md')" | Out-Null
  Check "un nœud ouvre sa note et referme le graphe" ((Until { (Js "cur") -eq 'Nav/Comète.md' } 6000) -and ((Js "String(view)") -eq 'null') -and ((Js "getComputedStyle(document.getElementById('pane')).display") -ne 'none'))
  Js "setGraphCol(true); openGraph()" | Out-Null
  Check "graphe dans une colonne : le canevas est à droite de l'éditeur, qui reste visible" ((Until { (Js "graphCol && !document.getElementById('gcol').hidden && document.getElementById('gcanvas').closest('#gcol') !== null && getComputedStyle(document.getElementById('pane')).display !== 'none' && document.getElementById('gcanvas').getBoundingClientRect().left > document.getElementById('ed').getBoundingClientRect().left ? 'oui' : 'non'") -eq 'oui' } 4000))
  Js "graphOpenNode('Nav/Lune.md')" | Out-Null
  Check "depuis la colonne, un nœud ouvre sa note et la colonne reste" ((Until { (Js "cur") -eq 'Nav/Lune.md' } 6000) -and ((Js "document.getElementById('gcol').hidden ? 'cachée' : 'visible'") -eq 'visible'))
  Js "setGraphCol(false)" | Out-Null
  Check "réglage retiré : le graphe reprend la place de l'éditeur" ((Until { (Js "String(view)") -eq 'graph' } 4000) -and ((Js "document.getElementById('gcanvas').closest('#main') !== null ? 'oui' : 'non'") -eq 'oui'))
  Js "showView(null)" | Out-Null

  # ---------- le bus : une autre application demande d'ouvrir une note ----------
  Check "bus open-note : la note demandée s'ouvre" ((BusSend 'notes' 'open-note' 'repos' @{ path = 'Beta.md' }) -and (Until { (Js "cur") -eq 'Beta.md' } 6000))
  BusSend 'notes' 'open-note' 'repos' @{ path = '../../windows/win.ini' } | Out-Null; Start-Sleep -Milliseconds 800
  Check "bus open-note : un chemin hors du coffre est refusé (rien ne s'ouvre)" ((Js "cur") -eq 'Beta.md' -and ((Js "tabs.some(p => p.includes('win.ini'))") -eq 'false'))

  # ---------- Claude écrit dans le coffre pendant que l'application est ouverte ----------
  $psi = New-Object Diagnostics.ProcessStartInfo("$Bin\cdx-notes-mcp.exe"); $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.Environment['CDX_NOTES_DIR'] = $vault; $psi.Environment['CDX_DATA_DIR'] = $data
  $mcp = [Diagnostics.Process]::Start($psi)
  function Rpc([string]$json) { $mcp.StandardInput.WriteLine($json); $mcp.StandardInput.Flush(); $mcp.StandardOutput.ReadLine() }
  Rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' | Out-Null
  $r = Rpc '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"notes_create","arguments":{"path":"Claude/Idée de Claude.md","content":"# Idée de Claude\n\nCréée par le serveur MCP, avec un lien vers [[Beta]].\n"}}}' | ConvertFrom-Json
  Check "Claude crée une note par le MCP : elle apparaît dans l'application sans rien faire (arbre, index)" (($r.result.isError -eq $false) -and (Until { (Js "notes.some(n => n.p === 'Claude/Idée de Claude.md')") -eq 'true' } 8000) -and ((Js "!!document.querySelector('#tree .fld[data-f=""Claude""]')") -eq 'true'))
  $r2 = Rpc '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"notes_append","arguments":{"path":"Beta","text":"\nAjouté par Claude via le MCP."}}}' | ConvertFrom-Json
  $okAppend = (($r2.result.isError -eq $false) -and (Until { (Js "document.getElementById('ed').value") -like '*Ajouté par Claude via le MCP*' } 8000))
  Check "Claude ajoute du texte à la note ouverte (propre) : l'éditeur le montre tout de suite" $okAppend
  if (-not $okAppend) { "   diagnostic : " + (Js "JSON.stringify({ cur, dirty: (docs.get(cur) || {}).dirty, saving: (docs.get(cur) || {}).saving, noticeHidden: document.getElementById('notice').hidden, notice: document.getElementById('notice').textContent, ed: document.getElementById('ed').value.slice(-80), status: document.getElementById('st1').textContent })") + " ; disque : " + ((ReadV 'Beta.md') -replace "`n", '|') }
  $r3 = Rpc '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"notes_trash","arguments":{"path":"Claude/Idée de Claude"}}}' | ConvertFrom-Json
  Check "Claude retire une note : .trash, et l'application la fait disparaître" (($r3.result.isError -eq $false) -and (Test-Path "$vault\.trash\Claude\Idée de Claude.md") -and (Until { (Js "notes.some(n => n.p === 'Claude/Idée de Claude.md')") -eq 'false' } 8000))
  $mcp.StandardInput.Close(); $mcp.WaitForExit(5000) | Out-Null

  # ---------- redémarrage : les onglets reviennent ----------
  $tabsBefore = Js "tabs.join('|')"; $curBefore = Js "cur"
  Start-Sleep -Milliseconds 1200
  $app.Kill(); Start-Sleep -Seconds 2
  $app = Open-Notes; $started += $app
  Check "après un redémarrage de l'application : les mêmes onglets, la même note active" ((Until { (Js "docs.size") -eq (($tabsBefore -split '\|').Count).ToString() } 30000) -and ((Js "tabs.join('|')") -eq $tabsBefore) -and (Until { (Js "cur") -eq $curBefore } 8000))
  Check "le programme n'a pas planté" (-not $app.HasExited)
}
finally {
  if ($mcp -and -not $mcp.HasExited) { try { $mcp.Kill() } catch { } }
  foreach ($p in $started) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch { } } }
  Start-Sleep -Seconds 1
  Remove-Item $suite, $data, $vault -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR, Env:CDX_NOTES_DIR, Env:CDX_NO_FOCUS -ErrorAction SilentlyContinue
}
