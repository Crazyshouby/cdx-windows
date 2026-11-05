using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Le côté « système de fichiers » de l'Explorateur CDX : tout ce que la page demande par des messages « fs.* ».
// Les opérations lourdes (copie, déplacement) passent par Microsoft.VisualBasic.FileIO, qui affiche les boîtes de dialogue de Windows
// pour les conflits et la progression ; la suppression envoie à la corbeille.
static partial class Fs
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, uint type, string? obj, string? page);

    public static object Places()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new[]
        {
            new { name = "Accueil", path = user, ic = "utilisateur" },
            new { name = "Bureau", path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ic = "pc" },
            new { name = "Documents", path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ic = "dossier" },
            new { name = "Images", path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ic = "image" },
            new { name = "Musique", path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ic = "musique" },
            new { name = "Vidéos", path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ic = "video" },
            new { name = "Téléchargements", path = Path.Combine(user, "Downloads"), ic = "telecharger" },
        };
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
            .Select(d => { var (kind, name) = DriveKind(d); return new { name, path = d.Name, ic = kind == "reseau" ? "serveur" : "disque", kind }; }).ToList();
        return new { type = "places", places, drives, home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    }


    // owner : fenêtre parente des boîtes de Windows ; send : où renvoyer les réponses (la page, ou l'auto-test).
    // Un travail long (effacer, vider la corbeille) fait sur un fil à part ; la réponse revient sur le fil de l'interface, si la fenêtre existe encore.
    static void OffUi(IntPtr owner, Action<object> send, Func<string?> work, Func<string?, object> reply)
    {
        var ctx = SynchronizationContext.Current;
        if (ctx == null) { string? e; try { e = work(); } catch (Exception ex) { e = ex.Message; } send(reply(e)); return; }
        Task.Run(() => { string? e; try { e = work(); } catch (Exception ex) { e = ex.Message; } ctx.Post(_ => { if (WindowAlive == null || WindowAlive(owner)) send(reply(e)); }, null); });
    }

    // Comme OffUi, avec plusieurs réponses, envoyées dans l'ordre sur le fil de l'interface. Sans contexte (essais), tout se fait sur place.
    static void OffUiMany(IntPtr owner, Action<object> send, Func<List<object>> work)
    {
        var ctx = SynchronizationContext.Current;
        List<object> Run() { try { return work(); } catch (Exception ex) { return new List<object> { new { type = "fs.done", tab = (string?)null, error = ex.Message } }; } }
        if (ctx == null) { foreach (var o in Run()) send(o); return; }
        Task.Run(() => { var res = Run(); ctx.Post(_ => { if (WindowAlive == null || WindowAlive(owner)) foreach (var o in res) send(o); }, null); });
    }

    static readonly Dictionary<(IntPtr, string), CancellationTokenSource> previews = new();   // l'aperçu en cours de chaque onglet
    public static void Handle(IntPtr owner, Action<object> send, string cmd, JsonElement r)
    {
        string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string Req(string k) => Str(k) ?? throw new FormatException(k + " manquant");   // rattrapé plus bas (fs.done avec l'erreur), comme l'était l'exception levée sur le null
        string?[] Paths() => r.TryGetProperty("paths", out var v) ? v.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string?>();   // un null du tableau reste null : chaque usage l'écarte
        string[] Ids() => r.TryGetProperty("ids", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();   // « ! » : une valeur JSON de type texte ne se lit jamais null
        string? tab = Str("tab");
        try
        {
            switch (cmd)
            {
                case "fs.places": send(Places()); break;
                case "fs.eject":   // hors du fil de l'interface : l'éjection peut attendre jusqu'à une seconde que Windows lâche le volume
                    {
                        string? drive = Str("drive");
                        OffUiMany(owner, send, () => { string? err = Eject(drive); var res = new List<object> { new { type = "fs.done", tab, error = err, ejected = err == null ? drive : null } }; if (err == null) res.Add(Places()); return res; });
                        break;
                    }
                case "fs.list": send(List(Str("path"), tab)); break;
                case "fs.open": Process.Start(new ProcessStartInfo(Req("path")) { UseShellExecute = true }); Host.Log("explorateur : ouvert " + Str("path")); break;
                case "fs.copypath":   // « Copier le chemin » : chaque chemin entre guillemets, un par ligne (comme Windows)
                    { var ps = Paths().Where(p => !string.IsNullOrEmpty(p) && p.Length <= 520).Take(5000).ToArray(); if (ps.Length > 0) Clipboard.SetText(string.Join("\r\n", ps.Select(p => "\"" + p + "\""))); break; }
                case "fs.openwith": if (!ShellData.OpenWithRun(Str("exe"), Str("path"))) send(new { type = "fs.done", tab, error = "Impossible d'ouvrir avec ce programme." }); break;
                case "fs.openas": ShellData.OpenAsDialog(Str("path")); break;
                case "fs.terminal": ShellData.OpenTerminal(Str("path")); break;
                case "fs.sendto":
                    {
                        var (sdir, sexe) = ShellData.SendToTarget(Str("item")); var ps = Paths();
                        if (sdir != null) RunTransfer(owner, send, ps, sdir, false, null, tab);   // une copie ; si un nom est pris, la page demande
                        else if (sexe != null) ShellData.RunProgramOn(sexe, ps);
                        else send(new { type = "fs.done", tab, error = "Destination introuvable." });
                        break;
                    }
                case "fs.new":
                    {
                        string? np = ShellData.CreateNew(Str("dir"), Str("ext"));
                        if (np != null) { Undo.For(owner).Push(Undo.Created("Nouveau fichier", new[] { np })); send(Undo.State(owner)); }
                        send(np != null ? new { type = "fs.done", tab, select = np, error = (string?)null } : (object)new { type = "fs.done", tab, select = (string?)null, error = "Impossible de créer ce fichier." });
                        break;
                    }
                case "fs.newshortcut":
                    {
                        string? sp = ShellData.CreateShortcut(Str("dir"), Str("name"), Str("target"));
                        if (sp != null) { Undo.For(owner).Push(Undo.Created("Nouveau raccourci", new[] { sp })); send(Undo.State(owner)); }
                        send(sp != null ? new { type = "fs.done", tab, select = sp, error = (string?)null } : (object)new { type = "fs.done", tab, select = (string?)null, error = "Impossible de créer le raccourci (cible vide ?)." });
                        break;
                    }
                case "fs.props": SHObjectProperties(owner, 0x2 /* SHOP_FILEPATH */, Str("path"), null); break;
                case "fs.newfolder":
                    {
                        string p = UniquePath(Req("dir"), "Nouveau dossier");
                        Directory.CreateDirectory(p);
                        Undo.For(owner).Push(Undo.Created("Nouveau dossier", new[] { p })); send(Undo.State(owner));
                        send(new { type = "fs.done", tab, select = p });
                        break;
                    }
                case "fs.rename":
                    {
                        string src = Req("path"), name = Req("name");
                        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length == 0) { send(new { type = "fs.done", tab, error = "Nom invalide : les caractères \\ / : * ? \" < > | sont interdits." }); break; }
                        string dst = Path.Combine(Path.GetDirectoryName(src.TrimEnd('\\')) ?? throw new IOException("La racine d'un disque ne se renomme pas."), name);   // (avant : ArgumentNullException, rattrapée de même)
                        if (!string.Equals(src, dst, StringComparison.Ordinal))
                        {
                            if (Directory.Exists(src)) Directory.Move(src, dst); else File.Move(src, dst);
                            Undo.For(owner).Push(Undo.Moved("Renommer", new List<(string, string)> { (src, dst) })); send(Undo.State(owner));
                        }
                        send(new { type = "fs.done", tab, select = dst });
                        break;
                    }
                case "fs.delete":   // vers la corbeille : on garde l'entrée de chacun pour pouvoir le restaurer (Annuler)
                    {
                        // Hors du fil de l'interface : Windows parcourt tout l'arbre avant de le déplacer (un dossier de développement de 1 Go = des dizaines de secondes) ;
                        // sur le fil de l'interface, la fenêtre se figeait et la suppression semblait ne jamais se faire.
                        var ps = Paths().OfType<string>().Where(p => !string.IsNullOrEmpty(p) && Path.IsPathRooted(p) && Path.GetFileName(p.TrimEnd('\\')).Length > 0 && Path.Exists(p)).ToArray();   // (OfType écarte les null, que le Where écartait déjà)
                        if (ps.Length == 0) { send(new { type = "fs.done", tab }); break; }
                        // Un disque sans corbeille (clé USB, partage, subst) : rien n'est supprimé avant que la page ait fait confirmer la suppression définitive.
                        var perm = ps.Where(p => !Recycle.HasBin(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool permOk = r.TryGetProperty("permOk", out var po) && po.ValueKind == JsonValueKind.True;
                        if (perm.Count > 0 && !permOk) { send(new { type = "fs.confirmperm", tab, paths = ps, perm = perm.ToArray() }); break; }
                        var gone = new List<BinEntry>(); int noBin = 0, kept = 0; string? firstErr = null;
                        void Finish(string? error)
                        {
                            if (gone.Count > 0) { Undo.For(owner).Push(Undo.Deleted(gone.Count == 1 ? "Supprimer" : "Supprimer " + gone.Count + " éléments", gone)); send(Undo.State(owner)); }
                            string? msg = error ?? firstErr;
                            if (msg == null && kept > 0) msg = kept + " élément" + (kept > 1 ? "s n'ont" : " n'a") + " pas été supprimé" + (kept > 1 ? "s" : "") + " : la corbeille ne pouvait pas " + (kept > 1 ? "les" : "le") + " recevoir.";
                            if (msg == null && noBin > 0) msg = noBin + " élément" + (noBin > 1 ? "s ont" : " a") + " été supprimé" + (noBin > 1 ? "s" : "") + " définitivement (pas de corbeille sur ce disque).";
                            send(new { type = "fs.done", tab, error = msg });
                        }
                        void Recycle1(string p)
                        {
                            if (perm.Contains(p)) { string? e1 = Recycle.DeletePermanently(new[] { p }); lock (gone) { if (e1 == null) noBin++; else firstErr ??= e1; } return; }
                            try { var e = Recycle.DeleteToBin(p, owner); lock (gone) { if (e != null) gone.Add(e); else noBin++; } }
                            catch (OperationCanceledException) { lock (gone) kept++; }   // Windows a prévenu (trop gros pour la corbeille) et l'utilisateur a refusé
                        }
                        if (Queue == null) { string? err = null; foreach (var p in ps) try { Recycle1(p); } catch (Exception ex) { err ??= ex.Message; } Finish(err); break; }
                        var ctx = SynchronizationContext.Current; string first = ps[0]; string? from = Path.GetDirectoryName(first.TrimEnd('\\'));
                        Queue.Enqueue("delete", (ps.Length == 1 ? Path.GetFileName(first.TrimEnd('\\')) : ps.Length + " éléments") + " → corbeille", job =>
                        {
                            job.Report(0, ps.Length);
                            for (int i = 0; i < ps.Length; i++)
                            {
                                job.WaitIfPaused();   // entre deux éléments : Windows ne sait pas interrompre une mise à la corbeille commencée
                                Recycle1(ps[i]);
                                job.Report(i + 1, ps.Length, Path.GetFileName(ps[i].TrimEnd('\\')));
                            }
                        }, j =>
                        {
                            void Back() { if (WindowAlive(owner)) Finish(j.State == JobState.Failed ? j.Error : null); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        }, j => { j.Items = ps.Length; j.From = FolderLabel(from ?? first); j.To = "Corbeille"; j.Path = from; });
                        break;
                    }
                case "fs.deleteperm":   // Maj+Suppr, après confirmation dans la page : pas de corbeille, pas d'annulation
                    {
                        var ps = Paths();
                        if (Queue == null) { send(new { type = "fs.done", tab, error = Recycle.DeletePermanently(ps) }); break; }
                        var ctx = SynchronizationContext.Current; string? first = ps.FirstOrDefault(x => !string.IsNullOrEmpty(x));
                        if (first == null) { send(new { type = "fs.done", tab }); break; }
                        Queue.Enqueue("delete", ps.Length == 1 ? Path.GetFileName(first.TrimEnd('\\')) : ps.Length + " éléments", job =>
                        {
                            string? e = Copier.DeleteMany(ps, job);
                            if (e != null) throw new IOException(e);
                        }, j =>
                        {
                            void Back() { if (WindowAlive(owner)) send(new { type = "fs.done", tab, error = j.State == JobState.Failed ? j.Error : null }); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        }, j => { j.Items = ps.Length; j.From = FolderLabel(Path.GetDirectoryName(first.TrimEnd('\\')) ?? first); j.Path = Path.GetDirectoryName(first.TrimEnd('\\')); });
                        break;
                    }
                case "fs.restore":   // depuis la corbeille : chaque élément retrouve sa place (jamais d'écrasement)
                    {
                        var ids = Ids();
                        OffUiMany(owner, send, () =>
                        {
                            var back = new List<string>(); var errs = new List<string>(); var res = new List<object>();
                            foreach (var id in ids) { var (err, dest) = Recycle.Restore(id); if (err == null) back.Add(dest!); else errs.Add(err); }   // « ! » : sans erreur, Restore rend toujours le chemin retrouvé
                            if (back.Count > 0) { Undo.For(owner).Push(Undo.Created(back.Count == 1 ? "Restaurer" : "Restaurer " + back.Count + " éléments", back)); res.Add(Undo.State(owner)); }
                            res.Add(new { type = "fs.done", tab, error = errs.Count == 0 ? null : errs[0] + (errs.Count > 1 ? " (et " + (errs.Count - 1) + " autre" + (errs.Count > 2 ? "s" : "") + ")" : "") });
                            return res;
                        });
                        break;
                    }
                case "fs.purge":   // effacer pour de bon des éléments de la corbeille (après confirmation) ; hors du fil de l'interface, comme la mise à la corbeille
                    {
                        var ids = Ids().Where(Recycle.IsEntryId).ToArray();
                        OffUi(owner, send, () => Recycle.Purge(ids), err => new { type = "fs.done", tab, error = err });
                        break;
                    }
                case "fs.emptybin": OffUi(owner, send, () => Recycle.EmptyAll() ? null : "La corbeille n'a pas pu être vidée.", err => new { type = "fs.done", tab, error = err }); break;
                case "fs.undo":
                case "fs.redo":
                    {
                        // hors du fil de l'interface : défaire un déplacement entre disques, une mise à la corbeille, peut durer (UndoLog est sous verrou)
                        bool isRedo = cmd == "fs.redo";
                        OffUiMany(owner, send, () =>
                        {
                            var log = Undo.For(owner); var (label, uerr) = isRedo ? log.DoRedo() : log.DoUndo();
                            return new List<object> { new { type = "fs.undone", redo = isRedo, label, error = uerr }, Undo.State(owner), new { type = "fs.done", tab } };
                        });
                        break;
                    }
                case "fs.preview":   // hors du fil de l'interface (un document lourd figeait toutes les fenêtres), annulé par l'aperçu suivant du même onglet
                    {
                        string? pp = Str("path"); var key = (owner, tab ?? ""); var cts = new CancellationTokenSource();
                        lock (previews) { if (previews.TryGetValue(key, out var old)) old.Cancel(); previews[key] = cts; }
                        var ctx = SynchronizationContext.Current;
                        _ = Task.Run(() =>
                        {
                            object res; try { res = Preview(pp, tab, cts.Token); } catch (OperationCanceledException) { return; } catch (Exception ex) { res = new { type = "fs.preview", path = pp, kind = "error", error = ex.Message }; }
                            lock (previews) { if (previews.TryGetValue(key, out var cur) && cur == cts) previews.Remove(key); }
                            if (cts.IsCancellationRequested) return;
                            void Back() { if (WindowAlive(owner)) send(res); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        });
                        break;
                    }
                case "fs.copy": SetClipboard(Paths(), r.TryGetProperty("cut", out var c) && c.GetBoolean()); break;
                case "fs.paste":
                    {
                        var (files, cut) = ClipboardFiles(); string? pdir = Str("dir");
                        if (files.Count == 0) { send(new { type = "fs.done", tab }); break; }
                        RunTransfer(owner, send, files, pdir, cut, null, tab, cut ? () => Clipboard.Clear() : null);
                        break;
                    }
                case "fs.drop":
                    {
                        bool mv = r.TryGetProperty("move", out var mvv) && mvv.ValueKind == JsonValueKind.True;
                        var ps = Paths(); string? dir = Str("dir");
                        RunTransfer(owner, send, ps, dir, mv, Str("conflict"), tab);
                        break;
                    }
            }
        }
        catch (OperationCanceledException) { send(new { type = "fs.done", tab }); }
        catch (Exception ex)
        {
            Host.Log($"explorateur : {cmd} a échoué : {ex.Message}");
            send(new { type = "fs.done", tab, error = ex.Message });
        }
    }
}
