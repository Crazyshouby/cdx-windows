# Un faux Google pour les essais d'Agenda (tests\agenda.ps1) : OAuth (page de consentement qui renvoie tout de suite le code, point de jeton), userinfo,
# Google Calendar (agendas, événements : lecture, création, modification, suppression) et Google Tasks (listes, tâches), tout en mémoire.
#   python tests\fake_google.py <port>
# /_state rend l'état (JSON) pour que l'essai vérifie ce que le programme a écrit. Rien ici ne touche à Internet ni au vrai compte.
import json, sys, re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8768
STATE = {
    "tokens": 0, "refreshes": 0, "scopes": "",
    "calendars": [
        {"id": "primary-id@group.calendar.google.com", "summary": "Valentin", "backgroundColor": "#9fe1e7", "primary": True, "accessRole": "owner"},
        {"id": "fetes@group.v.calendar.google.com", "summary": "Fêtes", "backgroundColor": "#7bd148", "accessRole": "reader"},
    ],
    "events": {
        "primary-id@group.calendar.google.com": [
            {"id": "ev1", "summary": "Dentiste", "start": {"dateTime": "2026-11-05T14:30:00+01:00"}, "end": {"dateTime": "2026-11-05T15:30:00+01:00"}, "location": "Clermont", "status": "confirmed", "htmlLink": "https://calendar.google.com/ev1"},
            {"id": "ev2", "summary": "Week-end", "start": {"date": "2026-11-14"}, "end": {"date": "2026-11-16"}, "status": "confirmed"},
            {"id": "ev3", "summary": "Ancien", "start": {"dateTime": "2026-10-02T09:00:00+02:00"}, "end": {"dateTime": "2026-10-02T10:00:00+02:00"}, "status": "confirmed"},
        ],
        "fetes@group.v.calendar.google.com": [
            {"id": "f1", "summary": "Armistice", "start": {"date": "2026-11-11"}, "end": {"date": "2026-11-12"}, "status": "confirmed"},
        ],
    },
    "lists": [{"id": "L1", "title": "Mes tâches"}],
    "tasks": {"L1": [
        {"id": "t1", "title": "Acheter du pain", "status": "needsAction", "position": "00000000000000000001"},
        {"id": "t2", "title": "Appeler la banque", "status": "completed", "due": "2026-11-03T00:00:00.000Z", "position": "00000000000000000002"},
    ]},
    "seq": 10,
}

def in_range(ev, tmin, tmax):
    s = ev["start"].get("dateTime") or ev["start"].get("date")
    e = ev["end"].get("dateTime") or ev["end"].get("date")
    return (not tmin or e[:10] >= tmin[:10]) and (not tmax or s[:10] < tmax[:10])

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send_json(self, obj, code=200):
        b = json.dumps(obj).encode("utf-8")
        self.send_response(code); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n).decode("utf-8") if n else ""
    protocol_version = "HTTP/1.1"   # connexions gardées : le client .NET n'aime pas une connexion fermée par le serveur en plein envoi
    def auth_ok(self):
        # tout jeton d'accès émis reste valable (comme chez Google jusqu'à son expiration) : deux appels concurrents peuvent se rafraîchir chacun
        a = self.headers.get("Authorization") or ""
        return a.startswith("Bearer access-") and a[len("Bearer access-"):].isdigit() and 1 <= int(a[len("Bearer access-"):]) <= STATE["tokens"]

    def do_GET(self):
        u = urlparse(self.path); q = parse_qs(u.query); p = u.path
        if p == "/auth":
            # la page de consentement : renvoie tout de suite vers redirect_uri avec un code (l'essai suit la redirection)
            STATE["scopes"] = q.get("scope", [""])[0]
            self.send_response(302); self.send_header("Location", q["redirect_uri"][0] + "?code=code-xyz&state=" + q["state"][0]); self.end_headers(); return
        if p == "/_state": return self.send_json(STATE)
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        if p == "/oauth2/v3/userinfo": return self.send_json({"sub": "1", "email": "essai@example.org"})
        if p == "/calendar/v3/users/me/calendarList": return self.send_json({"items": STATE["calendars"]})
        m = re.match(r"^/calendar/v3/calendars/([^/]+)/events$", p)
        if m:
            cal = m.group(1).replace("%40", "@")
            tmin, tmax = q.get("timeMin", [""])[0], q.get("timeMax", [""])[0]
            return self.send_json({"items": [e for e in STATE["events"].get(cal, []) if in_range(e, tmin, tmax)]})
        if p == "/tasks/v1/users/@me/lists": return self.send_json({"items": STATE["lists"]})
        m = re.match(r"^/tasks/v1/lists/([^/]+)/tasks$", p)
        if m: return self.send_json({"items": STATE["tasks"].get(m.group(1), [])})
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

    def do_POST(self):
        u = urlparse(self.path); p = u.path; raw = self.body()
        if p == "/token":
            form = parse_qs(raw)
            STATE["tokens"] += 1
            if form.get("grant_type", [""])[0] == "refresh_token": STATE["refreshes"] += 1
            if form.get("grant_type", [""])[0] == "authorization_code" and form.get("code", [""])[0] != "code-xyz": return self.send_json({"error": "invalid_grant"}, 400)
            return self.send_json({"access_token": "access-" + str(STATE["tokens"]), "refresh_token": "refresh-1", "expires_in": 30, "scope": STATE["scopes"] or "openid email", "token_type": "Bearer"})
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        d = json.loads(raw or "{}")
        m = re.match(r"^/calendar/v3/calendars/([^/]+)/events$", p)
        if m:
            cal = m.group(1).replace("%40", "@"); STATE["seq"] += 1
            ev = dict(d); ev["id"] = "ev" + str(STATE["seq"]); ev["status"] = "confirmed"
            STATE["events"].setdefault(cal, []).append(ev); return self.send_json(ev)
        if p == "/tasks/v1/users/@me/lists":
            STATE["seq"] += 1; l = {"id": "L" + str(STATE["seq"]), "title": d.get("title", "")}; STATE["lists"].append(l); STATE["tasks"][l["id"]] = []; return self.send_json(l)
        m = re.match(r"^/tasks/v1/lists/([^/]+)/tasks$", p)
        if m:
            STATE["seq"] += 1; t = dict(d); t["id"] = "t" + str(STATE["seq"]); t.setdefault("status", "needsAction"); t["position"] = "0000000000000000000" + str(STATE["seq"])
            STATE["tasks"].setdefault(m.group(1), []).append(t); return self.send_json(t)
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

    def do_PATCH(self):
        u = urlparse(self.path); p = u.path; d = json.loads(self.body() or "{}")
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        m = re.match(r"^/calendar/v3/calendars/([^/]+)/events/([^/]+)$", p)
        if m:
            cal = m.group(1).replace("%40", "@")
            for ev in STATE["events"].get(cal, []):
                if ev["id"] == m.group(2): ev.update(d); return self.send_json(ev)
            return self.send_json({"error": {"code": 404, "message": "Not Found"}}, 404)
        m = re.match(r"^/tasks/v1/lists/([^/]+)/tasks/([^/]+)$", p)
        if m:
            for t in STATE["tasks"].get(m.group(1), []):
                if t["id"] == m.group(2):
                    for k, v in d.items():
                        if v is None: t.pop(k, None)
                        else: t[k] = v
                    return self.send_json(t)
            return self.send_json({"error": {"code": 404, "message": "Not Found"}}, 404)
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

    def do_DELETE(self):
        u = urlparse(self.path); p = u.path
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        m = re.match(r"^/calendar/v3/calendars/([^/]+)/events/([^/]+)$", p)
        if m:
            cal = m.group(1).replace("%40", "@"); STATE["events"][cal] = [e for e in STATE["events"].get(cal, []) if e["id"] != m.group(2)]
            self.send_response(204); self.end_headers(); return
        m = re.match(r"^/tasks/v1/lists/([^/]+)/tasks/([^/]+)$", p)
        if m:
            STATE["tasks"][m.group(1)] = [t for t in STATE["tasks"].get(m.group(1), []) if t["id"] != m.group(2)]
            self.send_response(204); self.end_headers(); return
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
