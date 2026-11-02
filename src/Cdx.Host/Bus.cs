using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cdx;

// Le bus de la suite : un tube \\.\pipe\cdx.<nom> par application, des objets JSON d'une ligne (voir cdx-ui/docs/SUITE.md).
static class Bus
{
    public const int MaxLine = 65536;
    public const int MaxInstances = 8;
    public static readonly string[] Apps = { "shell", "explorer", "browser", "repos", "transfers", "notes", "terminal", "agenda", "mail", "pilote", "audiolinker", "messages" };

    // CDX_PIPE_PREFIX remplace « cdx. » (réservé aux essais : on ne touche pas au vrai bus).
    internal static string Prefix => Environment.GetEnvironmentVariable("CDX_PIPE_PREFIX") is { Length: > 0 } p ? p : "cdx.";

    // Le nom du tube d'une application (et, suivi de « .ctl », de son canal de test).
    public static string PipeName(string app) => Prefix + app;

    // Les fichiers locaux que le navigateur sait afficher (SUITE.md §3.3, open-file) : une seule liste pour celui qui les demande (l'Explorateur) et celui qui les ouvre.
    public static readonly string[] BrowsableExtensions = { ".html", ".htm", ".pdf", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".txt", ".mp3", ".ogg", ".wav", ".mp4", ".webm" };

    public static bool IsBrowsableFile(string p)
        => !string.IsNullOrEmpty(p) && p.Length <= 520 && p.IndexOf((char)0) < 0 && Path.IsPathRooted(p) && File.Exists(p)
           && Array.IndexOf(BrowsableExtensions, Path.GetExtension(p).ToLowerInvariant()) >= 0;

    // ---------- réception ----------
    // `handler(type, from, data)` rend true si le message est connu et valide ; sinon il est refusé (noté dans le journal, la connexion continue).
    // Il est appelé sur le fil qui a appelé Listen (le fil de l'interface). `data` n'est valable que pendant l'appel.
    public static void Listen(string app, Func<string, string, JsonElement, bool> handler)
    {
        var ctx = SynchronizationContext.Current ?? throw new InvalidOperationException("Bus.Listen doit être appelé depuis le fil de l'interface");
        string pipe = Prefix + app;
        new Thread(() =>
        {
            bool first = true;
            while (true)
            {
                NamedPipeServerStream srv = null;
                try
                {
                    // CurrentUserOnly : réservé à l'utilisateur courant ; le tube refuse aussi les connexions distantes. La première instance du nom est
                    // créée seule (si un autre processus tient déjà le nom, on le dit au lieu de servir à côté).
                    srv = new NamedPipeServerStream(pipe, PipeDirection.In, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    if (first) { Host.Log("bus : à l'écoute sur " + pipe); first = false; }
                    srv.WaitForConnection();
                    var conn = srv; srv = null;
                    new Thread(() => ReadConnection(conn, ctx, handler)) { IsBackground = true }.Start();
                }
                catch (Exception ex)
                {
                    try { srv?.Dispose(); } catch { }
                    Host.Log("bus : " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true }.Start();
    }

    static void ReadConnection(NamedPipeServerStream conn, SynchronizationContext ctx, Func<string, string, JsonElement, bool> handler)
    {
        using (conn)
        {
            var acc = new StringBuilder(); var buf = new byte[4096]; var dec = Encoding.UTF8.GetDecoder(); var chars = new char[4096];
            try
            {
                while (true)
                {
                    int n = conn.Read(buf, 0, buf.Length);
                    if (n <= 0) return;
                    int c = dec.GetChars(buf, 0, n, chars, 0);
                    for (int i = 0; i < c; i++)
                    {
                        if (chars[i] == '\n') { Dispatch(acc.ToString(), ctx, handler); acc.Clear(); }
                        else { acc.Append(chars[i]); if (acc.Length > MaxLine) { Host.Log("bus : ligne de plus de 64 Ko, connexion coupée"); return; } }
                    }
                }
            }
            catch (Exception ex) { Host.Log("bus : connexion : " + ex.Message); }
        }
    }

    static void Dispatch(string line, SynchronizationContext ctx, Func<string, string, JsonElement, bool> handler)
    {
        if (line.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString().Length > 64) { Host.Log("bus : message sans type valide, ignoré"); return; }
            string type = t.GetString();
            string from = r.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String && f.GetString().Length <= 32 ? f.GetString() : "?";
            JsonElement data = r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            ctx.Post(_ =>
            {
                try { if (!handler(type, from, data)) Host.Log($"bus : « {type} » refusé (inconnu ou invalide) de {from}"); }
                catch (Exception ex) { Host.Log($"bus : « {type} » : {ex.Message}"); }
            }, null);
        }
        catch (JsonException) { Host.Log("bus : ligne qui n'est pas du JSON, ignorée"); }
    }

    // ---------- émission ----------
    // Un message « tire et oublie » vers l'application `to`. True : écrit. `launch` : si le tube n'existe pas, lance l'application d'après apps.json et réessaie
    // pendant 6 s.
    public static bool Send(string to, string type, object data = null, string from = null, bool launch = false)
    {
        if (!Apps.Contains(to)) return false;
        byte[] line = Encoding.UTF8.GetBytes(Compose(type, from ?? Host.AppName, data) + "\n");
        if (line.Length > MaxLine) return false;
        bool launched = false;
        for (int i = 0; i < (launch ? 40 : 1); i++)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", Prefix + to, PipeDirection.Out, PipeOptions.None);
                c.Connect(launch && launched ? 100 : 250);
                c.Write(line, 0, line.Length); c.Flush();
                return true;
            }
            catch (Exception) { }
            if (!launch) break;
            if (!launched)
            {
                launched = true;
                string exe = Suite.AppPath(to);
                if (exe == null) return false;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) }); } catch (Exception ex) { Host.Log("bus : lancement de " + to + " : " + ex.Message); return false; }
            }
            Thread.Sleep(150);
        }
        return false;
    }

    public static string Compose(string type, string from, object data)
        => JsonSerializer.Serialize(new { type, from, data = data ?? new { } });
}

