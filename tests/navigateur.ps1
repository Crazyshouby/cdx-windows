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
Start-Sleep 2
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

