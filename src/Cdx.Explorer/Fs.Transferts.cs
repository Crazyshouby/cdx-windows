using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Copier, déplacer, déposer : plan des conflits, presse-papiers, transferts suivis et annulables. Sorti de Fs.cs (audit du 3/11/2026, 4.6).
static partial class Fs
{
    internal static string UniquePath(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        if (!Path.Exists(p)) return p;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++) { p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!Path.Exists(p)) return p; }
    }

    static void SetClipboard(string[] paths, bool cut)
    {
        var sc = new StringCollection(); sc.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));   // 2 = déplacer, 1 = copier : compatible avec l'Explorateur de Windows
        Clipboard.SetDataObject(data, true);
    }

    // Glisser-déposer : déplace (move) ou copie des éléments vers un dossier. Un dossier ne se place jamais dans lui-même ; déplacer dans le dossier d'origine ne fait rien ; copier dans le dossier
    // d'origine donne « nom (2) ». Un nom déjà pris dans la destination est un CONFLIT : sans consigne (onConflict null), rien n'est fait et les noms sont rendus pour que la page demande ;
    // avec « replace » (remplacer), « keep » (garder les deux : « nom (2) ») ou « skip » (ignorer), l'opération suit. Les conflits ne passent jamais par les boîtes de Windows : elles s'ouvrent
    // sans fenêtre parente, peuvent rester cachées derrière les autres, et bloquent le fil d'interface pendant le glisser-déposer.
    public sealed record TransferResult(string Error, List<string> Conflicts, List<(string src, string dst)> Done = null);

    // Ce qu'un transfert va faire, une fois les conflits réglés : chaque élément avec sa destination. `Error` : refusé d'avance ; `Conflicts` : des noms pris, il faut une consigne.
    sealed record TransferPlan(List<PlanItem> Items, bool Move, string Dir, string Error, List<string> Conflicts);

    static TransferPlan Plan(IEnumerable<string> sources, string dir, bool move, string onConflict)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length > 520 || !Path.IsPathRooted(dir) || !Directory.Exists(dir)) return new(null, move, dir, "Dossier de destination introuvable.", null);
        string dirN = dir.TrimEnd('\\');
        var items = new List<(string src, bool isDir, string name, bool sameDir)>();
        foreach (var raw in sources)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 520 || !Path.IsPathRooted(raw)) continue;
            string src = raw.TrimEnd('\\'); bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) continue;
            string name = Path.GetFileName(src);
            if (name.Length == 0) continue;   // la racine d'un disque ne se déplace pas
            if (isDir && (string.Equals(src, dirN, StringComparison.OrdinalIgnoreCase) || dirN.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))) return new(null, move, dir, "Impossible de placer un dossier dans lui-même.", null);
            bool sameDir = string.Equals(Path.GetDirectoryName(src), dirN, StringComparison.OrdinalIgnoreCase);
            if (move && sameDir) continue;
            items.Add((src, isDir, name, sameDir));
        }
        var conflicts = items.Where(i => !i.sameDir && Path.Exists(Path.Combine(dir, i.name))).Select(i => i.name).ToList();
        if (conflicts.Count > 0 && onConflict == null) return new(null, move, dir, null, conflicts);
        var plan = new List<PlanItem>();
        foreach (var (src, isDir, name, sameDir) in items)
        {
            string dst = Path.Combine(dir, name); bool exists = !sameDir && Path.Exists(dst), overwrite = false;
            if (sameDir) dst = UniquePath(dir, name);
            else if (exists)
            {
                if (onConflict == "skip") continue;
                if (onConflict == "keep") dst = UniquePath(dir, name); else overwrite = onConflict == "replace";
            }
            plan.Add(new PlanItem(src, isDir, dst, overwrite));
        }
        return new(plan, move, dir, null, null);
    }

    static TransferResult ResultOf(CopyOutcome o)
    {
        string err = o.Errors.Count == 0 ? null : o.Errors[0] + (o.Errors.Count > 1 ? " (et " + (o.Errors.Count - 1) + " autre" + (o.Errors.Count > 2 ? "s" : "") + ")" : "");
        return new(err, null, o.Done);
    }

    public static TransferResult Transfer(IEnumerable<string> sources, string dir, bool move, string onConflict = null)
    {
        var p = Plan(sources, dir, move, onConflict);
        if (p.Error != null || p.Conflicts != null) return new(p.Error, p.Conflicts);
        return ResultOf(Copier.Run(p.Items, p.Move, null));
    }

    // La file de tâches de fond (posée par le programme : sans elle, par exemple dans l'auto-test, les transferts se font sur place) et la question « cette fenêtre existe-t-elle encore ? ».
    internal static JobQueue Queue;
    internal static Func<IntPtr, bool> WindowAlive = _ => true;

    // Le « 3 éléments » ou le nom d'un seul, pour intituler un transfert.
    static string TitleOf(List<PlanItem> items) => items.Count == 1 ? Path.GetFileName(items[0].Src) : items.Count + " éléments";
    static string FolderLabel(string p) { string n = Path.GetFileName(p.TrimEnd('\\')); return string.IsNullOrEmpty(n) ? p : n; }

    // Le transfert d'une fenêtre : conflits d'abord (la page demande), puis, si la file existe, en tâche de fond (le résultat revient par `send` quand c'est fini) ; sinon sur place.
    // `afterOk` : à faire une fois le transfert réussi (vider le presse-papiers après un « couper »).
    static void RunTransfer(IntPtr owner, Action<object> send, IEnumerable<string> sources, string dir, bool move, string onConflict, string tab, Action afterOk = null)
    {
        var list = sources.ToList(); var p = Plan(list, dir, move, onConflict);
        if (p.Error != null || p.Conflicts != null) { send(Outcome(new(p.Error, p.Conflicts), tab, list, dir, move)); return; }
        if (p.Items.Count == 0) { send(new { type = "fs.done", tab }); return; }
        if (Queue == null)
        {
            var res = ResultOf(Copier.Run(p.Items, move, null)); RecordTransfer(owner, send, res, move); if (res.Error == null) afterOk?.Invoke(); send(Outcome(res, tab, list, dir, move));
            return;
        }
        var ctx = SynchronizationContext.Current;
        void Finish(Job j)   // hors du fil d'interface : le retour à la page s'y fait par le contexte de synchronisation
        {
            void Back()
            {
                if (!WindowAlive(owner)) return;
                if (j.Tag is TransferResult r) RecordTransfer(owner, send, r, move);
                if (j.State == JobState.Done) afterOk?.Invoke();
                send(new { type = "fs.done", tab, error = j.State == JobState.Failed ? j.Error : null });
            }
            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
        }
        Job Start(TransferPlan q, string from)
        {
            string parent = Path.GetDirectoryName(q.Items[0].Src);
            return Queue.Enqueue(move ? "move" : "copy", TitleOf(q.Items), job =>
            {
                var o = Copier.Run(q.Items, move, job);
                var r = ResultOf(o); job.Tag = r; job.Failed.AddRange(o.Failed);
                if (o.Canceled) throw new OperationCanceledException();
                if (r.Error != null) throw new IOException(r.Error);
            }, Finish, j =>
            {
                j.Items = q.Items.Count; j.To = FolderLabel(dir); j.Path = q.Items[0].Dst;
                j.From = from ?? (q.Items.All(i => Path.GetDirectoryName(i.Src) == parent) ? FolderLabel(parent) : "plusieurs dossiers");
                // « Réessayer » : seulement ce qui a échoué, en remplaçant ce qu'une copie interrompue aurait laissé à moitié
                j.Retry = () =>
                {
                    var again = j.Failed.Distinct().ToList(); if (again.Count == 0) return null;
                    var q2 = Plan(again, dir, move, "replace"); if (q2.Error != null || q2.Items.Count == 0) return null;
                    return Start(q2, j.From);
                };
            });
        }
        Start(p, null);
    }

    // Inscrit dans la pile « Annuler » de la fenêtre ce qu'un transfert a fait (déplacement ou copie), et dit à la page ce qu'on peut annuler.
    static void RecordTransfer(IntPtr owner, Action<object> send, TransferResult r, bool move)
    {
        if (r?.Done is not { Count: > 0 } d) return;
        Undo.For(owner).Push(move ? Undo.Moved(d.Count == 1 ? "Déplacer" : "Déplacer " + d.Count + " éléments", d) : Undo.Created(d.Count == 1 ? "Copier" : "Copier " + d.Count + " éléments", d.Select(x => x.dst)));
        send(Undo.State(owner));
    }

    // Le contenu du presse-papiers de Windows : les fichiers copiés ou coupés, et si c'est un « couper ».
    static (List<string> files, bool cut) ClipboardFiles()
    {
        var data = Clipboard.GetDataObject();
        if (data == null || !Clipboard.ContainsFileDropList()) return (new(), false);
        bool cut = false;
        if (data.GetData("Preferred DropEffect") is MemoryStream ms) { var bb = new byte[4]; ms.Position = 0; ms.Read(bb, 0, 4); cut = (BitConverter.ToInt32(bb, 0) & 2) != 0; }
        return (Clipboard.GetFileDropList().Cast<string>().ToList(), cut);
    }

    // Tous les éléments sont-ils sur le disque (ou le partage) de la destination ?
    public static bool SameVolume(IEnumerable<string> paths, string dir)
    {
        string root(string p) { try { return Path.GetPathRoot(p) ?? ""; } catch { return ""; } }
        string dr = root(dir); var valid = paths.Where(p => !string.IsNullOrEmpty(p) && Path.IsPathRooted(p)).ToList();
        return dr.Length > 0 && valid.Count > 0 && valid.All(p => string.Equals(root(p), dr, StringComparison.OrdinalIgnoreCase));
    }

    // Le résultat d'un dépôt, à renvoyer à la page : « fs.done » (avec l'erreur éventuelle) ou « fs.conflict » (noms déjà pris ; la page demande, puis renvoie fs.drop avec sa réponse).
    static object Outcome(TransferResult r, string tab, IEnumerable<string> paths, string dir, bool move)
        => r.Conflicts is { Count: > 0 } ? new { type = "fs.conflict", tab, paths = paths.ToArray(), dir, move, names = r.Conflicts } : new { type = "fs.done", tab, error = r.Error };

    // Des fichiers glissés depuis une autre application (l'Explorateur de Windows, le Bureau) ou une autre fenêtre de l'Explorateur : leurs chemins viennent de WebView2 (HostForm.DroppedPaths).
    // Comme dans Windows : Maj déplace, Ctrl copie ; sans touche, on déplace sur le même disque et on copie d'un disque à l'autre.
    public static void DropFiles(IntPtr owner, Action<object> send, IEnumerable<string> paths, string dir, bool shift, bool ctrl, string tab)
    {
        var list = (paths ?? Array.Empty<string>()).ToList();
        bool move = shift || (!ctrl && SameVolume(list, dir));
        try { RunTransfer(owner, send, list, dir, move, null, tab); }
        catch (OperationCanceledException) { send(new { type = "fs.done", tab }); }
        catch (Exception ex) { Host.Log("explorateur : dépôt de fichiers a échoué : " + ex.Message); send(new { type = "fs.done", tab, error = ex.Message }); }
    }
}
