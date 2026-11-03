using System.Text.Json;

namespace Cdx;

// Ce que la fenêtre « Transferts » sait d'un transfert : copie, déplacement, suppression, téléchargement, torrent... Le programme qui fait le travail (l'Explorateur, le navigateur) en reste le
// maître : il annonce l'état (message « xfer » du bus) et reçoit les ordres de la fenêtre (« xfer-ctl » : pause, reprise, annulation, nouvel essai).
sealed class XferInfo
{
    public int Id;
    public string? Kind;     // copy · move · delete · download · torrent
    public string? Title;    // « 3 éléments », « fichier.zip »
    public string? State;    // queued · running · paused · done · failed · canceled
    public string? From, To; // d'où et vers où, en clair (un dossier, un site)
    public string? Current;  // l'élément en cours
    public string? Error;
    public string? Path;     // ce que « Ouvrir » et « Afficher dans le dossier » désignent (donné par le programme, jamais par la page)
    public long Done, Total; // octets (ou éléments) ; Total 0 ou moins : inconnu
    public double Speed;     // octets par seconde, lissé
    public int Items;        // nombre d'éléments, quand le transfert en regroupe plusieurs
    public bool CanPause, CanRetry;
    public object? Extra;    // champs propres à un genre (torrent : pairs, partage...)
}

static class Xfer
{
    // Rend l'état de tout ce qui est en cours (pour renvoyer à une fenêtre qui vient de s'ouvrir) ; posé par le programme.
    public static Func<IEnumerable<XferInfo>>? Active;
    // Exécute un ordre de la fenêtre (id, action) ; rend false s'il est refusé. Posé par le programme ; appelé sur le fil de l'interface.
    public static Func<int, string, bool>? Control;

    static readonly object lk = new();
    static readonly Dictionary<int, XferInfo> pending = new();
    static readonly Dictionary<int, DateTime> firstSeen = new();
    static readonly HashSet<int> opened = new();       // les transferts pour lesquels on a déjà fait venir la fenêtre
    static readonly AutoResetEvent wake = new(false);
    static Thread? thread;
    public static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(1500);   // un transfert plus court que cela ne dérange personne

    // Annonce (ou met à jour) un transfert. Appelable de n'importe quel fil ; l'envoi se fait sur un fil à part, au plus quatre fois par seconde, le dernier état l'emporte.
    public static void Publish(XferInfo i)
    {
        if (i == null) return;
        lock (lk)
        {
            pending[i.Id] = i;
            if (!firstSeen.ContainsKey(i.Id)) firstSeen[i.Id] = DateTime.UtcNow;
            thread ??= StartThread();
        }
        wake.Set();
    }

    static Thread StartThread()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "xfer" }; t.Start(); return t;
    }

    // Redit à la fenêtre tout ce qui est en cours (elle vient de s'ouvrir, ou de se relancer).
    public static void Resync()
    {
        try { foreach (var i in Active?.Invoke() ?? Array.Empty<XferInfo>()) Publish(i); } catch (Exception ex) { Host.Log("transferts : resynchronisation : " + ex.Message); }
    }

    static void Loop()
    {
        DateTime beat = DateTime.UtcNow;
        while (true)
        {
            // Une exception ici (sur un fil d'arrière-plan) arrêterait tout le processus : elle est journalisée, et la boucle repart.
            try
            {
                wake.WaitOne(1000);
                List<XferInfo> batch;
                lock (lk) { batch = pending.Values.ToList(); pending.Clear(); }
                // une annonce de temps en temps même sans changement : la fenêtre sait ainsi que le programme est encore là
                if (batch.Count == 0 && (DateTime.UtcNow - beat).TotalSeconds >= 2)
                {
                    try { batch = (Active?.Invoke() ?? Array.Empty<XferInfo>()).Where(i => i.State is "running" or "paused" or "queued").ToList(); } catch { }
                }
                if (batch.Count > 0) beat = DateTime.UtcNow;
                foreach (var i in batch) Send(i);
                if (batch.Count > 0) Thread.Sleep(250);
            }
            catch (Exception ex) { Host.Log("transferts : " + ex.Message); Thread.Sleep(1000); }
        }
    }

    static void Send(XferInfo i)
    {
        bool launch = false;
        lock (lk)
        {
            bool live = i.State is "running" or "paused" or "queued";
            // un transfert vu pour la première fois par l'annonce périodique (Active), sans être passé par Publish
            if (live && !firstSeen.TryGetValue(i.Id, out _)) firstSeen[i.Id] = DateTime.UtcNow;
            // la fenêtre vient d'elle-même à un transfert qui dure, ou qui échoue (une seule fois par transfert : si on la ferme, on ne la rouvre pas)
            if (!opened.Contains(i.Id) && (i.State == "failed" || (live && DateTime.UtcNow - firstSeen[i.Id] >= OpenAfter))) { launch = true; opened.Add(i.Id); }
            if (!live) { firstSeen.Remove(i.Id); opened.Remove(i.Id); }
        }
        var data = new
        {
            id = i.Id, kind = i.Kind, title = i.Title, state = i.State, from = i.From, to = i.To, current = i.Current, error = i.Error, path = i.Path,
            done = i.Done, total = i.Total, speed = i.Speed, items = i.Items, canPause = i.CanPause, canRetry = i.CanRetry, extra = i.Extra,
        };
        try { Bus.Send("transfers", "xfer", data, launch: launch); } catch (Exception ex) { Host.Log("transferts : envoi : " + ex.Message); }
    }

    // Les messages du bus que tout programme producteur comprend : xfer-sync (la fenêtre demande l'état) et xfer-ctl { id, action } (un ordre de la fenêtre).
    public static bool OnBus(string type, JsonElement data)
    {
        if (type == "xfer-sync") { Resync(); return true; }
        if (type != "xfer-ctl") return false;
        if (!data.TryGetProperty("id", out var idv) || idv.ValueKind != JsonValueKind.Number || !idv.TryGetInt32(out int id)) return false;
        if (!data.TryGetProperty("action", out var av) || av.ValueKind != JsonValueKind.String) return false;
        string? action = av.GetString();
        if (action is not ("pause" or "resume" or "cancel" or "retry")) return false;
        return Control?.Invoke(id, action) ?? false;
    }

    // Demande à la fenêtre de venir devant (la lance au besoin).
    public static void Show() => _ = Task.Run(() => Bus.Send("transfers", "show", new { }, launch: true));
}
