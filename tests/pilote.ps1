# Essai isolé de « Pilote » contre un faux Claude Code (tests\fake_claude.py, lancé à la place de claude.exe) : la conversation, le flux affiché, les outils de la suite
# par le serveur MCP cdx-pilote-mcp (lancé par le faux Claude comme le ferait le vrai), le navigateur CDX piloté (ouvert, lu), les accords (automatiques dans le dossier
# des projets, demandés ailleurs, refus), la question à choix, la commande d'un programme, la création d'un projet (dossier + fiche dans un coffre jetable), la reprise
# après redémarrage (--resume), l'arrêt. Rien ne touche au vrai Claude Code, au vrai coffre ni aux vrais programmes (préfixe de tubes, dossiers sous %TEMP%).
#   pwsh -File tests\pilote.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"; $port = 8771; $gmailPort = 8772
$suite = Join-Path $env:TEMP "cdx-pl-suite-$id"; $data = Join-Path $env:TEMP "cdx-pl-data-$id"; $vault = Join-Path $env:TEMP "cdx-pl-coffre-$id"; $dev = Join-Path $env:TEMP "cdx-pl-dev-$id"; $www = Join-Path $env:TEMP "cdx-pl-www-$id"
New-Item -ItemType Directory $suite, $data, $vault, $dev, $www -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxpl$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data; $env:CDX_NOTES_DIR = $vault; $env:CDX_PILOTE_ROOT = $dev; $env:CDX_NO_FOCUS = '1'
$env:CDX_CLAUDE_EXE = (Get-Command python).Source; $env:CDX_CLAUDE_ARGS = "-I $PSScriptRoot\fake_claude.py"
# le courrier : un faux Gmail (tests\fake_gmail.py) que Courrier, lancé par Pilote, atteindra par CDX_GOOGLE_API ; l'identifiant client pointe dessus
$env:CDX_GOOGLE_API = "http://127.0.0.1:$gmailPort"; $env:CDX_GOOGLE_NO_BROWSER = '1'
"{`"installed`":{`"client_id`":`"essai.apps.googleusercontent.com`",`"client_secret`":`"secret-essai`",`"auth_uri`":`"http://127.0.0.1:$gmailPort/auth`",`"token_uri`":`"http://127.0.0.1:$gmailPort/token`",`"redirect_uris`":[`"http://localhost`"]}}" | Set-Content "$suite\google-client.json" -Encoding utf8
'<!doctype html><html><head><title>Page d essai</title></head><body><h1>Bonjour Pilote</h1><p>Ceci est la page lue par le faux Claude.</p><a href="/deux.html">Suite</a></body></html>' | Set-Content "$www\index.html" -Encoding utf8
'<!doctype html><html><head><title>Deux</title></head><body><p>Deuxieme page.</p></body></html>' | Set-Content "$www\deux.html" -Encoding utf8
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}pilote.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const ') { $code = "(() => { $code })()" } $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function St { Ctl 'state' | ConvertFrom-Json }
function Items { Ctl 'items' | ConvertFrom-Json }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function Open-Pilote { $p = Start-Process "$Bin\cdx-pilote.exe" -ArgumentList '--theme', 'rouge' -PassThru; Until { (Ctl 'state') -ne $null } 15000 | Out-Null; $p }
function Send([string]$text) { $j = @{ cmd = 'pl.send'; text = $text } | ConvertTo-Json -Compress; Ctl "cmd $j" | Out-Null }
function WaitIdle([int]$ms = 30000) { Until { $s = St; $s.current -and -not ($s.conversations | Where-Object { $_.id -eq $s.current }).busy } $ms }
function LastAssistant { ($(Items) | Where-Object { $_.kind -eq 'assistant' } | Select-Object -Last 1).text }
$started = @(); $launchedBefore = @()
try {
  $srv = Start-Process python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $www -PassThru -WindowStyle Hidden; $started += $srv
  $gm = Start-Process python -ArgumentList '-I', "$PSScriptRoot\fake_gmail.py", $gmailPort -PassThru -WindowStyle Hidden; $started += $gm; Start-Sleep 1

  "1. ouverture et première conversation"
  $app = Open-Pilote; $started += $app
  $s = St
  Check "au départ : Claude (le faux) trouvé, aucune conversation, dossiers de travail d'essai" ($s.claude -and $s.conversations.Count -eq 0 -and $s.root -eq $dev -and $s.vault -eq $vault)
  Check "la page montre l'accueil et le bouton Nouvelle conversation" (Until { (Js "!!document.getElementById('go')") -eq 'true' } 8000)
  Ctl 'cmd {"cmd":"pl.new"}' | Out-Null
  Check "nouvelle conversation : courante, vide" ((Until { (St).conversations.Count -eq 1 } 5000) -and (St).current -eq (St).conversations[0].id)

  "2. le flux et les outils MCP"
  Send 'outils'
  Check "la réponse arrive en morceaux puis est rangée ; le faux Claude a vu les deux serveurs MCP et leurs outils (suite et notes)" ((WaitIdle) -and ((LastAssistant) -match 'cdx-suite : .*browser_open.*project_folder') -and ((LastAssistant) -match 'cdx-notes : .*projects_create'))
  Check "la page affiche le message de Valentin et la réponse rendue en Markdown" (((Js "document.querySelectorAll('.m.u').length") -eq 1) -and ((Js "document.querySelector('.m.a') ? document.querySelector('.m.a').textContent : ''") -match 'cdx-suite'))
  Check "le quota de l'abonnement est affiché (37 % des 5 h)" (Until { (Js "document.getElementById('st3').textContent") -match '37 %' } 5000)
  Check "le processus du faux Claude reste vivant entre deux messages, la session est notée" (((St).conversations[0].alive) -and (Test-Path "$data\fake-claude-$((St).conversations[0].session).txt"))

  "3. le navigateur CDX piloté"
  Send "ouvre http://127.0.0.1:$port/index.html"
  Check "browser_open lance le navigateur d'essai, attend la page, browser_read la lit : le faux Claude répond avec le titre et le texte" ((WaitIdle 60000) -and ((LastAssistant) -match 'Page d.essai' -and (LastAssistant) -match 'Bonjour Pilote'))
  $it = Items
  Check "le fil montre les deux appels d'outil (Suite · browser_open, Suite · browser_read) réussis" ((($it | Where-Object { $_.kind -eq 'tool' -and $_.name -eq 'Suite · browser_open' -and $_.ok }).Count -ge 1) -and (($it | Where-Object { $_.kind -eq 'tool' -and $_.name -eq 'Suite · browser_read' -and $_.ok }).Count -ge 1))
  Check "le navigateur lancé par Pilote est noté (pour le fermer ici)" ((St).launched.Count -ge 1)
  Check "le volet des actions liste les appels" ((Js "document.querySelectorAll('#jr .tl').length") -ge 2)

  "4. les accords"
  Send "écris $dev\Zeta\notes.txt : bonjour"
  Check "écriture dans le dossier des projets : accord automatique (niveau libre), fichier écrit, noté « auto »" ((WaitIdle) -and (Test-Path "$dev\Zeta\notes.txt") -and (((Items) | Where-Object { $_.kind -eq 'ask' -and $_.decision -eq 'auto' }).Count -ge 1))
  $outside = Join-Path $env:TEMP "cdx-pl-hors-$id.txt"
  Send "écris $outside : interdit"
  Check "écriture hors des dossiers de travail : demande affichée dans le volet des actions, Claude attend" ((Until { (St).pending.Count -eq 1 } 8000) -and (Until { (Js "document.querySelectorAll('#asks .ask').length") -eq 1 } 5000) -and ((Js "document.querySelector('#asks .ask .t').textContent") -eq 'Write') -and ((Js "document.querySelector('#asks .ask .d').textContent") -match 'cdx-pl-hors'))
  $rid = (St).pending[0].id
  Ctl ("cmd " + (@{ cmd = 'pl.answer'; id = $rid; allow = $false; message = 'Pas là.' } | ConvertTo-Json -Compress)) | Out-Null
  Check "refus : rien d'écrit, Claude reçoit le motif, la demande est close" ((WaitIdle) -and -not (Test-Path $outside) -and ((LastAssistant) -match 'Refusé : Pas là') -and (St).pending.Count -eq 0 -and (((Items) | Where-Object { $_.kind -eq 'ask' -and $_.decision -eq 'refusé' }).Count -eq 1))
  Send "écris $outside : permis"
  Until { (St).pending.Count -eq 1 } 8000 | Out-Null
  $rid = (St).pending[0].id
  Ctl ("cmd " + (@{ cmd = 'pl.answer'; id = $rid; allow = $true } | ConvertTo-Json -Compress)) | Out-Null
  Check "accord : le fichier est écrit" ((WaitIdle) -and (Test-Path $outside) -and ((Get-Content $outside -Raw) -match 'permis'))
  Ctl 'cmd {"cmd":"pl.level","value":"prudent"}' | Out-Null
  Send "écris $dev\Zeta\deux.txt : prudent"
  Check "niveau prudent : l'écriture dans le dossier des projets est demandée" (Until { (St).pending.Count -eq 1 } 8000)
  Ctl ("cmd " + (@{ cmd = 'pl.answer'; id = (St).pending[0].id; allow = $true } | ConvertTo-Json -Compress)) | Out-Null
  WaitIdle | Out-Null
  Ctl 'cmd {"cmd":"pl.level","value":"libre"}' | Out-Null

  "5. une question à choix"
  Send 'question'
  Check "AskUserQuestion : la question et ses options s'affichent dans le volet" ((Until { (Js "document.querySelectorAll('#asks .ask input[type=radio]').length") -ge 2 } 8000) -and ((Js "document.querySelector('#asks .ask .q').textContent") -eq 'Quel langage ?'))
  Js "document.querySelectorAll('#asks .ask input[type=radio]')[1].checked = true; document.querySelector('#asks [data-reply]').click()" | Out-Null
  Check "la réponse cochée (C#) revient au faux Claude" ((WaitIdle) -and ((LastAssistant) -match 'Tu as choisi : C#'))

  "6. commander un programme (app_command, demandé puis permis)"
  Send "commande browser {`"cmd`":`"br.newtab`",`"url`":`"http://127.0.0.1:$port/deux.html`"}"
  Check "app_command n'est pas pré-autorisé : demande d'accord « Suite · app_command » avec la commande" ((Until { (St).pending.Count -eq 1 } 8000) -and ((St).pending[0].title -eq 'Suite · app_command'))
  Ctl ("cmd " + (@{ cmd = 'pl.answer'; id = (St).pending[0].id; allow = $true } | ConvertTo-Json -Compress)) | Out-Null
  WaitIdle | Out-Null
  function BrowserPilot([string]$line) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}browser.pilot", [System.IO.Pipes.PipeDirection]::InOut)
    try { $c.Connect(5000) } catch { return $null }
    $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
    $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
  }
  Check "le navigateur a reçu br.newtab : deux onglets" (Until { (BrowserPilot 'state' | ConvertFrom-Json).tabs.Count -ge 2 } 8000)
  Check "le canal de pilotage refuse eval (réservé au canal de test)" ((BrowserPilot 'eval 1+1') -match 'refusée')
  Check "page.find numérote les éléments, page.text lit le texte" (((BrowserPilot 'page.find' | ConvertFrom-Json) -match 'count') -and ((BrowserPilot 'page.text 100') -match 'Deuxi'))

  "7. créer un projet (dossier + git + fiche dans le coffre)"
  Send 'projet Omega'
  Check "project_folder crée le dossier avec git, projects_create la fiche dans le coffre d'essai" ((WaitIdle 40000) -and (Test-Path "$dev\Omega\.git") -and (Test-Path "$vault\Projets\Omega.md") -and ((Get-Content "$vault\Projets\Omega.md" -Raw) -match 'type: projet'))
  Check "la fiche porte le chemin du dossier" ((Get-Content "$vault\Projets\Omega.md" -Raw) -match [regex]::Escape("$dev\Omega"))

  "7b. lire le courrier (Courrier lancé par Pilote, compte relié au faux Gmail)"
  Send 'courrier facture'
  Check "compte non relié : Pilote lance Courrier, l'outil le dit, le faux Claude le répète" ((WaitIdle 40000) -and ((LastAssistant) -match 'non relié') -and (Until { (Get-Process cdx-mail -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*bin-test2*" -or $_.Path -like "*$Bin*" }).Count -ge 1 } 5000))
  function MailCtl([string]$line) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}mail.ctl", [System.IO.Pipes.PipeDirection]::InOut)
    try { $c.Connect(5000) } catch { return $null }
    $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
    $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
  }
  Until { (MailCtl 'state') -ne $null } 10000 | Out-Null
  MailCtl 'cmd {"cmd":"ml.connect"}' | Out-Null
  $url = ''; Until { $script:url = MailCtl 'google-url'; $script:url -like 'http://127.0.0.1:*' } 8000 | Out-Null
  try { Invoke-WebRequest $url -UseBasicParsing | Out-Null } catch { "   retour : $($_.Exception.Message)" }
  Check "le compte est relié au faux Gmail dans Courrier" (Until { (MailCtl 'state' | ConvertFrom-Json).connected } 10000)
  Send 'courrier facture'
  Check "mail_search puis mail_read : le faux Claude rend l'objet « Facture octobre » et le texte du dernier message (« Merci, reçu »)" ((WaitIdle 40000) -and ((LastAssistant) -match 'Facture octobre') -and ((LastAssistant) -match 'Merci, re') -and ((LastAssistant) -match '2 message'))
  Check "les deux appels sont des outils de lecture : aucune demande d'accord" ((($(Items) | Where-Object { $_.kind -eq 'tool' -and $_.name -eq 'Suite · mail_read' -and $_.ok }).Count -ge 1) -and ((($(Items) | Where-Object { $_.kind -eq 'ask' -and $_.title -match 'mail_' }).Count) -eq 0))
  Check "lire un fil ne le marque pas lu (le faux Gmail garde UNREAD sur m1)" (((Invoke-WebRequest "http://127.0.0.1:$gmailPort/_state" -UseBasicParsing).Content | ConvertFrom-Json).messages.m1.labels -contains 'UNREAD')

  "7c. la mémoire : des notes du coffre, lues au départ, écrites par Claude"
  Check "les quatre notes Pilote/ ont été créées dans le coffre au premier lancement de Claude" ((Test-Path "$vault\Pilote\Profil.md") -and (Test-Path "$vault\Pilote\Décisions.md") -and (Test-Path "$vault\Pilote\Projets.md") -and (Test-Path "$vault\Pilote\Journal.md"))
  $promptFile = "$data\fake-claude-prompt-$((St).conversations[0].session).txt"
  Check "la consigne reçue par Claude contient la règle de mémoire et le contenu des notes ; la consigne est relue à chaque lancement (snapshot off)" ((Test-Path $promptFile) -and ((Get-Content $promptFile -Raw) -match 'TA MÉMOIRE') -and ((Get-Content $promptFile -Raw) -match '=== Pilote/Journal.md ===') -and ((Get-Content $promptFile -Raw) -match '--system-prompt-snapshot off'))
  Send 'retiens Valentin préfère le thé'
  Check "Claude écrit dans Pilote/Profil.md par les outils de notes, sans demande d'accord" ((WaitIdle) -and ((Get-Content "$vault\Pilote\Profil.md" -Raw) -match 'préfère le thé') -and ((($(Items) | Where-Object { $_.kind -eq 'ask' -and $_.title -match 'notes_append' -and $_.decision -ne 'auto' }).Count) -eq 0))

  "8. redémarrage : la conversation et la session sont retrouvées (--resume)"
  $conv = (St).conversations[0]; $turnsFile = "$data\fake-claude-$($conv.session).txt"
  $launchedBefore = @((St).launched)   # le nouveau Pilote ne connaîtra pas le navigateur lancé par l'ancien : on le note pour le fermer à la fin
  $before = (Get-Content $turnsFile).Count
  $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(5000)) { $app.Kill() }; Start-Sleep 1
  $app = Open-Pilote; $started += $app
  $s = St
  Check "après redémarrage : la conversation est listée, courante, avec son fil" (($s.conversations.Count -eq 1) -and ($s.current -eq $conv.id) -and ($s.conversations[0].items -ge 10) -and -not $s.conversations[0].alive)
  Check "la page montre le fil retrouvé" (Until { (Js "document.querySelectorAll('.m.u').length") -ge 7 } 8000)
  Send 'encore'
  Check "le message suivant relance le faux Claude avec --resume sur la même session : un tour de plus dans son journal" ((WaitIdle) -and ((LastAssistant) -eq 'Tu as dit : encore') -and ((Get-Content $turnsFile).Count -eq $before + 1))
  Check "la session reprise reçoit la mémoire du jour (le fait retenu est dans sa consigne)" ((Get-Content "$data\fake-claude-prompt-$($conv.session).txt" -Raw) -match 'préfère le thé')

  "9. arrêter Claude en plein tour"
  Send 'attends'
  Start-Sleep 1
  Check "pendant le tour : occupé" ((St).conversations[0].busy)
  Ctl 'cmd {"cmd":"pl.stop"}' | Out-Null
  Check "pl.stop : le tour s'arrête, plus occupé, une note dans le fil" ((WaitIdle 10000) -and (((Items) | Where-Object { $_.kind -eq 'note' }).Count -ge 1))
  Send 'après'
  Check "et ça repart (nouveau processus, même session)" ((WaitIdle) -and ((LastAssistant) -eq 'Tu as dit : après'))

  "10. deuxième conversation et suppression"
  Ctl 'cmd {"cmd":"pl.new"}' | Out-Null
  Send 'bonjour'
  Check "deuxième conversation à part : son propre fil, la première garde le sien" ((WaitIdle) -and ((St).conversations.Count -eq 2) -and ((Items).Count -eq 2))
  $del = (St).current
  Ctl ("cmd " + (@{ cmd = 'pl.delete'; id = $del } | ConvertTo-Json -Compress)) | Out-Null
  Check "suppression : il n'en reste qu'une, son fichier a disparu" ((Until { (St).conversations.Count -eq 1 } 5000) -and -not (Test-Path "$data\pilote\$del.json"))
}
finally {
  foreach ($pid2 in @((St).launched) + @($launchedBefore)) { if ($pid2) { try { Stop-Process -Id $pid2 -Force -ErrorAction SilentlyContinue } catch { } } }
  foreach ($p in $started) { try { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } } catch { } }
  Start-Sleep 1
  Get-Process python -ErrorAction SilentlyContinue | Where-Object { try { $_.CommandLine -match 'fake_claude.py' -and $_.CommandLine -match [regex]::Escape($data) } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue
  "journal : $data\journal-pilote.log"
}
