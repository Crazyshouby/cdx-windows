# Un faux Gmail pour les essais de Courrier (tests\courrier.ps1) : OAuth (comme fake_google.py), libellés et compteurs, fils (liste, lecture, modification,
# corbeille), pièces jointes, envoi (message RFC 2822 brut, relu et rangé dans Envoyés), tout en mémoire. /_state rend l'état ; /_inject ajoute un courrier reçu.
#   python tests\fake_gmail.py <port>
import json, sys, re, base64, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, unquote

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8769
def b64u(s): return base64.urlsafe_b64encode(s if isinstance(s, bytes) else s.encode("utf-8")).decode("ascii").rstrip("=")
def part(mime, text=None, filename="", data=None, aid=None, parts=None):
    p = {"mimeType": mime, "filename": filename, "headers": [], "body": {}}
    if text is not None: p["body"] = {"size": len(text), "data": b64u(text)}
    if data is not None: p["body"] = {"size": len(data), "attachmentId": aid}
    if parts: p["parts"] = parts
    return p
PDF = b"%PDF-1.4 faux document de test\n"
NOW = int(time.time() * 1000)
STATE = {"tokens": 0, "scopes": "", "seq": 100, "sent": [], "attachments": {"a1": PDF},
    "labels": [{"id": "INBOX", "name": "INBOX", "type": "system"}, {"id": "STARRED", "name": "STARRED", "type": "system"}, {"id": "SENT", "name": "SENT", "type": "system"}, {"id": "TRASH", "name": "TRASH", "type": "system"},
               {"id": "SPAM", "name": "SPAM", "type": "system"}, {"id": "UNREAD", "name": "UNREAD", "type": "system"}, {"id": "CATEGORY_PROMOTIONS", "name": "CATEGORY_PROMOTIONS", "type": "system"}, {"id": "Label_1", "name": "Factures", "type": "user"}],
    "messages": {}}
def add(mid, tid, labels, hdrs, snippet, payload, date):
    STATE["messages"][mid] = {"id": mid, "threadId": tid, "labelIds": list(labels), "snippet": snippet, "internalDate": str(date), "payload": dict(payload, headers=[{"name": k, "value": v} for k, v in hdrs.items()])}
add("m1", "t1", ["INBOX", "UNREAD"], {"From": "Alice <alice@example.org>", "To": "essai@example.org", "Subject": "Facture octobre", "Date": "Sat, 1 Nov 2026 09:00:00 +0100", "Message-ID": "<m1@example.org>"}, "Voici la facture du mois",
    part("multipart/mixed", parts=[part("multipart/alternative", parts=[part("text/plain", "Bonjour,\n\nVoici la facture du mois.\n\nAlice"), part("text/html", '<p>Bonjour,</p><p>Voici la <b>facture</b> du mois.</p><img src="http://127.0.0.1:%d/pixel.png"><p>Alice</p>' % PORT)]), part("application/pdf", filename="facture.pdf", data=PDF, aid="a1")]), NOW - 7200000)
add("m2", "t1", ["INBOX", "UNREAD"], {"From": "Bob <bob@example.org>", "To": "essai@example.org", "Cc": "alice@example.org", "Subject": "Re: Facture octobre", "Message-ID": "<m2@example.org>", "References": "<m1@example.org>"}, "Merci, reçu",
    part("text/html", "<p>Merci, <b>reçu</b>.</p>"), NOW - 3600000)
add("m3", "t2", ["INBOX", "STARRED"], {"From": "Chloé <chloe@example.org>", "To": "essai@example.org", "Subject": "Week-end", "Message-ID": "<m3@example.org>"}, "On part samedi ?", part("text/plain", "On part samedi ?\n\nChloé"), NOW - 90000000)
add("m4", "t3", ["INBOX", "UNREAD", "Label_1"], {"From": "EDF <facture@edf.example>", "To": "essai@example.org", "Subject": "Votre facture EDF", "Message-ID": "<m4@example.org>"}, "Votre facture est disponible", part("text/plain", "Votre facture est disponible."), NOW - 200000000)
add("m5", "t4", ["SENT"], {"From": "essai@example.org", "To": "chloe@example.org", "Subject": "Re: Week-end", "Message-ID": "<m5@example.org>"}, "Oui, samedi 9 h", part("text/plain", "Oui, samedi 9 h"), NOW - 80000000)

def threads():
    t = {}
    for m in STATE["messages"].values(): t.setdefault(m["threadId"], []).append(m)
    for l in t.values(): l.sort(key=lambda m: int(m["internalDate"]))
    return t
def meta(m):
    return {"id": m["id"], "threadId": m["threadId"], "labelIds": m["labelIds"], "snippet": m["snippet"], "internalDate": m["internalDate"], "payload": {"headers": m["payload"]["headers"], "mimeType": m["payload"]["mimeType"]}}
def text_of(p):
    s = ""
    if p.get("body", {}).get("data"): s += base64.urlsafe_b64decode(p["body"]["data"] + "==").decode("utf-8", "replace")
    for q in p.get("parts", []): s += text_of(q)
    return s
def matches(ms, q):
    q = q.lower()
    if q.startswith("from:"): return any(q[5:] in h["value"].lower() for m in ms for h in m["payload"]["headers"] if h["name"] == "From")
    return any(q in (m["snippet"] + " ".join(h["value"] for h in m["payload"]["headers"]) + text_of(m["payload"])).lower() for m in ms)

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass
    def send_json(self, obj, code=200):
        b = json.dumps(obj).encode("utf-8")
        self.send_response(code); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""
    def auth_ok(self):
        a = self.headers.get("Authorization") or ""
        return a.startswith("Bearer access-") and a[14:].isdigit() and 1 <= int(a[14:]) <= STATE["tokens"]

    def do_GET(self):
        u = urlparse(self.path); q = parse_qs(u.query); p = u.path
        if p == "/auth":
            STATE["scopes"] = q.get("scope", [""])[0]
            self.send_response(302); self.send_header("Location", q["redirect_uri"][0] + "?code=code-xyz&state=" + q["state"][0]); self.send_header("Content-Length", "0"); self.end_headers(); return
        if p == "/_state":
            return self.send_json({"sent": STATE["sent"], "scopes": STATE["scopes"], "tokens": STATE["tokens"], "messages": {k: {"threadId": v["threadId"], "labels": v["labelIds"]} for k, v in STATE["messages"].items()}})
        if p == "/pixel.png": self.send_response(200); self.send_header("Content-Type", "image/png"); self.send_header("Content-Length", "0"); self.end_headers(); return
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        if p == "/oauth2/v3/userinfo": return self.send_json({"sub": "1", "email": "essai@example.org"})
        if p == "/gmail/v1/users/me/labels": return self.send_json({"labels": STATE["labels"]})
        m = re.match(r"^/gmail/v1/users/me/labels/([^/]+)$", p)
        if m:
            lid = unquote(m.group(1)); th = [ms for ms in threads().values() if any(lid in x["labelIds"] for x in ms)]
            return self.send_json({"id": lid, "name": lid, "threadsTotal": len(th), "threadsUnread": sum(1 for ms in th if any("UNREAD" in x["labelIds"] for x in ms)), "messagesUnread": 0})
        if p == "/gmail/v1/users/me/threads":
            lid = q.get("labelIds", [None])[0]; qq = q.get("q", [""])[0]; spam = q.get("includeSpamTrash", ["false"])[0] == "true"
            out = []
            for tid, ms in sorted(threads().items(), key=lambda kv: -int(kv[1][-1]["internalDate"])):
                labels = set(l for x in ms for l in x["labelIds"])
                if lid and lid not in labels: continue
                if not lid and not spam and labels & {"TRASH", "SPAM"}: continue
                if qq and not matches(ms, qq): continue
                out.append({"id": tid, "snippet": ms[-1]["snippet"], "historyId": "1"})
            return self.send_json({"threads": out[:int(q.get("maxResults", ["50"])[0])], "resultSizeEstimate": len(out)})
        m = re.match(r"^/gmail/v1/users/me/threads/([^/]+)$", p)
        if m:
            ms = threads().get(m.group(1))
            if not ms: return self.send_json({"error": {"code": 404, "message": "Not Found"}}, 404)
            full = q.get("format", ["full"])[0] == "full"
            return self.send_json({"id": m.group(1), "messages": [x if full else meta(x) for x in ms]})
        m = re.match(r"^/gmail/v1/users/me/messages/([^/]+)/attachments/([^/]+)$", p)
        if m:
            d = STATE["attachments"].get(m.group(2))
            if d is None: return self.send_json({"error": {"code": 404, "message": "Not Found"}}, 404)
            return self.send_json({"size": len(d), "data": b64u(d)})
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

    def do_POST(self):
        u = urlparse(self.path); p = u.path; raw = self.body()
        if p == "/token":
            form = parse_qs(raw.decode("utf-8")); STATE["tokens"] += 1
            return self.send_json({"access_token": "access-" + str(STATE["tokens"]), "refresh_token": "refresh-1", "expires_in": 3600, "scope": STATE["scopes"] or "openid email", "token_type": "Bearer"})
        if p == "/_inject":
            STATE["seq"] += 1; mid = "m" + str(STATE["seq"])
            add(mid, "t" + str(STATE["seq"]), ["INBOX", "UNREAD"], {"From": "Nouveau <nouveau@example.org>", "To": "essai@example.org", "Subject": "Courrier injecté", "Message-ID": "<%s@example.org>" % mid}, "Un courrier qui arrive", part("text/plain", "Un courrier qui arrive pendant l'essai."), int(time.time() * 1000))
            return self.send_json({"id": mid})
        if not self.auth_ok(): return self.send_json({"error": {"code": 401, "message": "Invalid Credentials"}}, 401)
        m = re.match(r"^/gmail/v1/users/me/threads/([^/]+)/(modify|trash|untrash)$", p)
        if m:
            ms = threads().get(m.group(1))
            if not ms: return self.send_json({"error": {"code": 404, "message": "Not Found"}}, 404)
            d = json.loads(raw or b"{}")
            addl = d.get("addLabelIds", []); rem = d.get("removeLabelIds", [])
            if m.group(2) == "trash": addl, rem = ["TRASH"], ["INBOX"]
            if m.group(2) == "untrash": addl, rem = ["INBOX"], ["TRASH"]
            for x in ms: x["labelIds"] = [l for l in x["labelIds"] if l not in rem] + [l for l in addl if l not in x["labelIds"]]
            return self.send_json({"id": m.group(1), "messages": [meta(x) for x in ms]})
        if p == "/upload/gmail/v1/users/me/messages/send" or p == "/gmail/v1/users/me/messages/send":
            txt = raw.decode("utf-8", "replace")
            if p.endswith("/messages/send") and not p.startswith("/upload"): txt = base64.urlsafe_b64decode(json.loads(txt)["raw"] + "==").decode("utf-8", "replace")
            hdr = lambda n: (re.search(r"^" + n + r": (.*)$", txt, re.M | re.I) or [None, ""])[1].strip()
            STATE["seq"] += 1; mid = "m" + str(STATE["seq"])
            subj = hdr("Subject")
            dec = re.match(r"=\?UTF-8\?B\?(.*)\?=", subj)
            subj_dec = base64.b64decode(dec.group(1)).decode("utf-8") if dec else subj
            irt = hdr("In-Reply-To")
            tid = next((x["threadId"] for x in STATE["messages"].values() if any(h["name"] == "Message-ID" and h["value"] == irt for h in x["payload"]["headers"])), "t" + str(STATE["seq"])) if irt else "t" + str(STATE["seq"])
            tm = re.search(r"Content-Transfer-Encoding: base64\r?\n\r?\n([A-Za-z0-9+/=\r\n]+)", txt)
            body_text = base64.b64decode(re.sub(r"\s", "", tm.group(1))).decode("utf-8", "replace") if tm else ""
            STATE["sent"].append({"id": mid, "text": body_text, "to": hdr("To"), "cc": hdr("Cc"), "subject": subj, "subjectDecoded": subj_dec, "inReplyTo": irt, "references": hdr("References"), "contentType": hdr("Content-Type"), "hasAttachment": "Content-Disposition: attachment" in txt, "raw": txt[:4000], "threadId": tid})
            add(mid, tid, ["SENT"], {"From": hdr("From"), "To": hdr("To"), "Subject": subj_dec, "Message-ID": "<%s@example.org>" % mid}, subj_dec, part("text/plain", "(envoyé)"), int(time.time() * 1000))
            return self.send_json({"id": mid, "threadId": tid, "labelIds": ["SENT"]})
        self.send_json({"error": {"code": 404, "message": "inconnu " + p}}, 404)

ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
