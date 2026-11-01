using System.Text.Json;

namespace Cdx;

// Le flux de Claude Code lancé en mode programme (`claude -p --input-format stream-json --output-format stream-json --include-partial-messages --verbose`) :
// une ligne de JSON par événement. Cet analyseur (logique pure, essai `cdx-shell --selftest-pilote`) rend ce que Pilote affiche et ce à quoi il doit répondre :
//   Text / Thinking   un morceau de texte au fil de l'eau (stream_event text_delta / thinking_delta) ; un message « assistant » dont le texte n'a pas été vu
//                     en morceaux (sans --include-partial-messages, ou un sous-agent) est rendu entier
//   ToolStart         un appel d'outil (message « assistant », bloc tool_use, arguments complets) : nom, identifiant, cible lisible, arguments
//   ToolResult        son résultat (message « user », bloc tool_result)
//   Permission        une demande d'accord (control_request can_use_tool, obtenue avec --permission-prompt-tool stdio) : à répondre par ClaudeFlux.Answer
//   Quota             la fenêtre de 5 h de l'abonnement (rate_limit_event), et celle de 7 jours s'il y en a une
//   Init              le début de session (system/init) : identifiant de session, modèle, serveurs MCP
//   Result            la fin du tour (result) : texte final, coût estimé, jetons, durée, contexte occupé / fenêtre
//   Status            system/status (requesting…), pour l'orbe
// Formes relevées en vrai le 01/11/2026 (Claude Code 2.1.316) ; voir aussi Tennis/src/claude-flux.ts.
sealed class ClaudeEvent
{
    public string Kind;                 // text thinking tool result permission quota init result status
    public string Text;                 // text, thinking, result (texte final), status (sous-type), init (modèle)
    public string ToolId, ToolName, Target, Input;   // tool : Input = arguments (JSON) ; result : ToolId, Text, IsError
    public bool IsError;
    public string RequestId, Description, ToolUseId, Suggestions;   // permission (Input = arguments, Suggestions = JSON brut)
    public double Part = -1, SevenPart = -1; public long ResetsAt, SevenResetsAt;   // quota (part = fraction utilisée, 0..1)
    public double CostUsd; public long InputTokens, OutputTokens, DurationMs, ContextUsed, ContextMax; public string SessionId, Subtype, Mcp;
}

sealed class ClaudeFlux
{
    readonly Dictionary<int, (string name, string id)> blocks = new();   // les blocs tool_use en cours de réception (index → nom)
    readonly HashSet<string> streamedText = new();                       // les messages dont le texte est arrivé en morceaux
    readonly HashSet<string> seenTools = new();
    string currentMessage;

    // Une ligne du flux → les événements qu'elle porte (aucun pour une ligne inconnue ou illisible).
    public List<ClaudeEvent> Line(string line)
    {
        var outp = new List<ClaudeEvent>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return outp; }
        using (doc)
        {
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return outp;
            string type = S(e, "type");
            switch (type)
            {
                case "stream_event": Stream(e, outp); break;
                case "assistant": Assistant(e, outp); break;
                case "user": User(e, outp); break;
                case "control_request":
                {
                    if (!e.TryGetProperty("request", out var r) || S(r, "subtype") != "can_use_tool") break;
                    outp.Add(new ClaudeEvent
                    {
                        Kind = "permission", RequestId = S(e, "request_id"), ToolName = S(r, "tool_name"), Description = S(r, "description"), ToolUseId = S(r, "tool_use_id"),
                        Input = r.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}", Target = r.TryGetProperty("input", out var inp2) ? TargetOf(S(r, "tool_name"), inp2) : "",
                        Suggestions = r.TryGetProperty("permission_suggestions", out var sg) ? sg.GetRawText() : "[]",
                    });
                    break;
                }
                case "rate_limit_event":
                {
                    if (!e.TryGetProperty("rate_limit_info", out var info) || !info.TryGetProperty("unifiedWindows", out var w)) break;
                    var q = new ClaudeEvent { Kind = "quota" };
                    if (w.TryGetProperty("five_hour", out var f) && f.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number) { q.Part = u.GetDouble(); q.ResetsAt = f.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : 0; }
                    if (w.TryGetProperty("seven_day", out var s7) && s7.TryGetProperty("utilization", out var u7) && u7.ValueKind == JsonValueKind.Number) { q.SevenPart = u7.GetDouble(); q.SevenResetsAt = s7.TryGetProperty("resetsAt", out var ra7) && ra7.ValueKind == JsonValueKind.Number ? ra7.GetInt64() : 0; }
                    if (q.Part >= 0) outp.Add(q);
                    break;
                }
                case "system":
                {
                    string sub = S(e, "subtype");
                    if (sub == "init")
                    {
                        string mcp = e.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array ? string.Join(", ", ms.EnumerateArray().Select(x => S(x, "name") + ":" + S(x, "status"))) : "";
                        outp.Add(new ClaudeEvent { Kind = "init", SessionId = S(e, "session_id"), Text = S(e, "model"), Mcp = mcp });
                    }
                    else if (sub == "status") outp.Add(new ClaudeEvent { Kind = "status", Text = S(e, "status") });
                    break;
                }
                case "result":
                {
                    var r = new ClaudeEvent { Kind = "result", Text = S(e, "result"), Subtype = S(e, "subtype"), SessionId = S(e, "session_id"), IsError = e.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True };
                    if (e.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) r.CostUsd = c.GetDouble();
                    if (e.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number) r.DurationMs = d.GetInt64();
                    if (e.TryGetProperty("usage", out var us))
                    {
                        r.InputTokens = L(us, "input_tokens") + L(us, "cache_read_input_tokens") + L(us, "cache_creation_input_tokens"); r.OutputTokens = L(us, "output_tokens");
                        // le contexte occupé : ce que le DERNIER appel au modèle a lu (usage.iterations), sinon l'usage du tour
                        var last = us.TryGetProperty("iterations", out var its) && its.ValueKind == JsonValueKind.Array && its.GetArrayLength() > 0 ? its[its.GetArrayLength() - 1] : us;
                        r.ContextUsed = L(last, "input_tokens") + L(last, "cache_read_input_tokens") + L(last, "cache_creation_input_tokens");
                    }
                    if (e.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                    {
                        long best = -1;
                        foreach (var m in mu.EnumerateObject())
                        {
                            long load = L(m.Value, "inputTokens") + L(m.Value, "cacheReadInputTokens") + L(m.Value, "cacheCreationInputTokens");
                            if (load > best && m.Value.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number) { best = load; r.ContextMax = cw.GetInt64(); }
                        }
                    }
                    outp.Add(r);
                    break;
                }
            }
        }
        return outp;
    }

    void Stream(JsonElement e, List<ClaudeEvent> outp)
    {
        if (!e.TryGetProperty("event", out var ev)) return;
        string t = S(ev, "type");
        if (t == "message_start") { currentMessage = ev.TryGetProperty("message", out var m) ? S(m, "id") : null; return; }
        if (t == "content_block_start")
        {
            if (ev.TryGetProperty("content_block", out var cb) && S(cb, "type") == "tool_use" && ev.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number) blocks[ix.GetInt32()] = (S(cb, "name"), S(cb, "id"));
            return;
        }
        if (t != "content_block_delta" || !ev.TryGetProperty("delta", out var d)) return;
        string dt = S(d, "type");
        if (dt == "text_delta" && S(d, "text") is { Length: > 0 } tx) { if (currentMessage != null) streamedText.Add(currentMessage); outp.Add(new ClaudeEvent { Kind = "text", Text = tx }); }
        else if (dt == "thinking_delta" && S(d, "thinking") is { Length: > 0 } th) outp.Add(new ClaudeEvent { Kind = "thinking", Text = th });
    }

    void Assistant(JsonElement e, List<ClaudeEvent> outp)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        string id = S(m, "id");
        bool sub = e.TryGetProperty("parent_tool_use_id", out var pt) && pt.ValueKind == JsonValueKind.String;   // un sous-agent : ses outils seulement
        foreach (var c in content.EnumerateArray())
        {
            string ct = S(c, "type");
            if (ct == "text" && !sub && !streamedText.Contains(id) && S(c, "text") is { Length: > 0 } tx) outp.Add(new ClaudeEvent { Kind = "text", Text = tx });
            else if (ct == "tool_use")
            {
                string tid = S(c, "id"); if (tid.Length > 0 && !seenTools.Add(tid)) continue;
                var input = c.TryGetProperty("input", out var inp) ? inp : default;
                outp.Add(new ClaudeEvent { Kind = "tool", ToolId = tid, ToolName = S(c, "name"), Target = input.ValueKind == JsonValueKind.Object ? TargetOf(S(c, "name"), input) : "", Input = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}" });
            }
        }
        blocks.Clear();
    }

    void User(JsonElement e, List<ClaudeEvent> outp)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var c in content.EnumerateArray())
        {
            if (S(c, "type") != "tool_result") continue;
            string text = "";
            if (c.TryGetProperty("content", out var cc))
            {
                if (cc.ValueKind == JsonValueKind.String) text = cc.GetString();
                else if (cc.ValueKind == JsonValueKind.Array) text = string.Join(" ", cc.EnumerateArray().Select(x => S(x, "type") == "image" ? "(image)" : S(x, "text")));
            }
            outp.Add(new ClaudeEvent { Kind = "result_tool", ToolId = S(c, "tool_use_id"), Text = text, IsError = c.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True });
        }
    }

    // Ce qu'on montre à côté du nom d'un outil : son argument principal, en une ligne courte.
    public static string TargetOf(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        foreach (var k in new[] { "file_path", "path", "notebook_path", "command", "pattern", "url", "query", "prompt", "description", "name", "title", "text", "app", "target" })
            if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0)
            {
                string s = v.GetString();
                if (k == "command" && input.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString().Length > 0 && s.Length > 80) s = d.GetString() + " · " + s;
                int nl = s.IndexOf('\n'); if (nl >= 0) s = s[..nl];
                return s.Length > 120 ? s[..117] + "…" : s;
            }
        return "";
    }

    // Les lignes à écrire sur l'entrée de Claude (JSON sans échappement inutile : les accents et les guillemets passent tels quels).
    static readonly JsonSerializerOptions Out = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string UserMessage(string text) => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new[] { new { type = "text", text } } } }, Out);
    public static string Answer(string requestId, bool allow, string updatedInput, string message)
    {
        object response = allow
            ? new { behavior = "allow", updatedInput = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(updatedInput) ? "{}" : updatedInput) }
            : new { behavior = "deny", message = message ?? "Refusé par Valentin." };
        return JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = requestId, response } }, Out);
    }
    public static string Interrupt(string requestId) => JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "interrupt" } }, Out);

    static string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static long L(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
