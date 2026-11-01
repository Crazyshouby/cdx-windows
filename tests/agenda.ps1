# Essai isolé d'« Agenda » contre un faux Google (tests\fake_google.py) : la liaison du compte (OAuth, page de consentement locale qui rend le code, jeton gardé
# chiffré, rafraîchi), la lecture des agendas et des événements, la création, la modification, la suppression, les tâches ; puis le redémarrage sans consentement et l'oubli du compte.
# Rien ne touche au vrai compte Google ni aux vrais réglages (CDX_SUITE_DIR, CDX_DATA_DIR, CDX_GOOGLE_API) ; aucune entrée simulée.
#   pwsh -File tests\agenda.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"; $port = 8768
$suite = Join-Path $env:TEMP "cdx-ag-suite-$id"; $data = Join-Path $env:TEMP "cdx-ag-data-$id"
New-Item -ItemType Directory $suite, $data -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxag$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data; $env:CDX_NO_FOCUS = '1'; $env:CDX_GOOGLE_API = "http://127.0.0.1:$port"; $env:CDX_GOOGLE_NO_BROWSER = '1'
# un identifiant client qui pointe sur le faux serveur (la vraie console Google donne le même format)
"{`"installed`":{`"client_id`":`"essai.apps.googleusercontent.com`",`"client_secret`":`"secret-essai`",`"auth_uri`":`"http://127.0.0.1:$port/auth`",`"token_uri`":`"http://127.0.0.1:$port/token`",`"redirect_uris`":[`"http://localhost`"]}}" | Set-Content "$suite\google-client.json" -Encoding utf8
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}agenda.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const ') { $code = "(() => { $code })()" } $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function St { Ctl 'state' | ConvertFrom-Json }
function Fake { (Invoke-WebRequest "http://127.0.0.1:$port/_state" -UseBasicParsing).Content | ConvertFrom-Json }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function Open-Agenda { $p = Start-Process "$Bin\cdx-agenda.exe" -ArgumentList '--theme', 'rouge' -PassThru; Until { (Ctl 'state') -ne $null } 15000 | Out-Null; $p }
$started = @()
try {
  $srv = Start-Process python -ArgumentList '-I', "$PSScriptRoot\fake_google.py", $port -PassThru -WindowStyle Hidden; $started += $srv; Start-Sleep 1
  Check "le faux Google répond" ((Fake).calendars.Count -eq 2)

  "1. liaison du compte"
  $app = Open-Agenda; $started += $app
  $s = St
  Check "au départ : identifiant client vu, compte non relié, la page propose de relier" ($s.hasClient -and -not $s.connected -and (Until { (Js "!!document.getElementById('connect')") -eq 'true' } 8000))
  Ctl 'cmd {"cmd":"ag.connect"}' | Out-Null
  $url = ''; Until { $script:url = Ctl 'google-url'; $script:url -like 'http://127.0.0.1:*' } 8000 | Out-Null   # $script: : le bloc a sa propre portée
  Check "la page de consentement est demandée avec PKCE, hors ligne et les portées agenda + tâches" ($url -match 'code_challenge_method=S256' -and $url -match 'access_type=offline' -and $url -match 'auth%2Fcalendar' -and $url -match 'auth%2Ftasks' -and $url -match 'redirect_uri=http%3A%2F%2F127\.0\.0\.1%3A\d+')
  # l'essai joue le navigateur : il ouvre la page de consentement, dont la redirection (code + state) revient sur le port local du programme
  try { Invoke-WebRequest $url -UseBasicParsing | Out-Null } catch { "   retour : $($_.Exception.Message)" }
  Check "le code est échangé : compte relié, adresse lue (essai@example.org)" ((Until { (St).connected } 10000) -and (St).email -eq 'essai@example.org')
  Check "le jeton est gardé chiffré dans le dossier de la suite (pas de texte en clair)" ((Test-Path "$suite\google-token.bin") -and -not ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes("$suite\google-token.bin")) -match 'refresh-1'))

  "2. agendas et événements"
  Js "setMonth(2026, 11)" | Out-Null
  Check "le mois de novembre 2026 montre Dentiste (le 5, avec l'heure), Armistice (le 11) et Week-end sur deux jours (14 et 15)" ((Until { (Js "document.querySelectorAll('.ev').length") -ge 4 } 10000) -and ((Js '[...document.querySelectorAll(''[data-day=\"2026-11-05\"] .ev'')].map(e => e.textContent).join()') -match '14:30.*Dentiste') -and ((Js 'document.querySelector(''[data-day=\"2026-11-11\"] .ev'').textContent') -match 'Armistice') -and ((Js 'document.querySelector(''[data-day=\"2026-11-14\"] .ev'').textContent') -match 'Week-end') -and ((Js 'document.querySelector(''[data-day=\"2026-11-15\"] .ev'').textContent') -match 'Week-end') -and ((Js 'document.querySelector(''[data-day=\"2026-11-16\"] .ev'') ? ''oui'' : ''non''') -eq 'non'))
  Check "l'événement d'octobre n'est pas demandé ni montré" ((Js "document.body.innerText") -notmatch 'Ancien')
  Check "le cache est écrit (agenda-cache.json)" (Test-Path "$data\agenda-cache.json")
  Ctl 'cmd {"cmd":"ag.cal.toggle","id":"fetes@group.v.calendar.google.com","on":false}' | Out-Null
  Check "masquer l'agenda « Fêtes » : Armistice disparaît, le réglage est écrit" ((Until { (Js "document.body.innerText") -notmatch 'Armistice' } 8000) -and ((Get-Content "$data\agenda.json" -Raw) -match 'fetes@'))
  Ctl 'cmd {"cmd":"ag.cal.toggle","id":"fetes@group.v.calendar.google.com","on":true}' | Out-Null
  Until { (Js "document.body.innerText") -match 'Armistice' } 8000 | Out-Null

  "3. créer, modifier, supprimer un événement"
  Ctl 'cmd {"cmd":"ag.event.save","cal":"primary-id@group.calendar.google.com","title":"Réunion","allDay":false,"start":"2026-11-20T10:00","end":"2026-11-20T11:00","location":"Bureau","description":"Point du mois"}' | Out-Null
  Check "création : le faux Google reçoit l'événement avec des instants RFC 3339 (fuseau local) et la page le montre le 20" ((Until { (Fake).events.'primary-id@group.calendar.google.com'.summary -contains 'Réunion' } 8000) -and (((Invoke-WebRequest "http://127.0.0.1:$port/_state" -UseBasicParsing).Content) -match '"dateTime": "2026-11-20T10:00:00[+-]\d\d:\d\d"') -and (Until { (Js 'document.querySelector(''[data-day=\"2026-11-20\"] .ev'') ? document.querySelector(''[data-day=\"2026-11-20\"] .ev'').textContent : ''''') -match 'Réunion' } 8000))
  $eid = ((Fake).events.'primary-id@group.calendar.google.com' | Where-Object { $_.summary -eq 'Réunion' }).id
  Ctl "cmd {`"cmd`":`"ag.event.save`",`"id`":`"$eid`",`"cal`":`"primary-id@group.calendar.google.com`",`"title`":`"Réunion reportée`",`"allDay`":true,`"start`":`"2026-11-21`",`"end`":`"2026-11-22`"}" | Out-Null
  Check "modification : titre et passage en journée entière (le 21)" ((Until { (Js 'document.querySelector(''[data-day=\"2026-11-21\"] .ev'') ? document.querySelector(''[data-day=\"2026-11-21\"] .ev'').textContent : ''''') -match 'reportée' } 8000) -and (((Fake).events.'primary-id@group.calendar.google.com' | Where-Object { $_.id -eq $eid }).start.date -eq '2026-11-21'))
  Ctl "cmd {`"cmd`":`"ag.event.delete`",`"cal`":`"primary-id@group.calendar.google.com`",`"id`":`"$eid`"}" | Out-Null
  Check "suppression : plus chez Google ni dans la page" ((Until { -not ((Fake).events.'primary-id@group.calendar.google.com'.id -contains $eid) } 8000) -and (Until { (Js "document.body.innerText") -notmatch 'reportée' } 8000))
  Check "un événement d'un agenda en lecture seule n'est pas modifiable (pas de bouton Modifier)" ((Js "const x = [...document.querySelectorAll('[data-ev]')].find(e => e.textContent.includes('Armistice')); x.click(); const r = !!document.querySelector('.veil') && !document.getElementById('edit'); document.querySelector('.veil') && document.querySelector('.veil').remove(); return r ? 'oui' : 'non'") -eq 'oui')

  "4. tâches"
  Check "la liste « Mes tâches » montre la tâche ouverte, la terminée est repliée" ((Until { (Js "document.querySelectorAll('.tk').length") -ge 1 } 8000) -and ((Js "document.querySelector('.tk .tt').textContent") -match 'Acheter du pain') -and ((Js "document.querySelector('[data-doneh]').textContent") -match '1 terminée'))
  Ctl 'cmd {"cmd":"ag.task.toggle","list":"L1","id":"t1","done":true}' | Out-Null
  Check "cocher : la tâche passe « completed » chez Google" (Until { ((Fake).tasks.L1 | Where-Object { $_.id -eq 't1' }).status -eq 'completed' } 8000)
  Ctl 'cmd {"cmd":"ag.task.toggle","list":"L1","id":"t1","done":false}' | Out-Null
  Check "décocher : « needsAction », sans date de fin" (Until { $t = (Fake).tasks.L1 | Where-Object { $_.id -eq 't1' }; $t.status -eq 'needsAction' -and -not $t.completed } 8000)
  Ctl 'cmd {"cmd":"ag.task.add","list":"L1","title":"Acheter du lait","due":"2026-11-30"}' | Out-Null
  Check "ajouter : la tâche est créée avec son échéance et apparaît dans la page" ((Until { (Fake).tasks.L1.title -contains 'Acheter du lait' } 8000) -and (((Invoke-WebRequest "http://127.0.0.1:$port/_state" -UseBasicParsing).Content) -match '"due": "2026-11-30T00:00:00.000Z"') -and (Until { (Js "document.body.innerText") -match 'Acheter du lait' } 8000))
  $tid = ((Fake).tasks.L1 | Where-Object { $_.title -eq 'Acheter du lait' }).id
  Ctl "cmd {`"cmd`":`"ag.task.delete`",`"list`":`"L1`",`"id`":`"$tid`"}" | Out-Null
  Check "supprimer : disparue" (Until { -not ((Fake).tasks.L1.id -contains $tid) } 8000)

  "5. redémarrage et oubli du compte"
  $app.Kill(); Start-Sleep 1
  $app = Open-Agenda; $started += $app
  Check "après redémarrage : relié sans repasser par le consentement, les événements du cache puis les frais" ((St).connected -and (St).email -eq 'essai@example.org' -and (Until { (Js "document.querySelectorAll('.ev').length") -ge 1 } 10000))
  Check "le jeton a été rafraîchi au moins une fois (expires_in court du faux serveur, ou 401)" ((Fake).tokens -ge 2)
  Ctl 'cmd {"cmd":"ag.disconnect"}' | Out-Null
  Check "oublier le compte : jeton effacé, page « relier »" ((Until { -not (St).connected } 8000) -and -not (Test-Path "$suite\google-token.bin") -and (Until { (Js "!!document.getElementById('connect')") -eq 'true' } 8000))
  Check "le programme n'a pas planté" (-not $app.HasExited)
  "journal (fin) :"; Get-Content "$data\journal-agenda.log" -Tail 12 | ForEach-Object { '   ' + $_ }
}
finally {
  foreach ($p in $started) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch { } } }
  Start-Sleep 1
  Remove-Item $suite, $data -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR, Env:CDX_NO_FOCUS, Env:CDX_GOOGLE_API, Env:CDX_GOOGLE_NO_BROWSER -ErrorAction SilentlyContinue
}
