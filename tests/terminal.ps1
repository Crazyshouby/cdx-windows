# Essai isolé du terminal (cdx-terminal.exe) dans sa vraie fenêtre : de vrais PowerShell derrière de vraies pseudo-consoles, affichés par la page (xterm.js, WebGL).
# On commande le terminal par le canal de test (écrire dans l'entrée d'un shell, lire l'écran de la page, agir sur les onglets et les volets) : aucune entrée simulée sur
# le bureau (les raccourcis clavier sont des événements envoyés dans la page, pas des touches), et le presse-papiers n'est pas touché (copier/coller ne sont pas essayés ici).
# Le terminal tourne avec son propre bus, son propre dossier de données et son propre suite.json.
#   pwsh -File tests\terminal.ps1 [-Bin <dossier de cdx-terminal.exe>] [-ExplorerBin <dossier de cdx-explorer.exe>]
# Avec l'Explorateur (s'il est là) : « Ouvrir dans un terminal » ouvre un onglet du terminal déjà ouvert, puis relance le terminal s'il est fermé (chemin de apps.json).
param([string]$Bin, [string]$ExplorerBin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
if (-not $ExplorerBin) { $ExplorerBin = $Bin }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-tm-suite-$id"; $data = Join-Path $env:TEMP "cdx-tm-data-$id"
New-Item -ItemType Directory $suite, $data -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxtm$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line, [string]$who = 'terminal') {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}$who.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function Screen([int]$sid = 1) { Js "window.__screenText(window.__panes.get($sid))" }
function Sessions { $s = Ctl 'state'; if ($s -match '(\d+) session') { [int]$Matches[1] } else { 0 } }
function Key([string]$init) { Js "document.dispatchEvent(new KeyboardEvent('keydown', Object.assign({ bubbles: true, cancelable: true }, $init))); 'ok'" | Out-Null }

$started = @()
try {
  $app = Start-Process "$Bin\cdx-terminal.exe" -PassThru; $started += $app
  Check "la fenêtre s'ouvre et le canal de test répond" (Until { (Ctl 'state') -like 'fenêtre ouverte*' } 20000)
  Check "xterm.js est chargé dans la page" (Until { (Js "typeof Terminal + '/' + typeof FitAddon + '/' + typeof WebglAddon") -eq 'function/object/object' } 15000)
  Check "le premier shell démarre : une session existe" (Until { (Sessions) -eq 1 } 20000)
  Check "l'invite de PowerShell s'affiche dans la page" (Until { (Screen) -match 'PS [A-Z]:\\' } 20000)

  # --- ce qu'on écrit dans l'entrée arrive au shell, et sa réponse à la page ---
  Ctl "write Write-Output ('CDX'+'TEST'+(1+2))\r" | Out-Null
  Check "une commande écrite dans l'entrée s'exécute et son résultat est affiché" (Until { (Screen) -match 'CDXTEST3' } 15000)
  Ctl "write Write-Output ('é€ß'+'-ACC')\r" | Out-Null
  Check "les accents et le signe euro font l'aller-retour (UTF-8)" (Until { (Screen) -match 'é€ß-ACC' } 15000)
  Ctl "write `$Host.UI.RawUI.WindowSize.Width\r" | Out-Null
  $cols = Js "window.__panes.get(1).term.cols"
  $okW = Until { (Screen) -match "(?m)^$cols\s*$" } 15000
  Check "le shell voit la même largeur que la page ($cols colonnes)" $okW
  if (-not $okW) { "    écran du volet 1 :"; (Screen 1) -split "`n" | Select-Object -Last 12 | ForEach-Object { "    | $_" } }

  # --- police et rendu ---
  Check "la police pixel à chasse fixe de la charte est chargée" ((Js "document.fonts.check('14px \""KIRC Pixel Mono\""')") -eq $true)
  $cell = Js "(() => { const t = window.__panes.get(1).term; return t.element.querySelector('.xterm-screen').getBoundingClientRect().width / t.cols; })()"
  Check "la case fait 7 pixels de large (avance de la police), sans fraction" ("$cell" -eq '7')
  Check "le texte est dessiné par le moteur WebGL (caractères de cadre exacts)" ((Js "!!window.__panes.get(1).gl") -eq $true)
  Check "fond noir et texte vert de la charte (thème rouge : #00ff00)" ((Js "(() => { const o = window.__panes.get(1).term.options.theme; return o.background + ' ' + o.foreground; })()") -eq '#000000 #00ff00')
  Check "la couleur ANSI rouge est celle de la charte (#ff5a4a)" ((Js "window.__panes.get(1).term.options.theme.red") -eq '#ff5a4a')
  Check "pas de gras : la police normale est aussi celle du « gras »" ((Js "window.__panes.get(1).term.options.fontWeightBold") -eq 'normal')
  Check "aucun coin arrondi dans le cadre, les onglets ni les volets" ((Js "[...document.querySelectorAll('.win, .tb, .tb-btn, .tabs, .tab, .termbox, .cb')].every(e => getComputedStyle(e).borderRadius === '0px')") -eq $true)
  Js "cdx.emit('theme', 'jean')" | Out-Null
  Check "un changement de thème recolore le terminal (jean : texte #ffffff, sélection #002cb2 / #ffffff)" (Until { (Js "(() => { const o = window.__panes.get(1).term.options.theme; return o.foreground + ' ' + o.selectionBackground + ' ' + o.selectionForeground; })()") -eq '#ffffff #002cb2 #ffffff' } 3000)

  # --- onglets ---
  Check "un onglet à l'ouverture, avec son bouton « + »" ((Js "document.querySelectorAll('#tabs .tab:not(.new)').length + '/' + document.querySelectorAll('#tabs .tab.new').length") -eq '1/1')
  Key "{ code: 'KeyT', key: 'T', ctrlKey: true, shiftKey: true }"
  Check "Ctrl+Maj+T ouvre un second onglet avec son propre shell (2 sessions)" ((Until { (Sessions) -eq 2 } 20000) -and (Js "window.__tabs.length") -eq 2)
  Check "le nouveau shell est dans l'onglet actif et affiche son invite" (Until { (Screen 2) -match 'PS [A-Z]:\\' } 20000)
  Ctl "write Write-Output ('SECOND'+'-TAB')\r" | Out-Null
  Check "l'entrée va au volet qui a le focus (le second onglet), pas au premier" ((Until { (Screen 2) -match 'SECOND-TAB' } 15000) -and -not ((Screen 1) -match 'SECOND-TAB'))
  Key "{ code: 'Tab', key: 'Tab', ctrlKey: true, shiftKey: true }"
  Check "Ctrl+Maj+Tab revient au premier onglet" (Until { (Js "window.__tabs.indexOf(window.__tabs.find(t => t.view.classList.contains('on')))") -eq 0 } 3000)
  Check "l'onglet actif porte la classe « on » dans la barre" ((Js "document.querySelector('#tabs .tab.on .tt') !== null") -eq $true)

  # --- volets ---
  Key "{ code: 'KeyD', key: 'D', altKey: true, shiftKey: true }"
  Check "Alt+Maj+D sépare le volet : un troisième shell, deux volets dans le premier onglet" ((Until { (Sessions) -eq 3 } 20000) -and (Js "window.__tabs[0].root.dir") -in 'h', 'v')
  $b = Js "window.__tabs[0].focus.sid"
  Check "le nouveau volet a le focus et affiche son invite" ((Until { (Screen $b) -match 'PS [A-Z]:\\' } 20000))
  Ctl "write Write-Output ('VOLET'+'-B')\r" | Out-Null
  Check "chaque volet a son shell : le marqueur n'apparaît que dans le volet qui l'a reçu" ((Until { (Screen $b) -match 'VOLET-B' } 15000) -and -not ((Screen 1) -match 'VOLET-B'))
  Key "{ code: 'ArrowLeft', key: 'ArrowLeft', altKey: true }"
  Key "{ code: 'ArrowUp', key: 'ArrowUp', altKey: true }"
  Check "Alt+flèches change de volet (le focus est revenu au premier volet)" (Until { (Js "window.__tabs[0].focus.sid") -eq 1 } 3000)
  $area1 = [int](Js "window.__panes.get(1).term.cols * window.__panes.get(1).term.rows")
  Js "(() => { const s = window.__tabs[0].root; s.ratio = 0.3; s.apply(); return 'ok'; })()" | Out-Null
  Check "déplacer le séparateur réduit la grille du volet" (Until { [int](Js "window.__panes.get(1).term.cols * window.__panes.get(1).term.rows") -lt $area1 } 10000)
  $n = Js "window.__panes.get(1).term.cols"; $r = Js "window.__panes.get(1).term.rows"
  Ctl "write@1 [string]`$Host.UI.RawUI.WindowSize.Width+'x'+`$Host.UI.RawUI.WindowSize.Height\r" | Out-Null
  $okS = Until { (Screen 1) -match "(?m)^${n}x${r}\s*$" } 15000
  Check "et son shell connaît la nouvelle taille ($n x $r)" $okS
  if (-not $okS) { "    écran du volet 1 :"; (Screen 1) -split "`n" | Select-Object -Last 12 | ForEach-Object { "    | $_" } }
  Js "window.__api.closePane(window.__panes.get($b)); 'ok'" | Out-Null
  Check "fermer un volet arrête son shell et rend la place au voisin (2 sessions, un seul volet)" ((Until { (Sessions) -eq 2 } 15000) -and (Js "window.__tabs[0].root.constructor.name") -eq 'Pane')

  # --- un shell qui se termine ferme son volet ---
  Js "window.__api.activate(window.__tabs[1]); 'ok'" | Out-Null
  Check "le titre de la fenêtre suit le terminal (séquence OSC)" ((Ctl "write Write-Host -NoNewline ([char]27+']0;Titre CDX'+[char]7)\r") -eq 'ok' -and (Until { (Js "document.getElementById('ttl').textContent") -eq 'Titre CDX' } 8000))
  Ctl "write exit\r" | Out-Null
  Check "« exit » dans le dernier volet d'un onglet ferme l'onglet (1 onglet, 1 session) sans fermer la fenêtre" ((Until { (Js "window.__tabs.length") -eq 1 -and (Sessions) -eq 1 } 15000) -and -not $app.HasExited)

  # --- fenêtre ---
  $second = Start-Process "$Bin\cdx-terminal.exe" -PassThru; $started += $second
  Check "un second lancement passe la main à la première instance et s'arrête" ($second.WaitForExit(10000))

  # --- un dossier de départ : second lancement, puis Explorateur ---
  $dirA = Join-Path $env:TEMP "cdx tm dossier é $id"; $dirB = Join-Path $env:TEMP "cdx tm autre $id"
  New-Item -ItemType Directory $dirA, $dirB -Force | Out-Null
  $third = Start-Process "$Bin\cdx-terminal.exe" -ArgumentList "`"$dirA`"" -PassThru; $started += $third
  Check "un second lancement avec un dossier s'arrête et ouvre un onglet dans le terminal déjà ouvert (2 sessions)" ($third.WaitForExit(10000) -and (Until { (Sessions) -eq 2 } 20000))
  $sidA = Js "window.__tabs[window.__tabs.length - 1].focus.sid"
  Check "l'onglet ouvert est actif et son shell démarre" (Until { (Screen $sidA) -match 'PS [A-Z]:\\' } 20000)
  Ctl "write (Get-Location).Path\r" | Out-Null
  Check "le shell de ce nouvel onglet est dans le dossier demandé (espaces et accents compris)" (Until { (Screen $sidA) -match [regex]::Escape($dirA) } 15000)
  Ctl "write exit\r" | Out-Null
  Check "« exit » ferme cet onglet (1 session)" (Until { (Sessions) -eq 1 } 15000)

  $exe = Join-Path $ExplorerBin 'cdx-explorer.exe'
  $fromExplorer = { param($dir) Ctl ("eval post({ cmd: 'fs.terminal', path: '" + $dir.Replace('\', '/') + "' })") 'explorer' | Out-Null }
  if (Test-Path $exe) {
    $explorer = Start-Process $exe -ArgumentList "`"$dirB`"" -PassThru; $started += $explorer
    Check "l'Explorateur s'ouvre (canal de test, page prête)" (Until { (Ctl 'state' 'explorer') -eq '1 fenêtre(s)' -and (Ctl 'eval typeof post' 'explorer') -like '*function*' } 30000)
    & $fromExplorer $dirB
    Check "« Ouvrir dans un terminal » (Explorateur) ouvre un onglet du terminal CDX déjà ouvert (2 sessions)" (Until { (Sessions) -eq 2 } 20000)
    $sidB = Js "window.__tabs[window.__tabs.length - 1].focus.sid"
    Ctl "write (Get-Location).Path\r" | Out-Null
    Check "son shell est dans le dossier de l'Explorateur" (Until { (Screen $sidB) -match [regex]::Escape($dirB) } 20000)
    Ctl "write exit\r" | Out-Null
    Check "« exit » ferme cet onglet (1 session)" (Until { (Sessions) -eq 1 } 15000)
  } else { "SAUTÉ   cdx-explorer.exe est introuvable dans $ExplorerBin : l'essai avec l'Explorateur n'est pas fait" }

  Ctl "write exit\r" | Out-Null
  Check "« exit » dans le dernier volet ferme la fenêtre et le programme s'arrête" ($app.WaitForExit(15000))

  # --- terminal fermé : l'Explorateur le relance sur le dossier (chemin enregistré dans apps.json) ---
  if (Test-Path $exe) {
    & $fromExplorer $dirA
    Check "terminal fermé : « Ouvrir dans un terminal » le relance, avec son premier shell" (Until { (Sessions) -eq 1 } 30000)
    $sidC = Js "window.__tabs[0].focus.sid"
    Ctl "write (Get-Location).Path\r" | Out-Null
    Check "et ce premier shell est dans le dossier demandé" (Until { (Screen $sidC) -match [regex]::Escape($dirA) } 20000)
    Ctl "write exit\r" | Out-Null
    Check "« exit » ferme la fenêtre relancée" (Until { $null -eq (Ctl 'state') } 20000)
  }
}
finally {
  foreach ($p in $started) { if ($p -and -not $p.HasExited) { try { $p.Kill($true) } catch { } } }
  Start-Sleep -Milliseconds 300
  Remove-Item $suite, $data, $dirA, $dirB -Recurse -Force -ErrorAction SilentlyContinue
}
