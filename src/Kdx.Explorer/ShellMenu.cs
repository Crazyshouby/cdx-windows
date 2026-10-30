using System.Runtime.InteropServices;
using System.Text;

namespace Kdx;

// « Plus d'options… » : le vrai menu contextuel de Windows pour les éléments sélectionnés, avec tout ce que d'autres logiciels y ajoutent (7-Zip, Git, partage, antivirus...). Windows le construit
// (IContextMenu, obtenu de l'IShellFolder du dossier parent) ; on l'affiche au pointeur, et la commande choisie est exécutée par Windows lui-même. Les sous-menus dessinés par leur logiciel
// (Envoyer vers...) demandent que la fenêtre leur transmette quelques messages (IContextMenu2/3) : ExplorerForm.WndProc appelle Forward.
// Tout se passe sur le fil d'interface, qui reste occupé jusqu'à la fermeture du menu (comme dans Windows).
static class ShellMenu
{
    // ---------- COM et Win32 ----------
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr e);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newPidl);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2 : IContextMenu
    {
        // (les trois méthodes de IContextMenu viennent de l'héritage : les redéclarer décalerait la table des méthodes COM)
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize, fMask; public IntPtr hwnd, lpVerb, lpParameters, lpDirectory; public int nShow, dwHotKey; public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW; public int ptX, ptY;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindingCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr h, uint pos, StringBuilder s, int max, uint flags);
    [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr h, int pos);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046"), IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100, FirstId = 1, LastId = 0x7FFF;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, MF_BYPOSITION = 0x400;

    // ---------- état pendant l'affichage (pour Forward) ----------
    static IContextMenu2 cm2; static IContextMenu3 cm3;
    public static bool Active => cm2 != null || cm3 != null;

    // Les éléments doivent exister et se trouver dans le même dossier (c'est ce que sait faire le menu d'une sélection).
    public static string[] Validate(IEnumerable<string> paths)
    {
        var list = paths?.Where(p => !string.IsNullOrEmpty(p) && p.Length <= 520 && Path.IsPathRooted(p)).Select(p => p.TrimEnd('\\')).Where(p => p.Length > 2 && (File.Exists(p) || Directory.Exists(p))).Take(500).ToArray() ?? Array.Empty<string>();
        if (list.Length == 0) return list;
        string parent = Path.GetDirectoryName(list[0]);
        return list.All(p => string.Equals(Path.GetDirectoryName(p), parent, StringComparison.OrdinalIgnoreCase)) ? list : Array.Empty<string>();
    }

    sealed class Built : IDisposable
    {
        public IContextMenu Cm; public IntPtr Menu; readonly List<IntPtr> pidls = new(); IShellFolder folder;
        public static Built Create(IntPtr hwnd, string[] paths, uint flags)
        {
            var b = new Built(); var kids = new List<IntPtr>();
            try
            {
                foreach (var p in paths)
                {
                    if (SHParseDisplayName(p, IntPtr.Zero, out var abs, 0, out _) != 0 || abs == IntPtr.Zero) throw new IOException("chemin refusé par Windows : " + p);
                    b.pidls.Add(abs);
                    var iid = IID_IShellFolder;
                    if (SHBindToParent(abs, ref iid, out var fp, out var child) != 0) throw new IOException("dossier parent introuvable");
                    var f = (IShellFolder)Marshal.GetObjectForIUnknown(fp); Marshal.Release(fp);
                    if (b.folder == null) b.folder = f; else Marshal.ReleaseComObject(f);   // le premier dossier sert à tous : ils ont le même parent
                    kids.Add(child);   // pointe dans `abs` : il vit autant que lui
                }
                var cmid = IID_IContextMenu;
                int hr = b.folder.GetUIObjectOf(hwnd, (uint)kids.Count, kids.ToArray(), ref cmid, IntPtr.Zero, out var obj);
                if (hr != 0 || obj == null) throw new IOException("Windows ne fournit pas de menu (0x" + hr.ToString("X8") + ")");
                b.Cm = (IContextMenu)obj;
                b.Menu = CreatePopupMenu();
                int q = b.Cm.QueryContextMenu(b.Menu, 0, FirstId, LastId, flags);
                if (q < 0) throw new IOException("QueryContextMenu a échoué (0x" + q.ToString("X8") + ")");
                return b;
            }
            catch { b.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (Menu != IntPtr.Zero) DestroyMenu(Menu); } catch { }
            try { if (Cm != null) Marshal.ReleaseComObject(Cm); } catch { }
            try { if (folder != null) Marshal.ReleaseComObject(folder); } catch { }
            foreach (var p in pidls) CoTaskMemFree(p);
            Menu = IntPtr.Zero; Cm = null; folder = null; pidls.Clear();
        }
    }

    // Les libellés du premier niveau du menu de Windows pour ces éléments, sans rien afficher (essais, diagnostic).
    public static List<string> Probe(string[] paths)
    {
        var ok = Validate(paths); if (ok.Length == 0) return new();
        using var b = Built.Create(IntPtr.Zero, ok, CMF_NORMAL);
        var res = new List<string>(); int n = GetMenuItemCount(b.Menu);
        for (int i = 0; i < n; i++) { var sb = new StringBuilder(256); int len = GetMenuString(b.Menu, (uint)i, sb, sb.Capacity, MF_BYPOSITION); if (len > 0) res.Add(sb.ToString().Replace("&", "")); }
        return res;
    }

    // Affiche le menu de Windows au point (x, y) de l'écran, attend le choix, l'exécute. Rend le nombre d'éléments du menu (0 : rien montré).
    public static int Show(IntPtr hwnd, string[] paths, int x, int y, bool extended)
    {
        var ok = Validate(paths); if (ok.Length == 0) return 0;
        using var b = Built.Create(hwnd, ok, CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0));
        int n = GetMenuItemCount(b.Menu); if (n <= 0) return 0;
        cm3 = b.Cm as IContextMenu3; cm2 = cm3 ?? (b.Cm as IContextMenu2);
        try
        {
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenuEx(b.Menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL : referme proprement le menu
            if (cmd >= FirstId)
            {
                var info = new CMINVOKECOMMANDINFOEX { fMask = 0x4000 | 0x20000000 /* UNICODE | PTINVOKE */, hwnd = hwnd, lpVerb = (IntPtr)(cmd - FirstId), lpVerbW = (IntPtr)(cmd - FirstId), nShow = 1, ptX = x, ptY = y };
                info.cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>();
                int hr = b.Cm.InvokeCommand(ref info);
                if (hr != 0) Host.Log("menu natif : la commande " + (cmd - FirstId) + " a rendu 0x" + hr.ToString("X8"));
            }
        }
        finally { cm2 = null; cm3 = null; }
        return n;
    }

    // À appeler depuis WndProc : transmet à Windows les messages dont ses sous-menus dessinés par leur logiciel ont besoin. True : message traité.
    public static bool Forward(ref Message m)
    {
        if (m.Msg != 0x117 /* WM_INITMENUPOPUP */ && m.Msg != 0x2B /* WM_DRAWITEM */ && m.Msg != 0x2C /* WM_MEASUREITEM */ && m.Msg != 0x120 /* WM_MENUCHAR */) return false;
        try
        {
            if (cm3 != null) { cm3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out var r); m.Result = r; return true; }
            if (cm2 != null && m.Msg != 0x120) { cm2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam); m.Result = m.Msg == 0x117 ? IntPtr.Zero : (IntPtr)1; return true; }
        }
        catch (Exception ex) { Host.Log("menu natif : " + ex.Message); }
        return false;
    }
}
