using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Kdx;

// Ce que le menu contextuel emprunte à Windows : les programmes de « Ouvrir avec », les destinations de « Envoyer vers », les modèles de « Nouveau » (les ShellNew du registre), le terminal.
// Tout est en lecture (registre, dossier SendTo) ; seules les actions lancent quelque chose, et elles ne lancent que ce que Windows a lui-même inscrit.
static class ShellData
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string src, StringBuilder buf, int len, IntPtr reserved);

    sealed record Prog(string Name, string Exe);
    public sealed record NewKind(string Key, string Name, string Ext);
    public sealed record SendItem(string Name, string Path, string Kind);   // Kind : « dir », « lnk » ou « drive »

    // ---------- chaînes localisées ----------
    static string Indirect(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '@') return s;
        var sb = new StringBuilder(260);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    // L'exécutable d'une commande de registre : « "C:\x\y.exe" "%1" » ou « C:\x\y.exe %1 ».
    static string ExeOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string exe = command[0] == '"' ? command[1..(command.IndexOf('"', 1) > 0 ? command.IndexOf('"', 1) : command.Length)] : command.Split(' ')[0];
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null;
    }

    static string AppPath(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try { using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName); var v = k?.GetValue(null) as string; if (v != null && File.Exists(v.Trim('"'))) return v.Trim('"'); } catch { }
        }
        return null;
    }

    static string FromSystem(string exeName)
    {
        foreach (var d in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        { string p = Path.Combine(d, exeName); if (File.Exists(p)) return p; }
        return null;
    }

    // ---------- Ouvrir avec ----------
    // Les programmes que Windows connaît pour une extension (« .txt »), sans doublons, au plus 12 : la liste récente de l'Explorateur, celle des classes, les identifiants de programme associés.
    public static List<(string name, string exe)> OpenWith(string ext)
    {
        var res = new List<Prog>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string exe) { if (exe == null || !seen.Add(exe)) return; string name; try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { name = null; } res.Add(new Prog(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim(), exe)); }
        string AppCommand(string exeName)
        {
            try { using var k = Registry.ClassesRoot.OpenSubKey(@"Applications\" + exeName + @"\shell\open\command"); var e = ExeOf(k?.GetValue(null) as string); if (e != null) return e; } catch { }
            return AppPath(exeName) ?? FromSystem(exeName);
        }
        if (string.IsNullOrEmpty(ext) || ext[0] != '.' || ext.Length > 32 || ext.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return new();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithList"))
                if (k != null) foreach (var n in k.GetValueNames().Where(n => n.Length == 1 && char.IsLetter(n[0]))) if (k.GetValue(n) is string exeName) Add(AppCommand(exeName));
            using (var k = Registry.ClassesRoot.OpenSubKey(ext + @"\OpenWithList"))
                if (k != null) foreach (var exeName in k.GetSubKeyNames()) Add(AppCommand(exeName));
            using (var k = Registry.ClassesRoot.OpenSubKey(ext + @"\OpenWithProgids"))
                if (k != null) foreach (var prog in k.GetValueNames().Where(n => n.Length > 0))
                {
                    using var c = Registry.ClassesRoot.OpenSubKey(prog + @"\shell\open\command"); Add(ExeOf(c?.GetValue(null) as string));
                }
        }
        catch (Exception ex) { Host.Log("ouvrir avec " + ext + " : " + ex.Message); }
        return res.Take(12).Select(p => (p.Name, p.Exe)).ToList();
    }

    // Lance un programme sur un fichier : le programme doit exister, être un .exe, et le fichier aussi.
    public static bool OpenWithRun(string exe, string path)
    {
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(path) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(exe) || !File.Exists(exe) || !(File.Exists(path) || Directory.Exists(path))) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) };
        psi.ArgumentList.Add(path);
        Process.Start(psi); return true;
    }

    // « Choisir une autre application… » : la boîte de Windows.
    public static void OpenAsDialog(string path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) return;
        var psi = new ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("shell32.dll,OpenAs_RunDLL"); psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    // ---------- Envoyer vers ----------
    public static string SendToDir => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    // Le dossier SendTo (raccourcis vers un programme ou un dossier, dossiers) et les disques amovibles prêts.
    public static List<SendItem> SendTo()
    {
        var res = new List<SendItem>();
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(SendToDir).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                try
                {
                    var a = File.GetAttributes(e); string n = Path.GetFileName(e);
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0 || n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((a & FileAttributes.Directory) != 0) res.Add(new SendItem(n, e, "dir"));
                    else if (e.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ResolveLink(e) is { } t && (Directory.Exists(t.target) || File.Exists(t.target))) res.Add(new SendItem(Path.GetFileNameWithoutExtension(n), e, "lnk"));
                }
                catch { }
            }
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
                try { if (d.IsReady) res.Add(new SendItem((string.IsNullOrEmpty(d.VolumeLabel) ? "Disque amovible" : d.VolumeLabel) + " (" + d.Name.TrimEnd('\\') + ")", d.Name, "drive")); } catch { }
        }
        catch (Exception ex) { Host.Log("envoyer vers : " + ex.Message); }
        return res;
    }

    // Ce que « Envoyer vers » doit faire d'un élément de la liste : un dossier (ou un disque) reçoit une copie, un programme reçoit les fichiers en arguments.
    // `item` doit appartenir à la liste (le dossier SendTo ou un disque amovible) : jamais un chemin quelconque envoyé par la page.
    public static (string dir, string exe) SendToTarget(string item)
    {
        if (string.IsNullOrEmpty(item)) return (null, null);
        bool inList = SendTo().Any(s => string.Equals(s.Path.TrimEnd('\\'), item.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!inList) return (null, null);
        if (Directory.Exists(item)) return (item, null);
        var t = ResolveLink(item);
        if (t == null) return (null, null);
        if (Directory.Exists(t.Value.target)) return (t.Value.target, null);
        return (null, t.Value.target);
    }

    public static void RunProgramOn(string exe, IEnumerable<string> paths)
    {
        if (exe == null || !File.Exists(exe)) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var p in paths.Take(200)) if (File.Exists(p) || Directory.Exists(p)) psi.ArgumentList.Add(p);
        if (psi.ArgumentList.Count > 0) Process.Start(psi);
    }

    // ---------- raccourcis (.lnk) ----------
    public static (string target, string args)? ResolveLink(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = sh.CreateShortcut(lnk);
            string t = sc.TargetPath; string a = sc.Arguments;
            Marshal.FinalReleaseComObject(sc); Marshal.FinalReleaseComObject(sh);
            return string.IsNullOrEmpty(t) ? null : (t, a);
        }
        catch { return null; }
    }

    // Crée un raccourci « name.lnk » vers une cible (fichier, dossier ou adresse web) dans `dir` ; rend son chemin, ou null.
    public static string CreateShortcut(string dir, string name, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(dir)) return null;
        name = string.Concat((string.IsNullOrWhiteSpace(name) ? "Raccourci" : name.Trim()).Split(Path.GetInvalidFileNameChars()));
        string path = Fs.UniquePath(dir, name + ".lnk");
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = sh.CreateShortcut(path);
            sc.TargetPath = target.Trim().Trim('"');
            if (Directory.Exists(target.Trim().Trim('"'))) sc.WorkingDirectory = target.Trim().Trim('"');
            sc.Save();
            Marshal.FinalReleaseComObject(sc); Marshal.FinalReleaseComObject(sh);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) { Host.Log("raccourci : " + ex.Message); return null; }
    }

    // ---------- Nouveau (ShellNew) ----------
    static List<NewKind> newCache;
    sealed record Tpl(string Ext, string Prog, string FileName, byte[] Data, bool Null);
    static readonly Dictionary<string, Tpl> templates = new(StringComparer.OrdinalIgnoreCase);

    // Les types de fichiers que Windows sait créer vides ou depuis un modèle (une entrée ShellNew dans le registre) : document texte, image bitmap, document Word... Gardé en mémoire.
    public static List<NewKind> NewKinds()
    {
        if (newCache != null) return newCache;
        var res = new List<NewKind>();
        // Les modèles que Windows 11 ne range plus dans le registre (le Bloc-notes les déclare ailleurs) : proposés d'office, vides.
        foreach (var (e, n) in new[] { (".txt", "Document texte"), (".md", "Document Markdown") })
        { res.Add(new NewKind(e, n, e)); templates[e] = new Tpl(e, null, null, null, true); }
        var builtin = res.Count;
        try
        {
            foreach (var ext in Registry.ClassesRoot.GetSubKeyNames().Where(n => n.Length > 1 && n[0] == '.' && !templates.ContainsKey(n) && n != ".library-ms" && n != ".contact"))
            {
                try
                {
                    using var e = Registry.ClassesRoot.OpenSubKey(ext); if (e == null) continue;
                    RegistryKey sn = e.OpenSubKey("ShellNew"); string prog = e.GetValue(null) as string;
                    if (sn == null) foreach (var sub in e.GetSubKeyNames()) { var c = e.OpenSubKey(sub + @"\ShellNew"); if (c != null) { sn = c; prog = sub; break; } }
                    if (sn == null) continue;
                    using (sn)
                    {
                        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || sn.GetValue("Command") != null) continue;   // le raccourci a sa propre commande ; les commandes de modèle sont laissées à Windows
                        bool isNull = sn.GetValue("NullFile") != null; string fn = sn.GetValue("FileName") as string; var data = sn.GetValue("Data") as byte[];
                        if (!isNull && fn == null && data == null) continue;
                        string friendly = null;
                        using (var pk = prog == null ? null : Registry.ClassesRoot.OpenSubKey(prog))
                            friendly = Indirect(pk?.GetValue("FriendlyTypeName") as string) ?? pk?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(friendly)) friendly = "Fichier " + ext[1..].ToUpperInvariant();
                        res.Add(new NewKind(ext, friendly, ext)); templates[ext] = new Tpl(ext, prog, fn, data, isNull);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Host.Log("nouveau : " + ex.Message); }
        return newCache = res.Take(builtin).Concat(res.Skip(builtin).OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)).Take(20).ToList();
    }

    // Crée « Nouveau <type>.<ext> » dans `dir` (nom unique) d'après le modèle ; rend son chemin, ou null.
    public static string CreateNew(string dir, string ext)
    {
        if (!Directory.Exists(dir)) return null;
        NewKinds();
        if (ext == null || !templates.TryGetValue(ext, out var t)) return null;
        var kind = NewKinds().First(k => k.Ext.Equals(ext, StringComparison.OrdinalIgnoreCase));
        string baseName = string.Concat(("Nouveau " + kind.Name.ToLowerInvariant()).Split(Path.GetInvalidFileNameChars()));
        string path = Fs.UniquePath(dir, baseName + t.Ext);
        try
        {
            if (t.Null) File.WriteAllBytes(path, Array.Empty<byte>());
            else if (t.Data != null) File.WriteAllBytes(path, t.Data);
            else
            {
                string src = t.FileName; if (!Path.IsPathRooted(src)) foreach (var d in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"), Environment.GetFolderPath(Environment.SpecialFolder.Templates) }) { var c = Path.Combine(d, src); if (File.Exists(c)) { src = c; break; } }
                if (!File.Exists(src)) return null;
                File.Copy(src, path);
            }
            return path;
        }
        catch (Exception ex) { Host.Log("nouveau : " + ex.Message); return null; }
    }

    // ---------- terminal ----------
    // La commande qui ouvre un terminal dans un dossier : Windows Terminal s'il est là (wt.exe -d dossier), sinon cmd.exe. Rend (programme, arguments, passer par le shell).
    public static (string exe, string[] args, bool shell) TerminalCommand(string dir, bool hasWt)
        => hasWt ? ("wt.exe", new[] { "-d", dir }, true) : ("cmd.exe", new[] { "/k", "cd /d \"" + dir + "\"" }, false);

    public static bool HasWindowsTerminal()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return File.Exists(Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe"));
    }

    public static void OpenTerminal(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var (exe, args, shell) = TerminalCommand(dir, HasWindowsTerminal());
        var psi = new ProcessStartInfo(exe) { UseShellExecute = shell, WorkingDirectory = dir };
        if (shell) psi.Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));   // l'alias wt.exe se lance par le shell, qui prend une ligne d'arguments
        else foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
