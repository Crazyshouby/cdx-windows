using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Annuler / rétablir : chaque fenêtre garde la pile de ce qu'elle a fait (renommer, créer, supprimer, déplacer, copier, restaurer) avec, pour chaque geste, de quoi le défaire et le refaire.
// On défait un déplacement en le rejouant à l'envers ; une création ou une copie en mettant l'élément à la corbeille (et on le refait en le restaurant) ; une suppression en la restaurant.
// Une suppression définitive n'est pas annulable (elle ne s'inscrit pas). 50 gestes au plus ; un nouveau geste efface la pile « rétablir ».
sealed class Op
{
    public string Label;                  // « Renommer », « Déplacer »... (le menu dit « Annuler Renommer »)
    public Func<string> Undo, Redo;       // rendent un message d'erreur, ou null si tout s'est bien passé
}

sealed class UndoLog
{
    const int Max = 50;
    readonly List<Op> undo = new(), redo = new();

    public void Push(Op op) { undo.Add(op); if (undo.Count > Max) undo.RemoveAt(0); redo.Clear(); }
    public string UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string RedoLabel => redo.Count > 0 ? redo[^1].Label : null;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    // Défait le dernier geste : (étiquette, erreur). Un geste qui échoue est retiré de la pile (on ne reste pas bloqué dessus).
    public (string label, string error) DoUndo()
    {
        if (undo.Count == 0) return (null, null);
        var op = undo[^1]; undo.RemoveAt(undo.Count - 1);
        string err = Safe(op.Undo); if (err == null) redo.Add(op);
        return (op.Label, err);
    }

    public (string label, string error) DoRedo()
    {
        if (redo.Count == 0) return (null, null);
        var op = redo[^1]; redo.RemoveAt(redo.Count - 1);
        string err = Safe(op.Redo); if (err == null) undo.Add(op);
        return (op.Label, err);
    }

    static string Safe(Func<string> f) { try { return f(); } catch (OperationCanceledException) { return "Annulé."; } catch (Exception ex) { return ex.Message; } }
}

static class Undo
{
    static readonly Dictionary<IntPtr, UndoLog> logs = new();
    public static UndoLog For(IntPtr window) { lock (logs) { if (!logs.TryGetValue(window, out var l)) logs[window] = l = new UndoLog(); return l; } }
    public static void Forget(IntPtr window) { lock (logs) logs.Remove(window); }

    // Ce que la page doit savoir : ce qu'on peut annuler et rétablir (étiquettes), pour le menu et la barre d'état.
    public static object State(IntPtr window) { var l = For(window); return new { type = "fs.undostate", undo = l.UndoLabel, redo = l.RedoLabel }; }

    static string First(List<string> errs) => errs.Count == 0 ? null : errs.Count == 1 ? errs[0] : errs[0] + " (et " + (errs.Count - 1) + " autre" + (errs.Count > 2 ? "s" : "") + ")";

    // Déplace un élément (le dossier d'arrivée est créé au besoin) sans écraser : l'arrivée doit être libre.
    static string MovePath(string from, string to)
    {
        if (!Path.Exists(from)) return "« " + Path.GetFileName(from) + " » est introuvable.";
        if (Path.Exists(to)) return "« " + Path.GetFileName(to) + " » existe déjà.";
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        bool dir = Directory.Exists(from);
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { if (dir) Directory.Move(from, to); else File.Move(from, to); }
        else if (dir) FileSystem.MoveDirectory(from, to, UIOption.OnlyErrorDialogs); else FileSystem.MoveFile(from, to, UIOption.OnlyErrorDialogs);
        return null;
    }

    // Des déplacements (src → dst) : défaire = dst → src, refaire = src → dst. Sert aussi au renommage.
    public static Op Moved(string label, List<(string src, string dst)> pairs) => new()
    {
        Label = label,
        Undo = () => { var errs = new List<string>(); foreach (var (s, d) in Enumerable.Reverse(pairs)) { var e = MovePath(d, s); if (e != null) errs.Add(e); } return First(errs); },
        Redo = () => { var errs = new List<string>(); foreach (var (s, d) in pairs) { var e = MovePath(s, d); if (e != null) errs.Add(e); } return First(errs); },
    };

    // Des éléments créés (nouveau dossier, nouveau fichier, copies, restaurations) : défaire = les mettre à la corbeille, refaire = les en sortir.
    public static Op Created(string label, IEnumerable<string> paths) => BinOp(label, paths.ToList(), null);

    // Des éléments supprimés (vers la corbeille) : défaire = les restaurer, refaire = les remettre à la corbeille. `entries` : leur origine et l'identifiant de leur entrée.
    public static Op Deleted(string label, List<BinEntry> entries) => BinOp(label, entries.Select(e => e.Original).ToList(), entries.Select(e => e.Id).ToList());

    // `ids` null : les éléments existent (leurs chemins) ; sinon ils sont à la corbeille (ces identifiants).
    static Op BinOp(string label, List<string> paths, List<string> ids)
    {
        string Bin()      // met les éléments à la corbeille et retient leurs identifiants
        {
            var errs = new List<string>(); var got = new List<string>();
            foreach (var p in paths)
            {
                if (!Path.Exists(p)) { errs.Add("« " + Path.GetFileName(p) + " » est introuvable."); got.Add(null); continue; }
                var e = Recycle.DeleteToBin(p); got.Add(e?.Id); if (e == null) errs.Add("« " + Path.GetFileName(p) + " » n'a pas pu aller à la corbeille.");
            }
            ids = got; return First(errs);
        }
        string Unbin()    // sort les éléments de la corbeille, à leur place d'origine
        {
            var errs = new List<string>();
            foreach (var id in ids) { if (id == null) continue; var (err, _) = Recycle.Restore(id); if (err != null) errs.Add(err); }
            ids = null; return First(errs);
        }
        return new Op { Label = label, Undo = () => ids == null ? Bin() : Unbin(), Redo = () => ids == null ? Bin() : Unbin() };
    }
}
