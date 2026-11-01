using System.Diagnostics;
using System.Text;

namespace Cdx;

// Claude Code en sous-processus, une conversation = un processus `claude -p` persistant (entrée et sortie en stream-json ; voir ClaudeFlux dans Cdx.Host).
// Le même canal que Tennis (src/claude.ts, 22/09/2026) : la session Claude a un identifiant qu'on choisit (--session-id) et qu'on reprend (--resume) après
// un redémarrage, avec tout son contexte. Les demandes d'accord arrivent sur la sortie (--permission-prompt-tool stdio) et se répondent sur l'entrée.
// Les événements sont rendus sur le fil de l'interface (SynchronizationContext du lancement).
sealed class ClaudeSession
{
    public readonly string Conv;
    public bool Busy { get; private set; }
    public bool Alive => p != null && !p.HasExited;
    public int Pid => p?.Id ?? 0;
    Process p; readonly ClaudeFlux flux = new(); readonly SynchronizationContext ctx; readonly Action<ClaudeSession, ClaudeEvent> onEvent; readonly Action<ClaudeSession, int> onExit;
    readonly object wl = new();

    public ClaudeSession(string conv, Action<ClaudeSession, ClaudeEvent> onEvent, Action<ClaudeSession, int> onExit)
    {
        Conv = conv; this.onEvent = onEvent; this.onExit = onExit;
        ctx = SynchronizationContext.Current ?? throw new InvalidOperationException("ClaudeSession : fil de l'interface requis");
    }

    // Le programme : CDX_CLAUDE_EXE (essais : « python » et CDX_CLAUDE_ARGS « -I faux.py »), sinon ~\.local\bin\claude.exe, sinon dans le PATH.
    public static string FindExe()
    {
        string env = Environment.GetEnvironmentVariable("CDX_CLAUDE_EXE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in new[] { "claude.exe", "claude.cmd" }) { try { string f = Path.Combine(dir.Trim(), n); if (File.Exists(f)) return f; } catch (Exception) { } }
        return null;
    }
    public static bool ExeFound() { string e = FindExe(); return e != null && (e.IndexOfAny(new[] { '\\', '/' }) < 0 || File.Exists(e)); }

    // Les arguments d'un lancement. `resume` : reprendre la session `id` ; sinon l'ouvrir sous cet identifiant.
    public static List<string> Arguments(string id, bool resume, string model, string effort, string mcpConfig, IEnumerable<string> allowedTools, IEnumerable<string> addDirs, string systemPrompt)
    {
        var a = new List<string>();
        string prefix = Environment.GetEnvironmentVariable("CDX_CLAUDE_ARGS");
        if (!string.IsNullOrWhiteSpace(prefix)) a.AddRange(prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        a.AddRange(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--include-partial-messages", "--verbose" });
        a.AddRange(resume ? new[] { "--resume", id } : new[] { "--session-id", id });
        if (!string.IsNullOrEmpty(model)) { a.Add("--model"); a.Add(model); }
        if (!string.IsNullOrEmpty(effort)) { a.Add("--effort"); a.Add(effort); }
        // Les outils intégrés que Pilote laisse à Claude ; les outils de la suite et des notes viennent des serveurs MCP (seulement ceux de notre fichier).
        a.AddRange(new[] { "--tools", "Read,Edit,Write,Glob,Grep,Bash,PowerShell,WebSearch,WebFetch,AskUserQuestion,Task" });
        a.AddRange(new[] { "--permission-mode", "manual", "--permission-prompt-tool", "stdio", "--strict-mcp-config", "--mcp-config", mcpConfig });
        a.Add("--allowedTools"); a.AddRange(allowedTools);
        foreach (var d in addDirs) { a.Add("--add-dir"); a.Add(d); }
        a.Add("--append-system-prompt"); a.Add(systemPrompt);
        return a;
    }

    public void Start(string exe, IEnumerable<string> args, string cwd, IDictionary<string, string> extraEnv)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        // Lancé depuis une session Claude Code (essais), le sous-processus refuserait de démarrer : ses variables ne lui sont pas transmises. Rien d'autre n'est retiré
        // (l'authentification de Claude Code passe par ses propres fichiers).
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
        psi.Environment["MCP_TOOL_TIMEOUT"] = "300000";
        foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;
        p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (s, e) => { int code = 0; try { code = p.ExitCode; } catch (Exception) { } ctx.Post(_ => { Busy = false; onExit(this, code); }, null); };
        p.Start();
        new Thread(() => ReadOut(p.StandardOutput)) { IsBackground = true, Name = "claude-out" }.Start();
        new Thread(() => { try { string l; while ((l = p.StandardError.ReadLine()) != null) if (l.Trim().Length > 0) Host.Log("claude (stderr) : " + (l.Length > 300 ? l[..300] : l)); } catch (Exception) { } }) { IsBackground = true, Name = "claude-err" }.Start();
    }

    void ReadOut(StreamReader r)
    {
        try
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                var evs = flux.Line(line);
                if (evs.Count == 0) continue;
                ctx.Post(_ => { foreach (var e in evs) { if (e.Kind == "result") Busy = false; onEvent(this, e); } }, null);
            }
        }
        catch (Exception ex) { Host.Log("claude (sortie) : " + ex.Message); }
    }

    public void Send(string text) { Busy = true; Write(ClaudeFlux.UserMessage(text)); }
    public void Answer(string requestId, bool allow, string updatedInput, string message) => Write(ClaudeFlux.Answer(requestId, allow, updatedInput, message));
    public void Interrupt() => Write(ClaudeFlux.Interrupt(Guid.NewGuid().ToString("N")));

    void Write(string line)
    {
        if (!Alive) return;
        lock (wl) { try { p.StandardInput.Write(line + "\n"); p.StandardInput.Flush(); } catch (Exception ex) { Host.Log("claude (entrée) : " + ex.Message); } }
    }

    public void Kill()
    {
        try { if (p != null && !p.HasExited) p.Kill(true); } catch (Exception) { }
        Busy = false;
    }
}
