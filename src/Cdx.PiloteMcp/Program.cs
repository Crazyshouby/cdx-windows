using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Cdx.PiloteMcp;

// cdx-pilote-mcp.exe : le serveur MCP « cdx-suite » (JSON-RPC 2.0, un message par ligne sur l'entrée et la sortie standard), lancé par Claude Code d'après
// le fichier --mcp-config que Pilote écrit. Il ne fait rien lui-même : chaque tools/list et tools/call est relayé au tube \\.\pipe\<préfixe>pilote.outils
// de cdx-pilote.exe (une connexion par requête, une ligne JSON dans chaque sens). Tout journal va sur l'erreur standard, jamais sur la sortie.
static class Program
{
    static readonly string[] Protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
    static string Pipe => (Environment.GetEnvironmentVariable("CDX_PIPE_PREFIX") is { Length: > 0 } p ? p : "cdx.") + "pilote.outils";

    static int Main()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            string r;
            try { r = Handle(line); }
            catch (Exception ex) { Console.Error.WriteLine("cdx-pilote-mcp : " + ex); r = null; }
            if (r != null) output.WriteLine(r);
        }
        return 0;
    }

    static string Handle(string line)
    {
        JsonNode msg;
        try { msg = JsonNode.Parse(line); } catch (Exception) { return Error(null, -32700, "JSON illisible"); }
        if (msg is not JsonObject o) return Error(null, -32600, "Requête invalide");
        string method = o["method"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
        bool notification = !o.ContainsKey("id");
        JsonNode id = notification ? null : o["id"]?.DeepClone();
        if (method == null) return notification ? null : Error(id, -32600, "Méthode absente");
        switch (method)
        {
            case "initialize":
            {
                string want = o["params"] is JsonObject p && p["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = want != null && Protocols.Contains(want) ? want : Protocols[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "cdx-suite", ["version"] = "1.0.0" },
                    ["instructions"] = "La suite CDX de Valentin, pilotée depuis Pilote : suite_state d'abord, puis les programmes (app_open, app_state, app_command), le navigateur (browser_open, browser_read, browser_find, browser_click, browser_fill, browser_screenshot) et les projets (project_folder).",
                });
            }
            case "ping": return notification ? null : Result(id, new JsonObject());
            case "tools/list":
            {
                if (notification) return null;
                var r = Relay(new JsonObject { ["op"] = "list" });
                return r == null ? Error(id, -32603, "Pilote ne répond pas (cdx-pilote.exe tourne-t-il ?)") : Result(id, new JsonObject { ["tools"] = r["tools"]?.DeepClone() ?? new JsonArray() });
            }
            case "tools/call":
            {
                if (notification) return null;
                var p = o["params"] as JsonObject ?? new JsonObject();
                var r = Relay(new JsonObject { ["op"] = "call", ["name"] = p["name"]?.DeepClone(), ["args"] = p["arguments"]?.DeepClone() ?? new JsonObject() }, 300000);
                if (r == null) return Result(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Pilote ne répond pas (cdx-pilote.exe tourne-t-il ?)" }), ["isError"] = true });
                return Result(id, r);
            }
            default: return notification ? null : Error(id, -32601, "Méthode inconnue : " + method);
        }
    }

    static JsonObject Relay(JsonObject req, int timeoutMs = 30000)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.None);
            c.Connect(5000);
            // leaveOpen : les deux flux ne ferment pas le tube ; sinon l'écrivain, disposé après le lecteur, vidait sa mémoire dans un tube déjà fermé et la réponse (déjà lue) était perdue dans l'exception.
            var wr = new StreamWriter(c, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" }; using var rd = new StreamReader(c, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            wr.WriteLine(req.ToJsonString());
            var t = rd.ReadLineAsync();
            if (!t.Wait(timeoutMs) || t.Result == null) return null;
            return JsonNode.Parse(t.Result) as JsonObject;
        }
        catch (Exception ex) { Console.Error.WriteLine("cdx-pilote-mcp : tube : " + ex.Message); return null; }
    }

    static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    static string Error(JsonNode id, int code, string message) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
