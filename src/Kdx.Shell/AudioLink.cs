using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Kdx;

// La liaison avec AudioLinker, quand il tourne sur la même machine : un tube nommé « kdx.audiolinker » (voir AudioLinker/src/KdxLink.cpp pour le
// protocole : des lignes de JSON dans les deux sens). On s'y connecte dès qu'il existe, on le retente toutes les 1,5 s sinon, et la barre
// n'affiche les boutons du son que pendant que le tube répond. Rien n'est évalué : seuls cinq types de messages sortent (liste blanche ci-dessous)
// et l'état reçu est recopié champ par champ vers la barre, jamais transmis tel quel.
sealed class AudioLink
{
    public const string DefaultPipe = "kdx.audiolinker";

    readonly string pipeName;
    readonly Action<object> toBar;                     // appelé sur n'importe quel fil : l'appelant le ramène sur le fil de l'interface
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly CancellationTokenSource cts = new();
    volatile NamedPipeClientStream pipe;
    volatile bool connected;
    Task loop;

    public AudioLink(Action<object> toBar, string pipeName = DefaultPipe) { this.toBar = toBar; this.pipeName = pipeName; }

    public bool Connected => connected;

    public void Start() => loop = Task.Run(RunAsync);

    public void Stop()
    {
        cts.Cancel();
        try { pipe?.Dispose(); } catch { }
        try { loop?.Wait(1000); } catch { }
    }

    async Task RunAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                // Asynchrone : un tube synchrone fait attendre une écriture tant qu'une lecture est en cours sur le même descripteur.
                var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using (p)
                {
                    await p.ConnectAsync(250, cts.Token);
                    pipe = p; connected = true;
                    Host.Log("AudioLinker connecté");
                    using var rd = new StreamReader(p, new UTF8Encoding(false));
                    await SendAsync("audio.get");
                    string line;
                    while ((line = await rd.ReadLineAsync(cts.Token)) != null) OnLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (IOException) { }
            catch (Exception ex) { Host.Log("AudioLinker : " + ex.Message); }
            if (connected) { connected = false; pipe = null; Host.Log("AudioLinker déconnecté"); toBar(new { type = "audio", on = false }); }
            try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { }
        }
    }

    void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out var t) || t.GetString() != "audio.state") return;
            if (!r.TryGetProperty("data", out var dt) || dt.ValueKind != JsonValueKind.Object) return;
            r = dt;   // le message est une enveloppe { type, from, data } (SUITE.md §2)
            var setups = new List<object>();
            if (r.TryGetProperty("setups", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var s in arr.EnumerateArray().Take(64))
                    if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        setups.Add(new { name = Clip(n.GetString()), count = s.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0 });
            toBar(new
            {
                type = "audio", on = true,
                running = Bool(r, "running"), starting = Bool(r, "starting"), muted = Bool(r, "muted"),
                volume = r.TryGetProperty("volume", out var v) && v.TryGetInt32(out var vi) ? Math.Clamp(vi, 0, 100) : 100,
                playing = r.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.String ? Clip(pl.GetString()) : "",
                setups,
            });
        }
        catch (Exception ex) { Host.Log("AudioLinker : ligne illisible (" + ex.Message + ")"); }
    }

    static bool Bool(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string Clip(string s) => s == null ? "" : s.Length > 200 ? s[..200] : s;

    // Commandes vers AudioLinker. Sans connexion, elles sont perdues : la barre n'affiche les boutons que connectée.
    public void Refresh() => _ = SendAsync("audio.get");
    public void Toggle() => _ = SendAsync("audio.toggle");
    public void Mute(bool on) => _ = SendAsync("audio.mute", new { on });
    public void Volume(int v, bool commit) => _ = SendAsync("audio.volume", new { v = Math.Clamp(v, 0, 100), commit });
    public void Setup(string name) { if (!string.IsNullOrEmpty(name) && name.Length <= 200) _ = SendAsync("audio.setup", new { name }); }

    async Task SendAsync(string type, object data = null)
    {
        var p = pipe;
        if (p == null || !p.IsConnected) return;
        byte[] bytes = Encoding.UTF8.GetBytes(Bus.Compose(type, "shell", data) + "\n");
        await writeLock.WaitAsync();
        try { await p.WriteAsync(bytes, cts.Token); await p.FlushAsync(cts.Token); }
        catch (Exception ex) { Host.Log("AudioLinker : envoi impossible (" + ex.Message + ")"); }
        finally { writeLock.Release(); }
    }
}
