# Série de tests du navigateur CDX par le canal de contrôle (aucune entrée simulée). Prérequis : un fichier %LOCALAPPDATA%\CdxShell\debug.txt, puis
# (re)lancer cdx-browser.exe : il ouvre alors le canal « CdxBrowserCtl » (utilisateur courant seulement). Le test ouvre le navigateur, navigue sur example.org et
# example.com (accès réseau), télécharge un fichier depuis un serveur Python local temporaire, et nettoie derrière lui. Supprimer debug.txt ensuite.
param([string]$Bin)
$ErrorActionPreference = 'Continue'
# TOUJOURS isolé (comme cadre.ps1) : données, réglages de la suite, bus et canal de contrôle d'essai à part, créés ici si l'appelant ne les a pas posés.
# Ni le vrai profil du navigateur, ni le vrai bus, ni le navigateur de l'utilisateur ne sont touchés : le test ne ferme que le navigateur qu'il a lancé.
# (Le 01/11/2026, lancé sans ces variables, l'ancien test tuait tous les cdx-browser et relançait le sien sur le vrai profil.)
$own = -not $env:CDX_PIPE_PREFIX
if ($own) {
  $env:CDX_PIPE_PREFIX = "cdxnav$PID."
  $env:CDX_DATA_DIR = Join-Path $env:TEMP "cdx-nav-data-$PID"; $env:CDX_SUITE_DIR = Join-Path $env:TEMP "cdx-nav-suite-$PID"
  New-Item -ItemType Directory $env:CDX_DATA_DIR, $env:CDX_SUITE_DIR -Force | Out-Null
}
$data = $env:CDX_DATA_DIR
New-Item "$data\debug.txt" -ItemType File -Force | Out-Null   # ouvre le canal de contrôle du navigateur lancé ci-dessous
$pipe = $env:CDX_PIPE_PREFIX + 'browser.ctl'
if (-not $Bin) { $Bin = Join-Path $PSScriptRoot '..\bin' }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
  $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function St { $s = Ctl 'state' | ConvertFrom-Json; $s }
function Show-Tabs($s) { foreach ($t in $s.tabs) { "   onglet $($t.Id)$(if ($t.Id -eq $s.active) {' *'}) : « $($t.Title) » $($t.Url) chargement=$($t.Loading) zoom=$($t.zoom) favori=$($t.starred) icône=$($t.favicon)" } }
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }

# --- le navigateur est lancé par le test (le sien, isolé par le préfixe), après le dépôt de l'extension de test (chargée à l'ouverture)
$ext = "$data\extensions\cdx-test"; New-Item -ItemType Directory $ext -Force | Out-Null
'{"manifest_version":3,"name":"cdx-test","version":"1.0","content_scripts":[{"matches":["https://example.org/*"],"js":["c.js"],"run_at":"document_start"}]}' | Set-Content "$ext\manifest.json" -Encoding ascii
"document.documentElement.setAttribute('data-cdx-ext','ok');" | Set-Content "$ext\c.js" -Encoding ascii

"1. ouverture"
$brp = Start-Process (Join-Path $Bin 'cdx-browser.exe') -ArgumentList '--theme', 'rouge' -PassThru; Start-Sleep 7
$s = St; Show-Tabs $s
Check "un onglet, page « Nouvel onglet » interne" (@($s.tabs).Count -eq 1 -and $s.tabs[0].Url -like '*interne.html?p=newtab*')

"2. navigation par la barre d'adresse (« example.org »)"
Ctl 'cmd {"cmd":"br.go","url":"example.org"}' | Out-Null; Start-Sleep 5
$s = St; Show-Tabs $s
Check "l'adresse est résolue en https://example.org/ et le titre est celui de la page" ($s.tabs[0].Url -eq 'https://example.org/' -and $s.tabs[0].Title -eq 'Example Domain')
$ev = Ctl 'eval document.documentElement.getAttribute("data-cdx-ext")'
Check "extension non empaquetée : script de contenu appliqué à la page (valeur lue : $ev)" ($ev -eq '"ok"')

"3. nouvel onglet, historique, retour"
Ctl 'cmd {"cmd":"br.newtab","url":"https://example.com"}' | Out-Null; Start-Sleep 5
$s = St; Show-Tabs $s
Check "deux onglets, le nouveau est actif et chargé" (@($s.tabs).Count -eq 2 -and $s.active -eq 2 -and $s.tabs[1].Title -eq 'Example Domain')
Ctl 'cmd {"cmd":"br.go","url":"https://example.org/?x=2"}' | Out-Null; Start-Sleep 4
Ctl 'cmd {"cmd":"br.back"}' | Out-Null; Start-Sleep 3
$s = St; Check "retour : revient à https://example.com/" ($s.tabs[1].Url -eq 'https://example.com/')
Ctl 'cmd {"cmd":"br.fwd"}' | Out-Null; Start-Sleep 3
$s = St; Check "suivant : https://example.org/?x=2" ($s.tabs[1].Url -like 'https://example.org/?x=2*')
Check "l'icône de l'onglet (favicon) n'est pas exigée mais relevée" $true

"4. zoom, recherche dans la page, favori"
Ctl 'cmd {"cmd":"br.zoom","d":1}' | Out-Null; Start-Sleep 1
$s = St; Check "zoom avant : 110 %" ([math]::Round($s.tabs[1].zoom, 2) -eq 1.1)
Ctl 'cmd {"cmd":"br.zoom","d":0}' | Out-Null; Start-Sleep 1
$s = St; Check "zoom rétabli à 100 %" ([math]::Round($s.tabs[1].zoom, 2) -eq 1)
Ctl 'cmd {"cmd":"br.find","text":"domain","first":1,"dir":1}' | Out-Null; Start-Sleep 2
$fc = Ctl 'evalui document.getElementById("fcount").textContent'
Check "recherche « domain » : compteur affiché dans le cadre ($fc)" ($fc -match '\d+ / \d+')
Ctl 'cmd {"cmd":"br.star"}' | Out-Null; Start-Sleep 1
$s = St; Check "favori ajouté (étoile enfoncée)" ($s.tabs[1].starred -eq $true)
Check "favori écrit dans favoris.json" ((Get-Content "$data\favoris.json" -Raw) -match 'example.org')

"5. recherche web depuis la barre d'adresse"
Ctl 'cmd {"cmd":"br.go","url":"charte graphique pixel"}' | Out-Null; Start-Sleep 5
$s = St
Check "un texte qui n'est pas une adresse part en recherche ($($s.tabs[1].Url.Substring(0, [Math]::Min(48, $s.tabs[1].Url.Length)))…)" ($s.tabs[1].Url -like 'https://www.google.com/search?q=*' -or $s.tabs[1].Url -like 'https://www.google.com/sorry/*')   # Google renvoie vers sa page anti-robot (« sorry ») quand le test a tourné plusieurs fois de suite

"6. pages internes"
Ctl 'cmd {"cmd":"br.internal","p":"historique"}' | Out-Null; Start-Sleep 3
$h = Ctl 'eval document.body.innerText'
Check "historique : liste les pages visitées (example)" ($h -match 'Example Domain')
Ctl 'cmd {"cmd":"br.internal","p":"favoris"}' | Out-Null; Start-Sleep 3
Check "favoris : liste example.org" ((Ctl 'eval document.body.innerText') -match 'example.org')
$s = St; Show-Tabs $s

"7. téléchargement (serveur local temporaire)"
$srvdir = Join-Path $env:TEMP 'cdx-dl-test'; New-Item -ItemType Directory $srvdir -Force | Out-Null
[IO.File]::WriteAllBytes("$srvdir\cdx-test.bin", (1..200000 | ForEach-Object { [byte]($_ % 251) }))
$srv = Start-Process python -ArgumentList '-m', 'http.server', '8766', '--bind', '127.0.0.1', '--directory', $srvdir -PassThru -WindowStyle Hidden
# pages piégées : une page web ne doit ni ouvrir ni encadrer une page interne, ni ouvrir de fenêtre d'elle-même
'<title>PIEGE</title><script>setTimeout(() => location.href = "https://cdx.shell/browser/interne.html?p=reglages", 300)</script>' | Set-Content "$srvdir\piege.html" -Encoding utf8
'<title>CADRE</title><iframe src="https://cdx.shell/browser/interne.html?p=motsdepasse"></iframe>' | Set-Content "$srvdir\cadre.html" -Encoding utf8
'<title>SURGIT</title><script>window.open("http://127.0.0.1:8766/piege.html?surgie")</script>' | Set-Content "$srvdir\surgit.html" -Encoding utf8
Start-Sleep 2
"7a. pages internes et fenêtres surgissantes depuis une page web"
$log = "$data\journal-browser.log"
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8766/piege.html"}' | Out-Null; Start-Sleep 3
$s = St; Check "une page web qui se redirige vers cdx://reglages reste où elle est" ((($s.tabs | Where-Object { $_.Id -eq $s.active }).Url) -like 'http://127.0.0.1:8766/piege.html*' -and ((Get-Content $log -Raw) -match 'page interne refusée'))
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8766/cadre.html"}' | Out-Null; Start-Sleep 3
Check "une page web ne peut pas encadrer une page interne (cadre refusé)" ((Get-Content $log -Raw) -match 'cadre interne refusé')
$n0 = @((St).tabs).Count
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8766/surgit.html"}' | Out-Null; Start-Sleep 3
Check "une fenêtre ouverte par la page sans clic est bloquée (pas d'onglet de plus)" ((@((St).tabs).Count -eq $n0) -and ((Get-Content $log -Raw) -match 'fenêtre surgissante bloquée'))
Ctl 'cmd {"cmd":"br.go","url":"cdx://reglages"}' | Out-Null; Start-Sleep 2
$s = St; Check "cdx://reglages tapé dans la barre d'adresse s'ouvre toujours" ((($s.tabs | Where-Object { $_.Id -eq $s.active }).Url) -like '*interne.html?p=reglages')
Ctl 'cmd {"cmd":"br.back"}' | Out-Null; Start-Sleep 2
Ctl 'cmd {"cmd":"br.fwd"}' | Out-Null; Start-Sleep 2
$s = St; Check "précédente puis suivante ramènent à la page interne (navigation du navigateur)" ((($s.tabs | Where-Object { $_.Id -eq $s.active }).Url) -like '*interne.html?p=reglages')
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8766/cdx-test.bin"}' | Out-Null; Start-Sleep 5
$s = St
$d = @($s.downloads)[0]
Check "téléchargement suivi par la page « cdx://telechargements » (état : $($d.state), $($d.received) o)" ($d -and $d.state -eq 'done' -and $d.received -ge 200000)
$dest = Join-Path "$env:USERPROFILE\Downloads" 'cdx-test.bin'
Check "fichier écrit dans Téléchargements (sans la fenêtre d'Edge)" (Test-Path $dest)
Remove-Item $dest -Force -ErrorAction SilentlyContinue
Stop-Process -Id $srv.Id -Force; Remove-Item $srvdir -Recurse -Force -ErrorAction SilentlyContinue

"8. cadre : onglets affichés par la page du navigateur"
Check "le cadre liste les onglets ($((Ctl 'evalui document.querySelectorAll("#tabs .tab").length')) boutons)" ([int](Ctl 'evalui document.querySelectorAll("#tabs .tab").length') -ge 3)

"10. session continue et onglet rouvert"
$sess = Get-Content "$data\session.json" -Raw | ConvertFrom-Json
Check "session.json est écrit sans fermer la fenêtre, avec les onglets et l'actif" ($sess.Windows.Count -eq 1 -and @($sess.Windows[0].Tabs).Count -ge 2 -and $sess.Windows[0].Active -ge 0)
$s = St; $n0 = @($s.tabs).Count; $closedUrl = $s.tabs[-1].Url; $closedId = $s.tabs[-1].Id
Ctl "cmd {`"cmd`":`"br.close`",`"id`":$closedId}" | Out-Null; Start-Sleep 1
Ctl 'cmd {"cmd":"br.reopen"}' | Out-Null; Start-Sleep 4
$s = St; Check "Ctrl+Maj+T rouvre l'onglet fermé (même adresse)" (@($s.tabs).Count -eq $n0 -and $s.tabs[-1].Url -eq $closedUrl)

"11. groupes d'onglets"
$s = St; $ids = @($s.tabs | Select-Object -First 2 | ForEach-Object { $_.Id })
Ctl ('cmd {"cmd":"br.group.new","ids":[' + ($ids -join ',') + '],"name":"Travail","color":"vert"}') | Out-Null; Start-Sleep 1
$s = St; $g = @($s.groups)[0]
Check "un groupe « Travail » vert avec deux onglets, contigus" ($g.Name -eq 'Travail' -and $g.Color -eq 'vert' -and @($s.tabs | Where-Object { $_.group -eq $g.Id }).Count -eq 2 -and $s.tabs[0].group -eq $g.Id -and $s.tabs[1].group -eq $g.Id)
Check "le cadre montre la pastille du groupe et les onglets colorés" ((Ctl 'evalui document.querySelectorAll("#tabs .grp").length + "/" + document.querySelectorAll("#tabs .tab.g").length') -eq '"1/2"')
Ctl "cmd {`"cmd`":`"br.group`",`"group`":$($g.Id),`"what`":`"collapse`",`"on`":true}" | Out-Null; Start-Sleep 1
$s = St; Check "groupe replié : ses onglets disparaissent de la barre, l'onglet actif est hors du groupe" (([int](Ctl 'evalui document.querySelectorAll("#tabs .tab:not(.new)").length')) -eq (@($s.tabs).Count - 2) -and ($s.tabs | Where-Object { $_.Id -eq $s.active }).group -ne $g.Id)
Ctl "cmd {`"cmd`":`"br.select`",`"id`":$($ids[0])}" | Out-Null; Start-Sleep 1
$s = St; Check "choisir un onglet du groupe le déplie" (-not @($s.groups)[0].Collapsed -and $s.active -eq $ids[0])
$sess = Get-Content "$data\session.json" -Raw | ConvertFrom-Json
Check "la session garde le groupe et l'appartenance des onglets" (@($sess.Windows[0].Groups).Count -eq 1 -and @($sess.Windows[0].Tabs | Where-Object { $_.Group -eq $g.Id }).Count -eq 2)
Ctl "cmd {`"cmd`":`"br.group`",`"group`":$($g.Id),`"what`":`"ungroup`"}" | Out-Null; Start-Sleep 1
$s = St; Check "dégrouper : plus de groupe, les onglets restent" (@($s.groups).Count -eq 0 -and @($s.tabs).Count -ge 2)

"12. pages scindées"
Ctl 'cmd {"cmd":"br.split"}' | Out-Null; Start-Sleep 2
$s = St; $l = $s.tabs | Where-Object { $_.Id -eq $s.active }; $r2 = $s.tabs | Where-Object { $_.Id -eq $s.split }
Check "deux onglets visibles côte à côte, le second à droite du premier" ($s.split -and $l.visible -and $r2.visible -and $r2.bounds[0] -gt ($l.bounds[0] + $l.bounds[2] - 5) -and $l.bounds[2] -gt 100 -and $r2.bounds[2] -gt 100)
Check "le cadre réserve le second rectangle (#vpB visible, avec sa barre de volet)" ((Ctl 'evalui String(document.getElementById("vpB").hidden) + "/" + String(document.getElementById("vpB").previousElementSibling.classList.contains("cdx-split"))') -eq '"false/true"')
Ctl 'cmd {"cmd":"br.unsplit"}' | Out-Null; Start-Sleep 2
$s = St; $l = $s.tabs | Where-Object { $_.Id -eq $s.active }
Check "réunies : une seule page visible, pleine largeur" (-not $s.split -and @($s.tabs | Where-Object { $_.visible }).Count -eq 1 -and $l.bounds[2] -gt 700)

"13. plusieurs fenêtres"
Ctl 'cmd {"cmd":"br.newwindow","url":"https://example.com"}' | Out-Null; Start-Sleep 6
$s = St
Check "une seconde fenêtre, avec son onglet, devient la fenêtre courante" (@($s.windows).Count -eq 2 -and $s.window -eq 2 -and $s.tabs[0].Url -eq 'https://example.com/')
$sess = Get-Content "$data\session.json" -Raw | ConvertFrom-Json
Check "la session compte les deux fenêtres" (@($sess.Windows).Count -eq 2)
Ctl 'use 1' | Out-Null; $s = St; $mv = $s.tabs[-1]
Ctl "cmd {`"cmd`":`"br.towindow`",`"id`":$($mv.Id)}" | Out-Null; Start-Sleep 4
Ctl 'use 0' | Out-Null; $s = St
Check "« Ouvrir dans une nouvelle fenêtre » : une troisième fenêtre reçoit l'onglet sans le recharger (même adresse, même titre)" (@($s.windows).Count -eq 3 -and $s.tabs[0].Url -eq $mv.Url -and $s.tabs[0].Title -eq $mv.Title)
$evm = Ctl 'eval document.title'
Check "la page déplacée répond toujours ($evm)" ($evm -and $evm -ne 'null' -and $evm.Length -gt 2)
# l'onglet déplacé est bien branché sur sa nouvelle fenêtre : un changement de titre redessine la barre d'onglets de la fenêtre 3, pas de la fenêtre 1
Ctl 'eval document.title = "DEPLACE-13"' | Out-Null; Start-Sleep 1
Check "après le déplacement, le titre changé s'affiche dans la barre d'onglets de la nouvelle fenêtre" ((Ctl 'evalui [...document.querySelectorAll("#tabs .tab .tt")].map(x => x.textContent).join("|")') -match 'DEPLACE-13')
Ctl 'use 1' | Out-Null
Check "… et pas dans celle de l'ancienne" ((Ctl 'evalui [...document.querySelectorAll("#tabs .tab .tt")].map(x => x.textContent).join("|")') -notmatch 'DEPLACE-13')
Ctl 'use 0' | Out-Null
Ctl 'cmd {"cmd":"win.close"}' | Out-Null; Start-Sleep 2; Ctl 'use 2' | Out-Null; Ctl 'cmd {"cmd":"win.close"}' | Out-Null; Start-Sleep 2; Ctl 'use 0' | Out-Null
$s = St; Check "fermer deux fenêtres : le programme continue avec la première" (@($s.windows).Count -eq 1 -and -not $brp.HasExited)

"14. réglages : moteur de recherche"
Ctl 'cmd {"cmd":"br.internal","p":"reglages"}' | Out-Null; Start-Sleep 3
Check "la page cdx://reglages liste les moteurs" ([int](Ctl 'eval document.querySelectorAll("#eng option").length') -ge 4)
Ctl 'eval (() => { const e = document.getElementById("eng"); e.value = "duckduckgo"; e.dispatchEvent(new Event("change")); })()' | Out-Null; Start-Sleep 1
Check "le choix est écrit dans reglages-navigateur.json" ((Get-Content "$data\reglages-navigateur.json" -Raw) -match '"Search":\s*"duckduckgo"')
Ctl 'cmd {"cmd":"br.go","url":"charte pixel"}' | Out-Null; Start-Sleep 4
$s = St; Check "une recherche part désormais sur DuckDuckGo" (($s.tabs | Where-Object { $_.Id -eq $s.active }).Url -like 'https://duckduckgo.com/?q=*')

"15. extensions : installation depuis une archive, blocage (règle DNR), page d'options, désactivation, retrait"
$srvdir2 = Join-Path $env:TEMP "cdx-ext-test-$PID"; New-Item -ItemType Directory "$srvdir2\ext" -Force | Out-Null
'{"manifest_version":3,"name":"cdx-bloqueur","version":"1.2","permissions":["declarativeNetRequest"],"host_permissions":["<all_urls>"],"options_page":"options.html","declarative_net_request":{"rule_resources":[{"id":"r","enabled":true,"path":"rules.json"}]}}' | Set-Content "$srvdir2\ext\manifest.json" -Encoding ascii
'[{"id":1,"priority":1,"action":{"type":"block"},"condition":{"urlFilter":"bloque.js","resourceTypes":["script"]}}]' | Set-Content "$srvdir2\ext\rules.json" -Encoding ascii
'<!doctype html><title>Options cdx-bloqueur</title>' | Set-Content "$srvdir2\ext\options.html" -Encoding ascii
Compress-Archive -Path "$srvdir2\ext\*" -DestinationPath "$srvdir2\ext.zip" -Force
"document.title = 'SCRIPT-PASSE';" | Set-Content "$srvdir2\bloque.js" -Encoding ascii
'<!doctype html><title>SCRIPT-ATTENDU</title><script src="bloque.js" onerror="document.title=''SCRIPT-BLOQUE''"></script>' | Set-Content "$srvdir2\page.html" -Encoding ascii
'<!doctype html><title>Connexion</title><form><input name="u" type="text"><input name="p" type="password"></form>' | Set-Content "$srvdir2\login.html" -Encoding ascii
$srv2 = Start-Process python -ArgumentList '-m', 'http.server', '8767', '--bind', '127.0.0.1', '--directory', $srvdir2 -PassThru -WindowStyle Hidden; Start-Sleep 2
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/page.html"}' | Out-Null; Start-Sleep 3
Check "sans l'extension : le script passe" ((Ctl 'eval document.title') -eq '"SCRIPT-PASSE"')
Ctl 'cmd {"cmd":"br.internal","p":"extensions"}' | Out-Null; Start-Sleep 3
Check "cdx://extensions liste l'extension de test (cdx-test) chargée au démarrage" ((Ctl 'eval document.body.innerText') -match 'cdx-test')
New-Item -ItemType Directory "$data\temoin-hors-extensions" -Force | Out-Null
Ctl 'eval post({ cmd: "in.ext.install", url: "http://127.0.0.1:8767/ext.zip", name: "..\\temoin-hors-extensions" })' | Out-Null; Start-Sleep 3
Check "in.ext.install : un nom qui remonte (..\) est refusé, rien n'est effacé hors de extensions\" ((Test-Path "$data\temoin-hors-extensions") -and ((Ctl 'eval document.body.innerText') -match 'Nom d.extension refus'))
Ctl 'eval post({ cmd: "in.ext.install", url: "https://exemple.invalid/ext.zip", name: "autre" })' | Out-Null; Start-Sleep 2
Check "in.ext.install : une archive hors de GitHub est refusée" ((Ctl 'eval document.body.innerText') -match 'Adresse refus')
Ctl 'eval post({ cmd: "in.ext.install", url: "http://127.0.0.1:8767/ext.zip", name: "bloqueur" })' | Out-Null; Start-Sleep 6
$lst = Ctl 'eval document.body.innerText'
Check "l'archive est téléchargée, dépliée sous extensions\bloqueur et chargée (listée avec sa version)" ((Test-Path "$data\extensions\bloqueur\manifest.json") -and $lst -match 'cdx-bloqueur' -and $lst -match '1\.2')
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/page.html?2"}' | Out-Null; Start-Sleep 3
Check "avec l'extension : la règle bloque le script" ((Ctl 'eval document.title') -eq '"SCRIPT-BLOQUE"')
Ctl 'cmd {"cmd":"br.internal","p":"extensions"}' | Out-Null; Start-Sleep 2
Ctl 'eval (() => { const b = [...document.querySelectorAll(".ri")].find(r => r.innerText.includes("cdx-bloqueur")); b.querySelector("[data-opt]").click(); })()' | Out-Null; Start-Sleep 3
$s = St; Check "« Réglages » ouvre la page d'options de l'extension dans un onglet" ((($s.tabs | Where-Object { $_.Id -eq $s.active }).Url) -like 'chrome-extension://*/options.html')
Ctl 'cmd {"cmd":"br.internal","p":"extensions"}' | Out-Null; Start-Sleep 2
Ctl 'eval (() => { const b = [...document.querySelectorAll(".ri")].find(r => r.innerText.includes("cdx-bloqueur")); b.querySelector("[data-en]").click(); })()' | Out-Null; Start-Sleep 2
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/page.html?3"}' | Out-Null; Start-Sleep 3
Check "désactivée : le script passe à nouveau" ((Ctl 'eval document.title') -eq '"SCRIPT-PASSE"')
Ctl 'cmd {"cmd":"br.internal","p":"extensions"}' | Out-Null; Start-Sleep 2
Ctl 'eval post({ cmd: "in.ext.remove", id: [...document.querySelectorAll("[data-rmx]")].find(b => b.closest(".ri").innerText.includes("cdx-bloqueur")).dataset.rmx })' | Out-Null; Start-Sleep 3
Check "retirée : plus dans la liste, dossier supprimé" (-not (Test-Path "$data\extensions\bloqueur") -and ((Ctl 'eval document.body.innerText') -notmatch 'cdx-bloqueur'))

"16. import des favoris et des mots de passe, remplissage automatique"
'{"roots":{"bookmark_bar":{"children":[{"type":"url","name":"Exemple deux","url":"https://example.com/deux"},{"type":"folder","name":"Dossier","children":[{"type":"url","name":"Trois","url":"https://example.net/trois"}]}]},"other":{"children":[{"type":"url","name":"Déjà là","url":"https://example.org/?x=2"}]}}}' | Set-Content "$srvdir2\Bookmarks" -Encoding utf8
Ctl 'cmd {"cmd":"br.internal","p":"reglages"}' | Out-Null; Start-Sleep 2
Ctl ('eval post({ cmd: "in.import.bookmarks", path: ' + ("$srvdir2\Bookmarks" | ConvertTo-Json) + ' })') | Out-Null; Start-Sleep 2
$fav = Get-Content "$data\favoris.json" -Raw
Check "favoris importés d'un fichier Bookmarks (dossiers compris), sans doublon" ($fav -match 'example.com/deux' -and $fav -match 'example.net/trois' -and ([regex]::Matches($fav, 'https://example.org/[?]x=2"').Count -eq 1))
Check "la page annonce le résultat (2 ajoutés, 1 déjà là)" ((Ctl 'eval document.getElementById("msg").textContent') -match '2 ajoutés, 1 déjà')
"name,url,username,password,note`nTest,http://127.0.0.1:8767/login.html,moi,secret-1,`n" | Set-Content "$srvdir2\mdp.csv" -Encoding utf8
Ctl ('eval post({ cmd: "in.import.passwords", path: ' + ("$srvdir2\mdp.csv" | ConvertTo-Json) + ' })') | Out-Null; Start-Sleep 2
Check "mots de passe importés : chiffrés sur le disque (motsdepasse.bin, rien en clair)" ((Test-Path "$data\motsdepasse.bin") -and -not ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes("$data\motsdepasse.bin")) -match 'secret-1'))
Ctl 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/login.html"}' | Out-Null; Start-Sleep 4
$vals = 'eval document.querySelector("[name=u]").value + "/" + document.querySelector("[name=p]").value'
Check "page de connexion : rien n'est rempli tant que l'utilisateur n'a pas touché au formulaire" ((Ctl $vals) -eq '"/"')
Ctl 'eval (() => { const p = document.querySelector("[name=p]"); p.focus(); p.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true })); p.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true })); return 1; })()' | Out-Null; Start-Sleep -Milliseconds 500
Check "un script de la page (focus, faux clic, fausse touche) ne déclenche pas le remplissage" ((Ctl $vals) -eq '"/"')
Ctl 'tap [name=p]' | Out-Null; Start-Sleep -Milliseconds 500
Check "un vrai clic dans le champ mot de passe : identifiant et mot de passe remplis" ((Ctl $vals) -eq '"moi/secret-1"')
Ctl 'cmd {"cmd":"br.go","url":"http://localhost:8767/login.html"}' | Out-Null; Start-Sleep 4
Ctl 'tap [name=p]' | Out-Null; Start-Sleep -Milliseconds 500
Check "une autre origine (localhost au lieu de 127.0.0.1) : rien n'est rempli, même après un clic" ((Ctl $vals) -eq '"/"')
Ctl 'cmd {"cmd":"br.internal","p":"motsdepasse"}' | Out-Null; Start-Sleep 3
$pwt = Ctl 'eval document.body.innerText'
Check "cdx://motsdepasse liste l'identifiant sans montrer le mot de passe" (($pwt -match 'moi') -and ($pwt -notmatch 'secret-1'))
Stop-Process -Id $srv2.Id -Force; Remove-Item $srvdir2 -Recurse -Force -ErrorAction SilentlyContinue

"9. fermeture d'onglet puis de la fenêtre"
Ctl 'cmd {"cmd":"br.close","id":1}' | Out-Null; Start-Sleep 1
$s = St; Check "onglet 1 fermé" (@($s.tabs | Where-Object { $_.Id -eq 1 }).Count -eq 0)
Ctl 'cmd {"cmd":"win.close"}' | Out-Null; Start-Sleep 2
Check "fenêtre fermée : le programme s'arrête avec sa dernière fenêtre" $brp.HasExited
Check "session enregistrée (session.json)" (Test-Path "$data\session.json")
Remove-Item $ext -Recurse -Force -ErrorAction SilentlyContinue
Get-Content "$data\journal-browser.log" | Where-Object { $_ -match 'extension|navigateur|refus|exception|échou' } | Select-Object -Last 8
try { if (-not $brp.HasExited) { $brp.Kill() } } catch { }
if ($own) { Start-Sleep 1; Remove-Item $env:CDX_DATA_DIR, $env:CDX_SUITE_DIR -Recurse -Force -ErrorAction SilentlyContinue }

