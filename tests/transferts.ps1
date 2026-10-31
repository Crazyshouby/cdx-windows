# Essai isolé de la fenêtre « Transferts » : (A) la fenêtre seule, avec des messages de producteurs simulés par le bus ; (B) un vrai Explorateur qui copie des milliers de fichiers
# (pause, reprise, annulation, fermeture de sa dernière fenêtre pendant la copie) ; (C) un vrai téléchargement du navigateur, depuis un petit serveur local volontairement lent.
# Tout est isolé : tubes à préfixe, dossier de réglages et de données jetables, dossiers jetables dans %TEMP% ; seul le téléchargement du navigateur écrit dans le dossier Téléchargements
# (un nom unique, supprimé ensuite). Aucune frappe ni clic sur la session : la page est pilotée par le canal de test.
#   pwsh -File tests\transferts.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-tr-suite-$id"; $data = Join-Path $env:TEMP "cdx-tr-data-$id"; $work = Join-Path $env:TEMP "cdx-tr-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxtr$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
# apps.json : où lancer chaque programme (la fenêtre vient d'elle-même à un transfert qui dure)
([ordered]@{ transfers = "$Bin\cdx-transfers.exe"; explorer = "$Bin\cdx-explorer.exe"; browser = "$Bin\cdx-browser.exe" } | ConvertTo-Json -Compress) | Set-Content "$suite\apps.json" -Encoding utf8
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$app, [string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "$prefix$app.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(4000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$app, [string]$code) { if ($code -match 'const') { $code = "(() => { $code })()" }
  $r = Ctl $app "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
# écrit une ligne de bus (un producteur simulé)
function BusSend([string]$app, [string]$type, [string]$from, $dataObj) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "$prefix$app", [System.IO.Pipes.PipeDirection]::Out)
  try { $c.Connect(3000) } catch { return $false }
  $b = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ type = $type; from = $from; data = $dataObj } | ConvertTo-Json -Compress -Depth 6) + "`n"); $c.Write($b, 0, $b.Length); $c.Flush(); $c.Dispose(); $true
}
function Entries { $r = Ctl 'transfers' 'entries'; if ($r) { @($r | ConvertFrom-Json) } else { @() } }
function Entry([string]$key) { Entries | Where-Object { $_.Key -eq $key } | Select-Object -First 1 }
function Click([string]$sel) { Js 'transfers' "const b = document.querySelector('$sel'); if (b) b.click(); return !!b" }
# met la tâche en pause (par le bus, comme le ferait la fenêtre) ; répété, car le message peut précéder la création de la tâche (une pause en double est sans effet)
function PauseJob([int]$n) { for ($i = 0; $i -lt 15; $i++) { BusSend 'explorer' 'xfer-ctl' 'transfers' @{ id = $n; action = 'pause' } | Out-Null; Start-Sleep -Milliseconds 120 } }
function Xfer($i, [string]$state, [long]$done, [long]$total, [hashtable]$more = @{}) {
  $d = @{ id = $i; kind = 'copy'; title = '3 éléments'; state = $state; from = 'Docs'; to = 'Archives'; done = $done; total = $total; speed = 50; items = 3; canPause = $true }
  foreach ($k in $more.Keys) { $d[$k] = $more[$k] }
  BusSend 'transfers' 'xfer' 'explorer' $d | Out-Null
}

$started = @(); $t0Run = Get-Date; $fakeExplorer = $null
try {
  # ======================= A. la fenêtre seule =======================
  "A. la fenêtre seule"
  $tr = Start-Process "$Bin\cdx-transfers.exe" -PassThru; $started += $tr
  Check "la fenêtre s'ouvre et la page est prête (liste vide)" ((Until { (Ctl 'transfers' 'state') -like '*fenêtre ouverte*' -and (Js 'transfers' "document.getElementById('rows').textContent") -like '*Aucun transfert*' } 25000))
  Start-Sleep -Milliseconds 1500   # (la page a eu le temps de dire « ready » : la fenêtre a déjà demandé l'état aux producteurs)
  # un producteur « explorer » simulé : il écoute pour recevoir les ordres de la fenêtre
  $fakeExplorer = New-Object System.IO.Pipes.NamedPipeServerStream("${prefix}explorer", [System.IO.Pipes.PipeDirection]::In, 4, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
  Xfer 1 'running' 100 1000
  Check "un transfert annoncé par le bus apparaît dans la liste de la fenêtre" (Until { (Entry 'explorer#1').State -eq 'running' } 6000)
  Check "la page montre une ligne : titre, 10 %, barre à 10 %, état de la source" ((Until { (Js 'transfers' "document.querySelectorAll('.tx').length") -eq '1' } 6000) -and ((Js 'transfers' "document.querySelector('.tx .stt').textContent") -eq '10 %') -and ((Js 'transfers' "document.querySelector('.tx .bar i').style.width") -eq '10%') -and ((Js 'transfers' "document.querySelector('.tx .ttl').textContent") -eq '3 éléments'))
  Xfer 1 'running' 500 1000
  Check "la progression suit (50 %)" (Until { (Js 'transfers' "document.querySelector('.tx .stt').textContent") -eq '50 %' } 6000)
  Check "la ligne de détail dit d'où et vers où : « Docs → Archives · 3 éléments »" ((Js 'transfers' "document.querySelector('.tx .l2').textContent") -eq 'Docs → Archives · 3 éléments')
  # messages invalides : refusés, la liste ne change pas
  BusSend 'transfers' 'xfer' 'explorer' @{ id = 7; kind = 'virus'; title = 'x'; state = 'running' } | Out-Null
  BusSend 'transfers' 'xfer' 'explorer' @{ id = 8; kind = 'copy'; title = 'x'; state = 'bizarre' } | Out-Null
  BusSend 'transfers' 'xfer' 'explorer' @{ id = -5; kind = 'copy'; title = 'x'; state = 'running' } | Out-Null
  BusSend 'transfers' 'xfer' 'nobody' @{ id = 9; kind = 'copy'; title = 'x'; state = 'running' } | Out-Null
  BusSend 'transfers' 'xfer' 'explorer' @{ id = 'a'; kind = 'copy'; title = 'x'; state = 'running' } | Out-Null
  Start-Sleep -Milliseconds 700
  Check "des messages mal formés (genre ou état inconnus, numéro négatif ou texte, émetteur inconnu) sont refusés : toujours un seul transfert" ((Entries).Count -eq 1)
  Xfer 3 'running' 5 10 @{ title = ('T' * 5000); from = ('F' * 5000) }
  Check "des textes énormes sont coupés (300 caractères au plus)" ((Until { (Entry 'explorer#3') -ne $null } 4000) -and ((Js 'transfers' "[...document.querySelectorAll('.tx .ttl')].map(x => x.textContent.length).sort((a, b) => b - a)[0]") -le 300))
  Xfer 3 'canceled' 5 10
  # les ordres de la page partent vers le producteur
  $wait = $fakeExplorer.WaitForConnectionAsync()
  Click '.tx[data-key="explorer#1"] [data-a=pause]' | Out-Null
  $got = $null; if ($wait.Wait(6000)) { $sr = New-Object System.IO.StreamReader($fakeExplorer); $got = $sr.ReadLine() | ConvertFrom-Json }
  Check "« Pause » envoie xfer-ctl { id: 1, action: pause } au programme qui fait le travail" ($got -and $got.type -eq 'xfer-ctl' -and $got.data.id -eq 1 -and $got.data.action -eq 'pause' -and $got.from -eq 'transfers')
  $fakeExplorer.Dispose(); $fakeExplorer = $null
  # fin du transfert : historique, copie du chemin
  Set-Content "$work\fini.txt" 'x'
  Xfer 1 'done' 1000 1000 @{ path = "$work\fini.txt" }
  Check "terminé : la ligne passe à « Terminé », barre pleine, boutons Ouvrir / Afficher / Retirer" ((Until { (Js 'transfers' "document.querySelector('.tx[data-key=\""explorer#1\""] .stt').textContent") -like 'Terminé*' } 6000) -and ((Js 'transfers' "document.querySelector('.tx[data-key=\""explorer#1\""] .bar i').style.width") -eq '100%') -and ((Js 'transfers' "[...document.querySelectorAll('.tx[data-key=\""explorer#1\""] .acts .btn')].map(b => b.textContent).join('|')") -eq 'Ouvrir|Afficher|Retirer'))
  Check "l'historique est écrit dans transferts.json (écriture atomique)" ((Until { Test-Path "$data\transferts.json" } 8000) -and -not (Test-Path "$data\transferts.json.tmp") -and ((Get-Content "$data\transferts.json" -Raw -Encoding utf8 | ConvertFrom-Json).items.title -contains '3 éléments'))
  Check "les compteurs de filtres : Tous, En cours, Terminés" ((Js 'transfers' "document.getElementById('chips').textContent") -match 'Tous\s*\d+\s*En cours\s*0\s*Terminés\s*\d+')
  # un producteur qui disparaît : ses transferts en cours sont déclarés interrompus
  Xfer 2 'running' 10 100
  Check "un transfert dont le programme ne donne plus signe de vie est déclaré « interrompu » (au bout de 12 s)" (Until { (Entry 'explorer#2').State -eq 'failed' } 25000)
  Check "la ligne dit pourquoi, en rouge, avec le bouton Retirer" ((Until { (Js 'transfers' "document.querySelector('.tx[data-key=\""explorer#2\""] .l3')?.textContent") -like 'Interrompu*' } 6000) -and ((Js 'transfers' "document.querySelector('.tx[data-key=\""explorer#2\""] .l3').classList.contains('err')") -eq 'true') -and ((Js 'transfers' "document.querySelectorAll('.tx[data-key=\""explorer#2\""] [data-a=remove]').length") -eq '1'))
  # retirer / effacer
  Click '.tx[data-key="explorer#2"] [data-a=remove]' | Out-Null
  Check "« Retirer » ôte la ligne (page et programme)" ((Until { $null -eq (Entry 'explorer#2') } 5000) -and ((Js 'transfers' "document.querySelectorAll('.tx[data-key=\""explorer#2\""]').length") -eq '0'))
  # redémarrage : l'historique revient, pas les transferts en cours
  Xfer 5 'running' 1 100
  Until { (Entry 'explorer#5') -ne $null } 4000 | Out-Null
  $tr.Kill(); Start-Sleep -Seconds 1
  $tr = Start-Process "$Bin\cdx-transfers.exe" -PassThru; $started += $tr
  Check "après un redémarrage de la fenêtre, l'historique des terminés revient (marqué « historique »), pas le transfert resté en cours" ((Until { (Ctl 'transfers' 'state') -like '*fenêtre ouverte*' } 25000) -and (Until { (Entries | Where-Object { $_.Hist -and $_.State -eq 'done' }).Count -ge 1 } 8000) -and -not (Entries | Where-Object { $_.State -eq 'running' }))
  Check "la page affiche l'historique relu (au moins une ligne)" (Until { (Js 'transfers' "document.querySelectorAll('.tx').length") -match '^[1-9]' } 8000)
  Js 'transfers' "document.querySelector('.tb-m').click(); const it = [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Effacer les terminés')); if (it) it.click(); return !!it" | Out-Null
  Check "« Effacer les terminés » vide la liste" ((Until { (Entries).Count -eq 0 } 5000) -and ((Js 'transfers' "document.getElementById('rows').textContent") -like '*Aucun transfert*'))
  $tr.Kill(); Start-Sleep -Seconds 1

  # ======================= B. un vrai Explorateur =======================
  "B. un vrai Explorateur qui copie"
  New-Item -ItemType Directory "$work\src\many", "$work\dst", "$work\dst2", "$work\dst3" -Force | Out-Null
  $buf = [byte[]](1..600 | ForEach-Object { $_ % 251 })
  for ($d = 1; $d -le 30; $d++) { $dd = "$work\src\many\d$d"; New-Item -ItemType Directory $dd | Out-Null; for ($f = 1; $f -le 1000; $f++) { [IO.File]::WriteAllBytes("$dd\f$f.bin", $buf) } }
  $wj = $work.Replace('\', '\\')
  $ex = Start-Process "$Bin\cdx-explorer.exe" -ArgumentList "`"$work`"" -PassThru; $started += $ex
  Check "l'Explorateur isolé s'ouvre" (Until { (Js 'explorer' "T().items.length") -ge 3 } 25000)
  Js 'explorer' "post({ cmd: 'fs.drop', paths: ['$wj\\src\\many'], dir: '$wj\\dst', move: false, tab: T().id })" | Out-Null
  # on met aussitôt en pause (par le bus, comme le ferait la fenêtre) : la tâche reste en cours, donc la fenêtre vient d'elle-même au bout de 1,5 s
  PauseJob 1
  Check "la fenêtre « Transferts » se lance toute seule quand un transfert dure (programme cdx-transfers démarré par l'Explorateur)" (Until { (Ctl 'transfers' 'state') -like '*fenêtre ouverte*' } 30000)
  $started += Get-Process cdx-transfers -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run }
  if (-not (Get-Process cdx-transfers -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run })) { '   (journal de l''Explorateur :)'; Get-Content "$data\journal-explorer.log" -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { '   ' + $_ } }
  Check "elle montre la copie « many » de l'Explorateur, en pause, avec son dossier d'origine et de destination" ((Until { (Entry 'explorer#1').State -eq 'paused' } 15000) -and ((Js 'transfers' "document.querySelector('.tx .l2').textContent") -like 'src → dst*'))
  $n1 = (Get-ChildItem "$work\dst\many" -Recurse -File -ErrorAction SilentlyContinue).Count; Start-Sleep -Milliseconds 1200; $n2 = (Get-ChildItem "$work\dst\many" -Recurse -File -ErrorAction SilentlyContinue).Count
  Check "en pause, la copie n'avance plus (le nombre de fichiers copiés est figé : $n1 / $n2)" ($n1 -eq $n2 -and $n2 -lt 30000)
  Check "la page propose « Reprendre » et « Annuler »" ((Js 'transfers' "[...document.querySelectorAll('.tx[data-key=\""explorer#1\""] .acts .btn')].map(b => b.textContent).join('|')") -eq 'Reprendre|Annuler')
  Click '.tx[data-key="explorer#1"] [data-a=resume]' | Out-Null
  Check "« Reprendre » (clic dans la fenêtre) relance la copie, qui se termine : 30 000 fichiers, l'original intact" ((Until { (Entry 'explorer#1').State -eq 'done' } 120000) -and ((Get-ChildItem "$work\dst\many" -Recurse -File).Count -eq 30000) -and ((Get-ChildItem "$work\src\many" -Recurse -File).Count -eq 30000))
  Check "le contenu est le même (empreinte d'un fichier au hasard)" ((Get-FileHash "$work\src\many\d17\f333.bin").Hash -eq (Get-FileHash "$work\dst\many\d17\f333.bin").Hash)
  Check "l'Explorateur a gardé le geste pour « Annuler » (le menu du fond le dit)" (Until { (Js 'explorer' "undoState.undo") -eq 'Copier' } 6000)
  # annulation
  Js 'explorer' "post({ cmd: 'fs.drop', paths: ['$wj\\src\\many'], dir: '$wj\\dst2', move: false, tab: T().id })" | Out-Null
  PauseJob 2
  Check "une deuxième copie apparaît dans la fenêtre" (Until { (Entry 'explorer#2').State -eq 'paused' } 20000)
  Click '.tx[data-key="explorer#2"] [data-a=cancel]' | Out-Null
  Check "« Annuler » : la tâche est annulée et ce qu'elle avait copié est retiré (dst2 vide), l'original est intact" ((Until { (Entry 'explorer#2').State -eq 'canceled' } 20000) -and (Until { -not (Test-Path "$work\dst2\many") } 20000) -and ((Get-ChildItem "$work\src\many" -Recurse -File).Count -eq 30000))
  Check "une copie annulée n'ajoute rien à la pile « Annuler »" ((Js 'explorer' "undoState.undo") -eq 'Copier')
  # fermer la dernière fenêtre de l'Explorateur pendant une copie
  Js 'explorer' "post({ cmd: 'fs.drop', paths: ['$wj\\src\\many'], dir: '$wj\\dst3', move: false, tab: T().id })" | Out-Null
  PauseJob 3
  Until { (Entry 'explorer#3').State -eq 'paused' } 20000 | Out-Null
  Js 'explorer' "document.getElementById('bx').click()" | Out-Null
  Start-Sleep -Seconds 4
  Check "fermer la dernière fenêtre de l'Explorateur en pleine copie ne l'arrête pas (le programme reste, sans fenêtre)" (-not $ex.HasExited)
  Click '.tx[data-key="explorer#3"] [data-a=resume]' | Out-Null
  Check "la copie se termine malgré tout (30 000 fichiers)" ((Until { (Entry 'explorer#3').State -eq 'done' } 120000) -and ((Get-ChildItem "$work\dst3\many" -Recurse -File).Count -eq 30000))
  Check "puis l'Explorateur s'arrête de lui-même, un instant après la dernière annonce" (Until { $ex.HasExited } 20000)

  # ======================= C. un vrai téléchargement du navigateur =======================
  "C. un vrai téléchargement du navigateur"
  $py = "$work\lent.py"
  @'
import http.server, sys, time
SIZE = 24 * 1024 * 1024
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        start = 0; rng = self.headers.get('Range')
        if rng and rng.startswith('bytes='):
            start = int(rng[6:].split('-')[0])
        self.send_response(206 if start else 200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Content-Disposition', 'attachment; filename="cdx-xfer-test.bin"')
        self.send_header('Accept-Ranges', 'bytes')
        self.send_header('Content-Length', str(SIZE - start))
        if start: self.send_header('Content-Range', 'bytes %d-%d/%d' % (start, SIZE - 1, SIZE))
        self.end_headers()
        try:
            sent = start
            while sent < SIZE:
                n = min(256 * 1024, SIZE - sent); self.wfile.write(b'\x07' * n); self.wfile.flush(); sent += n; time.sleep(0.12)
        except Exception: pass
http.server.ThreadingHTTPServer(('127.0.0.1', int(sys.argv[1])), H).serve_forever()
'@ | Set-Content $py -Encoding utf8
  $dest = Join-Path "$env:USERPROFILE\Downloads" 'cdx-xfer-test.bin'; Remove-Item $dest -Force -ErrorAction SilentlyContinue
  $srv = Start-Process python -ArgumentList '-I', $py, '8767' -PassThru -WindowStyle Hidden; $started += $srv
  Start-Sleep -Seconds 2
  $br = Start-Process "$Bin\cdx-browser.exe" -ArgumentList '--theme', 'rouge' -PassThru; $started += $br
  Check "le navigateur isolé s'ouvre" (Until { (Ctl 'browser' 'state') -like '{*' } 30000)
  Ctl 'browser' 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/cdx-xfer-test.bin"}' | Out-Null
  Check "le téléchargement apparaît dans la fenêtre « Transferts » (elle se lance d'elle-même), avec le site et le dossier" ((Until { (Ctl 'transfers' 'state') -like '*fenêtre ouverte*' } 40000) -and (Until { (Entries | Where-Object { $_.Kind -eq 'download' -and $_.Title -eq 'cdx-xfer-test.bin' }).Count -eq 1 } 20000) -and ((Js 'transfers' "document.querySelector('.tx[data-key^=\""browser#\""] .l2').textContent") -like '127.0.0.1 → Téléchargements*'))
  $key = (Entries | Where-Object { $_.Kind -eq 'download' } | Select-Object -First 1).Key
  Check "il avance : des octets reçus, un total de 24 Mo, une vitesse" (Until { $e = Entry $key; $e.Done -gt 0 -and $e.Total -eq 25165824 } 20000)
  Click ".tx[data-key=`"$key`"] [data-a=pause]" | Out-Null
  Check "« Pause » arrête le téléchargement (état « en pause » dans la fenêtre)" (Until { (Entry $key).State -eq 'paused' } 15000)
  $p1 = (Entry $key).Done; Start-Sleep -Milliseconds 1500; $p2 = (Entry $key).Done
  Check "en pause, rien ne progresse ($p1 / $p2 octets)" ($p1 -eq $p2)
  Click ".tx[data-key=`"$key`"] [data-a=resume]" | Out-Null
  Check "« Reprendre » : le téléchargement repart et se termine (24 Mo écrits dans Téléchargements)" ((Until { (Entry $key).State -eq 'done' } 60000) -and (Test-Path $dest) -and ((Get-Item $dest).Length -eq 25165824))
  Remove-Item $dest -Force -ErrorAction SilentlyContinue
  # un second téléchargement, annulé
  Ctl 'browser' 'cmd {"cmd":"br.go","url":"http://127.0.0.1:8767/cdx-xfer-test.bin"}' | Out-Null
  Check "un second téléchargement apparaît" (Until { (Entries | Where-Object { $_.Kind -eq 'download' -and $_.State -eq 'running' }).Count -eq 1 } 20000)
  $key2 = (Entries | Where-Object { $_.Kind -eq 'download' -and $_.State -eq 'running' } | Select-Object -First 1).Key
  Click ".tx[data-key=`"$key2`"] [data-a=cancel]" | Out-Null
  Check "« Annuler » : le téléchargement est marqué annulé" (Until { (Entry $key2).State -eq 'canceled' } 20000)
}
finally {
  if ($fakeExplorer) { $fakeExplorer.Dispose() }
  $started | ForEach-Object { try { if ($_ -and -not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process cdx-transfers -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force -ErrorAction SilentlyContinue   # (le programme est lancé par les producteurs : pas dans $started)
  Start-Sleep -Seconds 1
  Remove-Item (Join-Path "$env:USERPROFILE\Downloads" 'cdx-xfer-test.bin') -Force -ErrorAction SilentlyContinue
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR -ErrorAction SilentlyContinue
}
