# Un faux « claude -p » pour l'essai isolé de Pilote (tests\pilote.ps1) : même ligne de commande, même flux stream-json dans les deux sens
# (formes relevées en vrai le 01/11/2026, Claude Code 2.1.316), mais un scénario fixé par le texte de Valentin, sans modèle ni réseau.
# Il lance lui-même les serveurs MCP de --mcp-config (comme Claude Code) et leur parle en JSON-RPC sur leur entrée/sortie standard.
# Ce qu'il fait selon le message :
#   « outils »                  liste les outils de chaque serveur MCP et les nomme dans sa réponse
#   « ouvre <adresse> »         browser_open puis browser_read ; répond avec le titre et un extrait
#   « écris <chemin> : <texte> »  demande l'accord (can_use_tool Write) ; si accordé, écrit le fichier ; répond selon la décision
#   « question »                pose une question à choix (AskUserQuestion) et répond avec le choix
#   « commande <app> <json> »   app_command (outil non pré-autorisé : demande d'accord d'abord, comme le ferait Claude Code)
#   « projet <nom> »            project_folder puis (notes) projects_create
#   « courrier <recherche> »    mail_search puis mail_read du premier fil ; répond avec l'objet et le début du texte
#   « attends »                 ne répond rien pendant 60 s (pour l'essai de l'arrêt)
#   autre                       répète le texte, en morceaux
# Il note chaque tour dans <dossier des données>\fake-claude-<session>.txt (le nombre de tours prouve la reprise --resume).
import json, os, subprocess, sys, threading, time, uuid
sys.stdin.reconfigure(encoding="utf-8"); sys.stdout.reconfigure(encoding="utf-8"); sys.stderr.reconfigure(encoding="utf-8")   # Windows : sinon la console (cp1252) mange les accents

def arg(name, default=None):
    a = sys.argv
    return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

session = arg("--session-id") or arg("--resume") or str(uuid.uuid4())
resumed = "--resume" in sys.argv
allowed = []
if "--allowedTools" in sys.argv:
    i = sys.argv.index("--allowedTools") + 1
    while i < len(sys.argv) and not sys.argv[i].startswith("--"): allowed.append(sys.argv[i]); i += 1
mcp_file = arg("--mcp-config")
prompt_tool = arg("--permission-prompt-tool")
data_dir = os.environ.get("CDX_DATA_DIR") or os.getcwd()
# la consigne reçue (--append-system-prompt) et les arguments, pour que l'essai vérifie ce que Pilote donne à Claude
open(os.path.join(data_dir, f"fake-claude-prompt-{session}.txt"), "w", encoding="utf-8").write(" ".join(sys.argv[1:]) + chr(10) + "=====" + chr(10) + (arg("--append-system-prompt") or ""))
out_lock = threading.Lock()

def emit(o):
    with out_lock:
        sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()

# ---------- les serveurs MCP ----------
class Mcp:
    def __init__(self, name, cfg):
        self.name = name; self.n = 0; self.lock = threading.Lock()
        env = dict(os.environ); env.update(cfg.get("env") or {})
        self.p = subprocess.Popen([cfg["command"]] + list(cfg.get("args") or []), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr, env=env, text=True, encoding="utf-8")
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "fake-claude", "version": "0"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); self.p.stdin.flush()
        self.tools = [t["name"] for t in self.rpc("tools/list", {}).get("tools", [])]
    def rpc(self, method, params):
        with self.lock:
            self.n += 1; rid = self.n
            self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n"); self.p.stdin.flush()
            while True:
                line = self.p.stdout.readline()
                if not line: return {"error": "serveur muet"}
                try: m = json.loads(line)
                except Exception: continue
                if m.get("id") == rid: return m.get("result") or {"error": m.get("error")}
    def call(self, tool, args):
        r = self.rpc("tools/call", {"name": tool, "arguments": args})
        txt = " ".join(c.get("text", "(image)") for c in r.get("content", [])) if isinstance(r, dict) and "content" in r else json.dumps(r)
        return txt, bool(r.get("isError")) if isinstance(r, dict) else True

servers = {}
if mcp_file:
    cfg = json.load(open(mcp_file, encoding="utf-8")).get("mcpServers", {})
    for name, c in cfg.items():
        try: servers[name] = Mcp(name, c)
        except Exception as e: sys.stderr.write(f"fake-claude : serveur {name} : {e}\n")

emit({"type": "system", "subtype": "init", "cwd": os.getcwd(), "session_id": session, "tools": ["Bash", "Read", "Write", "AskUserQuestion"],
      "mcp_servers": [{"name": n, "status": "connected"} for n in servers], "model": "faux-claude", "permissionMode": "default", "uuid": str(uuid.uuid4()), "resumed": resumed})

# ---------- le protocole d'accord ----------
pending = {}   # request_id -> Event + réponse
def ask_permission(tool, inp, description=""):
    rid = str(uuid.uuid4()); ev = threading.Event(); pending[rid] = [ev, None]
    emit({"type": "control_request", "request_id": rid, "request": {"subtype": "can_use_tool", "tool_name": tool, "display_name": tool, "input": inp, "description": description, "permission_suggestions": [], "tool_use_id": "toolu_" + rid[:8]}})
    ev.wait(600)
    r = pending.pop(rid)[1] or {"behavior": "deny", "message": "pas de réponse"}
    return r

def text(s):
    emit({"type": "stream_event", "event": {"type": "message_start", "message": {"id": "msg_" + uuid.uuid4().hex[:8], "role": "assistant", "content": []}}, "session_id": session, "uuid": str(uuid.uuid4())})
    for i in range(0, len(s), 12):
        emit({"type": "stream_event", "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": s[i:i + 12]}}, "session_id": session, "uuid": str(uuid.uuid4())})
        time.sleep(0.01)

def use_tool(full, inp, run):
    # un outil MCP qui n'est pas pré-autorisé passe par la demande d'accord, comme chez Claude Code
    tid = "toolu_" + uuid.uuid4().hex[:10]
    if full not in allowed and prompt_tool == "stdio":
        r = ask_permission(full, inp)
        if r.get("behavior") != "allow":
            emit({"type": "assistant", "message": {"id": "msg_x", "role": "assistant", "content": [{"type": "tool_use", "id": tid, "name": full, "input": inp}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
            emit({"type": "user", "message": {"role": "user", "content": [{"tool_use_id": tid, "type": "tool_result", "content": "refusé : " + r.get("message", ""), "is_error": True}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
            return "refusé : " + r.get("message", ""), True
        inp = r.get("updatedInput", inp)
    emit({"type": "assistant", "message": {"id": "msg_x", "role": "assistant", "content": [{"type": "tool_use", "id": tid, "name": full, "input": inp}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
    res, err = run(inp)
    emit({"type": "user", "message": {"role": "user", "content": [{"tool_use_id": tid, "type": "tool_result", "content": res, "is_error": err}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
    return res, err

def mcp(server, tool, args):
    s = servers.get(server)
    if not s: return "serveur absent : " + server, True
    return use_tool(f"mcp__{server}__{tool}", args, lambda inp: s.call(tool, inp))

turns = 0
def turn(msg):
    global turns
    turns += 1
    with open(os.path.join(data_dir, f"fake-claude-{session}.txt"), "a", encoding="utf-8") as f: f.write(f"tour {turns} : {msg}\n")
    emit({"type": "system", "subtype": "status", "status": "requesting", "session_id": session, "uuid": str(uuid.uuid4())})
    emit({"type": "rate_limit_event", "rate_limit_info": {"status": "allowed", "unifiedWindows": {"five_hour": {"utilization": 0.37, "resetsAt": int(time.time()) + 3600}, "seven_day": {"utilization": 0.12, "resetsAt": int(time.time()) + 86400}}}, "session_id": session, "uuid": str(uuid.uuid4())})
    low = msg.strip().lower(); reply = ""
    if low == "outils":
        names = [f"{n} : {', '.join(s.tools)}" for n, s in servers.items()]
        reply = "Outils : " + " | ".join(names)
    elif low.startswith("ouvre "):
        url = msg.strip()[6:].strip()
        res, err = mcp("cdx-suite", "browser_open", {"url": url})
        if err: reply = "Impossible d'ouvrir : " + res
        else:
            txt, err2 = mcp("cdx-suite", "browser_read", {"max_chars": 500})
            try: j = json.loads(txt); reply = f"La page « {j.get('title')} » dit : {j.get('text', '').strip()[:120]}"
            except Exception: reply = "Lu : " + txt[:160]
    elif low.startswith("écris ") or low.startswith("ecris "):
        rest = msg.strip()[6:]; path, _, content = rest.partition(" : ")
        path = path.strip(); content = content.strip() or "ok"
        r = ask_permission("Write", {"file_path": path, "content": content}, os.path.basename(path))
        tid = "toolu_" + uuid.uuid4().hex[:10]
        emit({"type": "assistant", "message": {"id": "msg_w", "role": "assistant", "content": [{"type": "tool_use", "id": tid, "name": "Write", "input": {"file_path": path, "content": content}}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
        if r.get("behavior") == "allow":
            os.makedirs(os.path.dirname(path), exist_ok=True); open(path, "w", encoding="utf-8").write(content)
            emit({"type": "user", "message": {"role": "user", "content": [{"tool_use_id": tid, "type": "tool_result", "content": "File created successfully at: " + path}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
            reply = "Écrit : " + path
        else:
            emit({"type": "user", "message": {"role": "user", "content": [{"tool_use_id": tid, "type": "tool_result", "content": "refusé", "is_error": True}]}, "parent_tool_use_id": None, "session_id": session, "uuid": str(uuid.uuid4())})
            reply = "Refusé : " + r.get("message", "")
    elif low == "question":
        q = {"questions": [{"question": "Quel langage ?", "header": "Langage", "options": [{"label": "Rust", "description": "rapide"}, {"label": "C#", "description": "la suite"}], "multiSelect": False}]}
        r = ask_permission("AskUserQuestion", q)
        ans = (r.get("updatedInput") or {}).get("answers", {}) if r.get("behavior") == "allow" else {}
        reply = "Tu as choisi : " + (ans.get("Quel langage ?") or "(rien)")
    elif low.startswith("commande "):
        parts = msg.strip().split(" ", 2); app = parts[1]; cmd = parts[2] if len(parts) > 2 else "{}"
        res, err = mcp("cdx-suite", "app_command", {"app": app, "command": cmd})
        reply = ("Échec : " if err else "Commande envoyée : ") + res[:160]
    elif low.startswith("projet "):
        name = msg.strip()[7:].strip()
        res, err = mcp("cdx-suite", "project_folder", {"name": name})
        path = ""
        try: path = json.loads(res).get("path", "")
        except Exception: pass
        res2, err2 = mcp("cdx-notes", "projects_create", {"name": name, "repo": path, "summary": "Projet créé par le faux Claude"})
        reply = f"Projet {name} : dossier {path} ; fiche : {res2[:80]}"
    elif low.startswith("courrier "):
        q = msg.strip()[9:].strip()
        res, err = mcp("cdx-suite", "mail_search", {"q": q, "max": 5})
        if err: reply = "Courrier impossible : " + res[:160]
        else:
            try: fils = json.loads(res)
            except Exception: fils = []
            if not fils: reply = "Aucun fil pour « %s »." % q
            else:
                txt, err2 = mcp("cdx-suite", "mail_read", {"id": fils[0]["id"]})
                try: msgs = json.loads(txt); reply = "Courrier : %s — %s (%d message(s))" % (fils[0].get("subject"), (msgs[-1].get("text") or "").strip()[:60], len(msgs))
                except Exception: reply = "Courrier lu : " + txt[:160]
    elif low.startswith("retiens "):
        fait = msg.strip()[8:].strip()
        res, err = mcp("cdx-notes", "notes_append", {"path": "Pilote/Profil.md", "text": "- " + fait})
        reply = ("Mémoire impossible : " if err else "Retenu : ") + fait
    elif low == "attends":
        time.sleep(60); reply = "J'ai attendu."
    else:
        reply = "Tu as dit : " + msg.strip()
    text(reply)
    emit({"type": "result", "subtype": "success", "is_error": False, "duration_ms": 1200, "duration_api_ms": 900, "num_turns": 1, "result": reply, "stop_reason": "end_turn", "session_id": session, "total_cost_usd": 0.01,
          "usage": {"input_tokens": 20, "cache_creation_input_tokens": 100, "cache_read_input_tokens": 3000, "output_tokens": 40, "iterations": [{"input_tokens": 20, "output_tokens": 40, "cache_read_input_tokens": 3000, "cache_creation_input_tokens": 100}]},
          "modelUsage": {"faux-claude": {"inputTokens": 20, "outputTokens": 40, "cacheReadInputTokens": 3000, "cacheCreationInputTokens": 100, "contextWindow": 200000}}, "permission_denials": [], "uuid": str(uuid.uuid4())})

for line in sys.stdin:
    line = line.strip()
    if not line: continue
    try: m = json.loads(line)
    except Exception: continue
    t = m.get("type")
    if t == "user":
        c = m.get("message", {}).get("content")
        txt = c if isinstance(c, str) else " ".join(x.get("text", "") for x in c if x.get("type") == "text")
        threading.Thread(target=turn, args=(txt,), daemon=True).start()
    elif t == "control_response":
        r = m.get("response", {}); rid = r.get("request_id")
        if rid in pending: pending[rid][1] = r.get("response"); pending[rid][0].set()
    elif t == "control_request" and m.get("request", {}).get("subtype") == "interrupt":
        emit({"type": "control_response", "response": {"subtype": "success", "request_id": m.get("request_id"), "response": {}}})
        emit({"type": "result", "subtype": "error_during_execution", "is_error": True, "result": "Interrompu.", "session_id": session, "usage": {}, "uuid": str(uuid.uuid4())})
        os._exit(0)
for s in servers.values():
    try: s.p.kill()
    except Exception: pass
