using System.Runtime.InteropServices;

namespace Cdx;

// Le moteur de copie et de déplacement : aucune boîte de Windows, un rappel de progression, une pause, une annulation qui ne laisse rien de moitié copié.
// Un déplacement sur le même disque est un simple renommage (instantané) ; entre deux disques, c'est une copie suivie de l'effacement de l'original, élément par élément.
// Les conflits sont réglés avant (voir Fs.Plan) : ici chaque élément arrive avec sa destination et la consigne d'écraser ou non.
sealed record PlanItem(string Src, bool IsDir, string Dst, bool Overwrite);

sealed class CopyOutcome
{
    public readonly List<(string src, string dst)> Done = new();   // ce qui a vraiment été fait en entier : de quoi défaire (Annuler)
    public readonly List<string> Failed = new();                   // les éléments de premier niveau qui ont échoué
    public readonly List<string> Errors = new();                   // les messages, un par fichier
    public bool Canceled;
}

static class Copier
{
    delegate uint CopyProgressRoutine(long total, long transferred, long streamSize, long streamTransferred, uint streamNo, uint reason, IntPtr src, IntPtr dst, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CopyFileEx(string src, string dst, CopyProgressRoutine routine, IntPtr data, ref int cancel, uint flags);
    const uint COPY_FILE_FAIL_IF_EXISTS = 0x1, COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x8;
    const uint PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1;
    const int ERROR_REQUEST_ABORTED = 1235;
    internal static Action<string, long> TestHook;   // réservé aux essais : appelé à chaque avancement d'un fichier (nom, octets copiés), sur le fil de la copie

    // Les chemins de plus de 260 caractères passent par le préfixe « \\?\ ».
    internal static string Long(string p)
    {
        if (p.Length < 240 || p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p;
        return p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    }

    static string Root(string p) { try { return Path.GetPathRoot(p) ?? ""; } catch { return ""; } }
    static bool SameVolume(string a, string b) => Root(a).Length > 0 && string.Equals(Root(a), Root(b), StringComparison.OrdinalIgnoreCase);

    // Les octets à copier (les renommages n'en comptent pas) : pour que la barre de progression ait un total dès le début.
    static long SizeOf(PlanItem it, bool move)
    {
        if (move && !it.Overwrite && SameVolume(it.Src, it.Dst) && !Path.Exists(it.Dst)) return 0;   // un renommage
        if (!it.IsDir) { try { return new FileInfo(it.Src).Length; } catch { return 0; } }
        long n = 0; var stack = new Stack<string>(); stack.Push(it.Src);
        while (stack.Count > 0)
        {
            string d = stack.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(Long(d)).EnumerateFileSystemInfos())
                {
                    if (e.Attributes.HasFlag(FileAttributes.ReparsePoint) && e is DirectoryInfo) continue;
                    if (e is DirectoryInfo) stack.Push(e.FullName); else n += ((FileInfo)e).Length;
                }
            }
            catch { }
        }
        return n;
    }

    // Exécute le plan. `job` null : en entier sur le fil appelant, sans progression (essais, appels sans file).
    public static CopyOutcome Run(IEnumerable<PlanItem> plan, bool move, Job job)
    {
        var items = plan.ToList(); var o = new CopyOutcome();
        var sizes = new List<long>(); long total = 0; if (job != null) { foreach (var it in items) { long n = SizeOf(it, move); sizes.Add(n); total += n; } job.Report(0, Math.Max(total, 1)); }
        long baseDone = 0;
        for (int idx = 0; idx < items.Count; idx++)
        {
            var it = items[idx];
            if (job != null && job.WaitPaused()) { o.Canceled = true; break; }
            string name = Path.GetFileName(it.Src);
            var created = new List<string>(); var createdDirs = new List<string>();
            int errsBefore = o.Errors.Count;
            try
            {
                if (move && !it.Overwrite && SameVolume(it.Src, it.Dst) && !Path.Exists(it.Dst))   // même disque, rien en travers : on renomme
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(it.Dst));
                    if (it.IsDir) Directory.Move(Long(it.Src), Long(it.Dst)); else File.Move(Long(it.Src), Long(it.Dst));
                    o.Done.Add((it.Src, it.Dst)); continue;
                }
                long itemSize = job != null ? sizes[idx] : 0;
                bool ok = it.IsDir ? CopyTree(it, o, job, baseDone, total, created, createdDirs) : CopyOne(it.Src, it.Dst, it.Overwrite, job, baseDone, total, name, created, o);
                if (job != null && job.Token.IsCancellationRequested) throw new OperationCanceledException();
                baseDone += itemSize;
                if (!ok) { o.Failed.Add(it.Src); continue; }
                // La copie est complète : elle ne sera plus jamais défaite. Si l'effacement de l'original échoue à moitié (un fichier verrouillé dans un dossier),
                // une partie des originaux est déjà partie : retirer les copies ferait perdre ces fichiers pour de bon.
                created.Clear(); createdDirs.Clear();
                if (move)
                {
                    try { DeleteSource(it); }
                    catch (Exception ex) { o.Errors.Add(name + " : copié, mais l'original n'a pas pu être effacé entièrement (" + ex.Message + ")"); o.Failed.Add(it.Src); continue; }
                }
                o.Done.Add((it.Src, it.Dst));
            }
            catch (OperationCanceledException)
            {
                o.Canceled = true;
                Cleanup(created, createdDirs);   // une copie annulée ne laisse rien de moitié fait (les originaux ne sont jamais touchés avant la fin de l'élément)
                break;
            }
            catch (Exception ex)
            {
                o.Errors.Add(name + " : " + ex.Message); o.Failed.Add(it.Src);
                Cleanup(created, createdDirs);
            }
            if (o.Errors.Count > errsBefore && !o.Failed.Contains(it.Src)) o.Failed.Add(it.Src);
        }
        return o;
    }

    // Un fichier : CopyFileEx avec progression, pause (on attend dans le rappel) et annulation (le rappel rend PROGRESS_CANCEL : Windows efface la copie partielle).
    static bool CopyOne(string src, string dst, bool overwrite, Job job, long baseDone, long total, string name, List<string> created, CopyOutcome o)
    {
        try
        {
            if (job != null && job.WaitPaused()) throw new OperationCanceledException();   // aussi entre deux petits fichiers : pause et annulation répondent tout de suite
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            bool existed = File.Exists(Long(dst));
            if (existed && overwrite) { try { File.SetAttributes(Long(dst), FileAttributes.Normal); } catch { } }
            int cancel = 0;
            CopyProgressRoutine cb = job == null ? null : (tot, xfer, ss, sx, sn, reason, s, d, data) =>
            {
                try { TestHook?.Invoke(name, xfer); } catch { }
                if (job.WaitPaused()) return PROGRESS_CANCEL;
                job.Report(baseDone + xfer, total, name);
                return PROGRESS_CONTINUE;
            };
            uint flags = COPY_FILE_ALLOW_DECRYPTED_DESTINATION | (overwrite ? 0u : COPY_FILE_FAIL_IF_EXISTS);
            if (!CopyFileEx(Long(src), Long(dst), cb, IntPtr.Zero, ref cancel, flags))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_REQUEST_ABORTED) throw new OperationCanceledException();
                o.Errors.Add(name + " : " + new System.ComponentModel.Win32Exception(err).Message);
                return false;
            }
            GC.KeepAlive(cb);
            if (!existed) created.Add(dst);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { o.Errors.Add(name + " : " + ex.Message); return false; }
    }

    // Un dossier : l'arborescence est recréée, chaque fichier copié ; un fichier en échec n'arrête pas les autres (mais l'original du dossier ne sera pas effacé).
    static bool CopyTree(PlanItem it, CopyOutcome o, Job job, long baseDone, long total, List<string> created, List<string> createdDirs)
    {
        bool all = true; long done = baseDone;
        if (!Directory.Exists(Long(it.Dst))) { Directory.CreateDirectory(Long(it.Dst)); createdDirs.Add(it.Dst); }
        var stack = new Stack<(string src, string dst)>(); stack.Push((it.Src, it.Dst));
        while (stack.Count > 0)
        {
            var (s, d) = stack.Pop();
            if (job != null && job.WaitPaused()) throw new OperationCanceledException();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(Long(s)).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) { o.Errors.Add(Path.GetFileName(s) + " : " + ex.Message); all = false; continue; }
            foreach (var e in entries)
            {
                string dd = Path.Combine(d, e.Name);
                if (e is DirectoryInfo)
                {
                    if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { o.Errors.Add(e.Name + " : lien de dossier ignoré (jonction ou lien symbolique)"); all = false; continue; }
                    try { if (!Directory.Exists(Long(dd))) { Directory.CreateDirectory(Long(dd)); createdDirs.Add(dd); } stack.Push((e.FullName, dd)); }
                    catch (Exception ex) { o.Errors.Add(e.Name + " : " + ex.Message); all = false; }
                }
                else
                {
                    long len = 0; try { len = ((FileInfo)e).Length; } catch { }
                    if (!CopyOne(e.FullName, dd, it.Overwrite, job, done, total, e.Name, created, o)) all = false;
                    done += len; job?.Report(done, total, e.Name);
                }
            }
        }
        return all;
    }

    // Après une copie complète d'un déplacement entre disques : l'original disparaît (les attributs « lecture seule » ne l'en empêchent pas).
    static void DeleteSource(PlanItem it)
    {
        if (it.IsDir) DeleteTree(it.Src);
        else { File.SetAttributes(Long(it.Src), FileAttributes.Normal); File.Delete(Long(it.Src)); }
    }

    internal static void DeleteTree(string path)
    {
        if (Directory.Exists(Long(path)))
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(Long(path), "*", SearchOption.AllDirectories)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(Long(path), true);
        }
        else if (File.Exists(Long(path))) { File.SetAttributes(Long(path), FileAttributes.Normal); File.Delete(Long(path)); }
    }

    // Supprime pour de bon (Maj+Suppr, après confirmation) en rendant compte de l'avancement fichier par fichier ; rend le premier message d'erreur. Jamais la racine d'un disque ni un chemin relatif.
    public static string DeleteMany(IEnumerable<string> paths, Job job)
    {
        var tops = paths.Where(p => !string.IsNullOrEmpty(p) && Path.IsPathRooted(p) && p.Length <= 520 && Path.GetFileName(p.TrimEnd('\\')).Length > 0 && Path.Exists(p)).Select(p => p.TrimEnd('\\')).ToList();
        var files = new List<string>(); var dirs = new List<string>();
        foreach (var t in tops)
        {
            if (!Directory.Exists(Long(t))) { files.Add(t); continue; }
            var stack = new Stack<string>(); stack.Push(t);
            while (stack.Count > 0)
            {
                string d = stack.Pop(); dirs.Add(d);
                try
                {
                    foreach (var e in new DirectoryInfo(Long(d)).EnumerateFileSystemInfos())
                    {
                        if (e is DirectoryInfo && !e.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(e.FullName); else if (e is DirectoryInfo) dirs.Add(e.FullName); else files.Add(e.FullName);
                    }
                }
                catch { }
            }
        }
        long total = files.Count + dirs.Count, done = 0; string err = null;
        job?.Report(0, Math.Max(total, 1));
        foreach (var f in files)
        {
            job?.WaitIfPaused();
            try { File.SetAttributes(Long(f), FileAttributes.Normal); File.Delete(Long(f)); } catch (Exception ex) { err ??= Path.GetFileName(f) + " : " + ex.Message; }
            job?.Report(++done, total, Path.GetFileName(f));
        }
        for (int i = dirs.Count - 1; i >= 0; i--)   // les plus profonds d'abord
        {
            job?.WaitIfPaused();
            try { Directory.Delete(Long(dirs[i])); } catch (Exception ex) { err ??= Path.GetFileName(dirs[i]) + " : " + ex.Message; }
            job?.Report(++done, total, Path.GetFileName(dirs[i]));
        }
        return err;
    }

    // Retire ce que cet élément a créé (jamais ce qui existait déjà).
    static void Cleanup(List<string> files, List<string> dirs)
    {
        foreach (var f in files) try { File.Delete(Long(f)); } catch { }
        foreach (var d in Enumerable.Reverse(dirs)) try { if (Directory.Exists(Long(d)) && !Directory.EnumerateFileSystemEntries(Long(d)).Any()) Directory.Delete(Long(d)); } catch { }
    }
}
