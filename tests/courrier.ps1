# Essai isolé de « Courrier » contre un faux Gmail (tests\fake_gmail.py) : liaison du compte avec la portée gmail.modify, libellés et compteurs, liste des fils,
# lecture d'un fil (marqué lu, HTML dans un cadre isolé, images distantes bloquées puis autorisées, pièce jointe enregistrée), réponse (fil, In-Reply-To, citation),
# nouveau message avec pièce jointe (multipart, objet accentué encodé), archiver, favori, non lu, corbeille, libellé, recherche, nouveau courrier relevé, redémarrage.
# Rien ne touche au vrai compte Google ni aux vrais réglages ; aucune entrée simulée.
#   pwsh -File tests\courrier.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"; $port = 8769
$suite = Join-Path $env:TEMP "cdx-ml-suite-$id"; $data = Join-Path $env:TEMP "cdx-ml-data-$id"; $dl = Join-Path $env:TEMP "cdx-ml-dl-$id"
New-Item -ItemType Directory $suite, $data, $dl -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxml$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data; $env:CDX_NO_FOCUS = '1'; $env:CDX_GOOGLE_API = "http://127.0.0.1:$port"; $env:CDX_GOOGLE_NO_BROWSER = '1'
"{`"installed`":{`"client_id`":`"essai.apps.googleusercontent.com`",`"client_secret`":`"secret-essai`",`"auth_uri`":`"http://127.0.0.1:$port/auth`",`"token_uri`":`"http://127.0.0.1:$port/token`",`"redirect_uris`":[`"http://localhost`"]}}" | Set-Content "$suite\google-client.json" -Encoding utf8
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}mail.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const ') { $code = "(() => { $code })()" } $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function St { Ctl 'state' | ConvertFrom-Json }
function FakeRaw { (Invoke-WebRequest "http://127.0.0.1:$port/_state" -UseBasicParsing).Content }
function Fake { FakeRaw | ConvertFrom-Json }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function Open-Mail { $p = Start-Process "$Bin\cdx-mail.exe" -ArgumentList '--theme', 'rouge' -PassThru; Until { (Ctl 'state') -ne $null } 15000 | Out-Null; $p }
function Labels($m) { (Fake).messages.$m.labels }
$started = @()
try {
  $srv = Start-Process python -ArgumentList '-I', "$PSScriptRoot\fake_gmail.py", $port -PassThru -WindowStyle Hidden; $started += $srv; Start-Sleep 1
  Check "le faux Gmail répond" ((Fake).messages.m1.threadId -eq 't1')

  "1. liaison du compte (portée courrier)"
  $app = Open-Mail; $started += $app
  Check "au départ : non relié, la page propose de relier" (-not (St).connected -and (Until { (Js "!!document.getElementById('connect')") -eq 'true' } 8000))
  Ctl 'cmd {"cmd":"ml.connect"}' | Out-Null
  $url = ''; Until { $script:url = Ctl 'google-url'; $script:url -like 'http://127.0.0.1:*' } 8000 | Out-Null
  Check "la page de consentement demande la portée gmail.modify" ($url -match 'gmail\.modify')
  try { Invoke-WebRequest $url -UseBasicParsing | Out-Null } catch { "   retour : $($_.Exception.Message)" }
  Check "compte relié (essai@example.org)" ((Until { (St).connected } 10000) -and (St).email -eq 'essai@example.org')

  "2. libellés et liste"
  Check "les libellés : Boîte de réception avec 2 fils non lus, Favoris, Envoyés, Corbeille, puis le libellé « Factures » ; les catégories cachées" ((Until { (Js "document.querySelectorAll('.lbl').length") -ge 6 } 10000) -and ((Js 'document.querySelector(".lbl[data-l=INBOX] .u").textContent') -eq '2') -and ((Js 'document.querySelector(".lbl[data-l=Label_1] .n").textContent') -eq 'Factures') -and ((Js "document.body.innerText") -notmatch 'CATEGORY'))
  Check "la boîte de réception liste 3 fils, du plus récent au plus ancien, les non lus marqués, le favori étoilé" ((Until { (Js "document.querySelectorAll('.th').length") -eq 3 } 10000) -and ((Js "[...document.querySelectorAll('.th')].map(t => t.dataset.t).join()") -eq 't1,t2,t3') -and ((Js "document.querySelectorAll('.th.unread').length") -eq 2) -and ((Js 'document.querySelector(".th[data-t=t2] .st").classList.contains("on")') -eq 'true'))
  Check "le fil t1 montre ses deux expéditeurs et (2)" ((Js 'document.querySelector(".th[data-t=t1] .f").textContent') -match 'Alice, Bob.*\(2\)')
  Check "le cache est écrit" (Test-Path "$data\courrier-cache.json")

  "3. lecture d'un fil"
  Ctl 'cmd {"cmd":"ml.open","id":"t1"}' | Out-Null
  Check "le fil s'ouvre : deux messages, l'objet en tête, le HTML dans un cadre isolé sans script, le texte brut en <pre>" ((Until { (Js "document.querySelectorAll('.msg').length") -eq 2 } 10000) -and ((Js "document.querySelector('.vh .subj').textContent") -match 'Facture octobre') -and ((Js "document.querySelector('.msg iframe').getAttribute('sandbox')") -notmatch 'allow-scripts') -and ((Js "document.querySelector('.msg iframe').getAttribute('srcdoc')") -match 'facture'))
  Check "ouvrir le fil le marque lu chez Google et dans la liste" ((Until { -not ((Labels 'm1') -contains 'UNREAD') -and -not ((Labels 'm2') -contains 'UNREAD') } 8000) -and (Until { (Js 'document.querySelector(".th[data-t=t1]").classList.contains("unread")') -eq 'false' } 5000))
  Check "les images distantes sont bloquées (CSP sans http), avec un bouton pour les afficher" (((Js "document.querySelector('.msg iframe').getAttribute('srcdoc')") -match "img-src data: cid:") -and ((Js "!!document.querySelector('[data-img]')") -eq 'true'))
  Js "document.querySelector('[data-img]').click()" | Out-Null
  Check "« Afficher les images » : la CSP autorise les images du premier message" ((Until { (Js "document.querySelector('.msg iframe').getAttribute('srcdoc')") -match "img-src \* data: cid:" } 5000))
  Check "la pièce jointe facture.pdf est proposée avec sa taille" ((Js "document.querySelector('[data-att]').textContent") -match 'facture\.pdf')
  Ctl ('cmd {"cmd":"ml.attach","msg":"m1","id":"a1","name":"facture.pdf","dir":' + ($dl | ConvertTo-Json) + '}') | Out-Null
  Check "enregistrer la pièce jointe : fichier écrit avec son contenu, la barre d'état le dit" ((Until { Test-Path "$dl\facture.pdf" } 8000) -and ((Get-Content "$dl\facture.pdf" -Raw) -match '^%PDF') -and ((Js "document.getElementById('st1').textContent") -match 'Enregistr'))

  "4. répondre"
  Js "compose('reply')" | Out-Null
  Check "la fenêtre de réponse : au dernier expéditeur (Bob), objet « Re: », le message cité" (((Js "document.getElementById('cto').value") -match 'bob@example.org') -and ((Js "document.getElementById('csub').value") -eq 'Re: Facture octobre') -and ((Js "document.getElementById('ctext').value") -match '> Merci, reçu'))
  Js "document.getElementById('ctext').value = 'Parfait, merci.' + document.getElementById('ctext').value; sendCompose()" | Out-Null
  Check "envoyé : le faux Gmail reçoit un message RFC 2822 dans le fil (In-Reply-To, References), à Bob, et la fenêtre se ferme" ((Until { @((Fake).sent).Count -ge 1 } 8000) -and ((Fake).sent[0].inReplyTo -eq '<m2@example.org>') -and ((Fake).sent[0].references -match '<m1@example.org>.*<m2@example.org>') -and ((Fake).sent[0].to -match 'bob@example.org') -and ((Fake).sent[0].text -match 'Parfait, merci') -and ((Fake).sent[0].threadId -eq 't1') -and (Until { (Js "!!document.querySelector('.veil')") -eq 'false' } 5000))
  Js "compose('replyAll')" | Out-Null
  Check "répondre à tous : Alice en copie, pas moi" (((Js "document.getElementById('ccc').value") -match 'alice@example.org') -and ((Js "document.getElementById('ccc').value") -notmatch 'essai@example.org'))
  Js "closeVeil()" | Out-Null

  "5. nouveau message avec pièce jointe et objet accentué"
  "contenu de la pièce jointe" | Set-Content "$dl\piece.txt" -Encoding utf8
  Js "compose('new')" | Out-Null
  Js ("document.getElementById('cto').value = 'dest@example.org'; document.getElementById('csub').value = 'Objet accentué : été'; document.getElementById('ctext').value = 'Bonjour'; attachFiles([{ path: " + ("$dl\piece.txt" | ConvertTo-Json) + ", name: 'piece.txt', size: 28 }]); sendCompose()") | Out-Null
  Check "envoyé : multipart avec la pièce jointe (Content-Disposition), objet encodé RFC 2047 et relu « été »" ((Until { @((Fake).sent).Count -ge 2 } 8000) -and ((Fake).sent[1].hasAttachment) -and ((Fake).sent[1].contentType -match 'multipart/mixed') -and ((Fake).sent[1].subject -match '^=\?UTF-8\?B\?') -and ((Fake).sent[1].subjectDecoded -eq 'Objet accentué : été') -and ((Fake).sent[1].raw -match 'filename="piece.txt"'))
  Check "la liste Envoyés montre le nouveau message" ((Ctl 'cmd {"cmd":"ml.list","label":"SENT"}') -eq 'ok' -and (Until { (Js "document.body.innerText") -match 'Objet accentué' } 8000))

  "6. actions sur les fils"
  Ctl 'cmd {"cmd":"ml.list","label":"INBOX"}' | Out-Null; Until { (Js "document.querySelectorAll('.th').length") -eq 3 } 8000 | Out-Null
  Ctl 'cmd {"cmd":"ml.act","id":"t2","what":"archive"}' | Out-Null
  Check "archiver t2 : plus dans la boîte de réception (Google et liste)" ((Until { -not ((Labels 'm3') -contains 'INBOX') } 8000) -and (Until { (Js "document.querySelectorAll('.th').length") -eq 2 } 8000))
  Ctl 'cmd {"cmd":"ml.act","id":"t3","what":"star"}' | Out-Null
  Check "favori t3" (Until { (Labels 'm4') -contains 'STARRED' } 8000)
  Ctl 'cmd {"cmd":"ml.act","id":"t1","what":"unread"}' | Out-Null
  Check "non lu t1" (Until { (Labels 'm1') -contains 'UNREAD' } 8000)
  Ctl 'cmd {"cmd":"ml.list","label":"Label_1"}' | Out-Null
  Check "le libellé Factures liste le fil t3" ((Until { (Js "[...document.querySelectorAll('.th')].map(t => t.dataset.t).join()") -eq 't3' } 8000))
  Ctl 'cmd {"cmd":"ml.list","label":"INBOX","q":"octobre"}' | Out-Null
  Check "recherche « octobre » : un seul fil" ((Until { (Js "[...document.querySelectorAll('.th')].map(t => t.dataset.t).join()") -eq 't1' } 8000))
  Ctl 'cmd {"cmd":"ml.list","label":"INBOX"}' | Out-Null; Until { (Js "document.querySelectorAll('.th').length") -eq 2 } 8000 | Out-Null
  Ctl 'cmd {"cmd":"ml.act","id":"t3","what":"trash"}' | Out-Null
  Check "corbeille t3 : chez Google et retiré de la liste" ((Until { (Labels 'm4') -contains 'TRASH' } 8000) -and (Until { (Js "document.querySelectorAll('.th').length") -eq 1 } 8000))

  "7. nouveau courrier relevé"
  $u0 = (St).unread
  Invoke-WebRequest "http://127.0.0.1:$port/_inject" -Method Post -UseBasicParsing | Out-Null
  Check "un courrier arrivé est vu au relevé : compteur à jour, la liste le montre" (((Ctl 'poll') -eq 'ok') -and (Until { (St).unread -gt $u0 } 5000) -and (Until { (Js "document.body.innerText") -match 'Courrier injecté' } 8000))

  "8. redémarrage et oubli"
  $app.Kill(); Start-Sleep 1
  $app = Open-Mail; $started += $app
  Check "après redémarrage : relié sans consentement, la boîte de réception revient" ((St).connected -and (Until { (Js "document.querySelectorAll('.th').length") -ge 1 } 10000))
  Ctl 'cmd {"cmd":"ml.disconnect"}' | Out-Null
  Check "oublier le compte : page « relier », jeton effacé" ((Until { -not (St).connected } 8000) -and -not (Test-Path "$suite\google-token.bin"))
  Check "le programme n'a pas planté" (-not $app.HasExited)
  "journal (fin) :"; Get-Content "$data\journal-mail.log" -Tail 10 | ForEach-Object { '   ' + $_ }
}
finally {
  foreach ($p in $started) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch { } } }
  Start-Sleep 1
  Remove-Item $suite, $data, $dl -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR, Env:CDX_NO_FOCUS, Env:CDX_GOOGLE_API, Env:CDX_GOOGLE_NO_BROWSER -ErrorAction SilentlyContinue
}
