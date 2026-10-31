using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Cdx;

// La zone de notification : les applications inscrivent leurs icônes en envoyant WM_COPYDATA (dwData = 1) à la fenêtre « Shell_TrayWnd » qu'elles
// trouvent par FindWindow. Le shell pose sa propre fenêtre de cette classe, au-dessus de celle d'explorer.exe dans l'ordre des fenêtres
// supérieures, puis diffuse « TaskbarCreated » : les applis se réinscrivent chez nous. Tout ce qui n'est pas une icône (messages de barre
// d'application de SHAppBarMessage, commandes…) est renvoyé tel quel à la vraie barre de Windows, qui reste cachée mais vivante.
sealed class TrayIcon
{
    public string Key, Tip = "", Exe = "", Png;
    public IntPtr Hwnd, HIcon; public uint Uid, CbMsg, Pid; public int Version;
    public bool Hidden, Dirty = true; public long Seq;
}

sealed class TrayRelay
{
    public static TrayRelay Current;
    public IntPtr Handle { get; private set; }
    public Action<string, string, string> OnBalloon;   // titre, texte, nom du programme

    // Les icônes posées par Windows lui-même (écran, Bluetooth, éjection, micro utilisé) font doublon avec la barre : masquées.
    public readonly HashSet<string> HiddenExes = new(StringComparer.OrdinalIgnoreCase) { "explorer" };

    readonly Dictionary<string, TrayIcon> icons = new(); readonly HashSet<long> refused = new(); long seq; bool changed, suspended;
    WndProcDelegate keep; IntPtr explorerTray;
    const int MaxIcons = 64;

    delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public uint cbSize, style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr inst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);

    static readonly IntPtr HWND_BROADCAST = new(0xffff), HWND_TOPMOST = new(-1);

    // La vraie barre de Windows : la fenêtre « Shell_TrayWnd » qui appartient à explorer.exe (ni la nôtre, ni le relais d'un autre shell lancé).
    public static IntPtr ExplorerTray()
    {
        IntPtr found = IntPtr.Zero;
        N.EnumWindows((h, _) =>
        {
            if (N.ClassOf(h) != "Shell_TrayWnd") return true;
            if (!string.Equals(Path.GetFileName(N.ExePathOf(h) ?? ""), "explorer.exe", StringComparison.OrdinalIgnoreCase)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public bool Create(Rectangle bounds)
    {
        keep = Proc;
        var c = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = keep, hInstance = N.GetModuleHandle(null), lpszClassName = "Shell_TrayWnd" };
        if (RegisterClassEx(ref c) == 0) { Host.Log("zone de notification : classe refusée, erreur " + Marshal.GetLastWin32Error()); return false; }
        // Cachée (jamais affichée), mais à la place de la barre : les applis qui lisent le rectangle de la barre pour poser leurs bulles le trouvent au bon endroit.
        Handle = CreateWindowEx(0x80 | 0x08 | 0x08000000 /* TOOLWINDOW | TOPMOST | NOACTIVATE */, "Shell_TrayWnd", "", 0x80000000u /* WS_POPUP */,
                                bounds.X, bounds.Y, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, c.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) { Host.Log("zone de notification : fenêtre refusée, erreur " + Marshal.GetLastWin32Error()); return false; }
        RaiseToTop();
        Current = this;
        return true;
    }

    public bool Alive => Handle != IntPtr.Zero;

    public void Place(Rectangle b) { if (Alive) N.SetWindowPos(Handle, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, 0x0010 /* NOACTIVATE */); }

    public void RaiseToTop() { if (Alive) N.SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }

    // Explorer peut remonter sa propre barre au-dessus de la nôtre : on reprend la place, sinon les applis lui parleraient à lui.
    public void EnsureTop() { if (Alive && !suspended && N.FindWindow("Shell_TrayWnd", null) != Handle) RaiseToTop(); }

    // Le shell passe lui-même par SHAppBarMessage (réserver la place de la barre) : il se met brièvement sous la barre de Windows pour que ces appels l'atteignent.
    public void Suspend() { if (!Alive) return; suspended = true; N.SetWindowPos(Handle, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
    public void Resume() { suspended = false; RaiseToTop(); }

    public void Announce() => SendNotifyMessage(HWND_BROADCAST, N.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);

    // À l'arrêt : la fenêtre disparaît, puis les applis sont invitées à se réinscrire auprès de la zone de Windows.
    public void Shutdown()
    {
        if (!Alive) return;
        DestroyWindow(Handle); Handle = IntPtr.Zero; Current = null;
        UnregisterClass("Shell_TrayWnd", N.GetModuleHandle(null));
        icons.Clear();
        Announce();
        Host.Log("zone de notification : relais retiré, applis invitées à se réinscrire chez Windows");
    }

    // ---------- messages reçus ----------
    IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (msg == 0x4A /* WM_COPYDATA */)
            {
                long kind = Marshal.ReadIntPtr(l, 0).ToInt64();
                if (kind == 1) return Tray(l) ? (IntPtr)1 : IntPtr.Zero;
                // Les messages de barre d'application (kind 0) passent par une mémoire partagée créée pour le processus de la fenêtre trouvée,
                // donc pour le nôtre : renvoyés à Explorer, ils lui parviendraient avec une poignée qui ne lui dit rien (réponse trompeuse, rectangle nul).
                // Un échec franc vaut mieux ; le shell se met en retrait (Suspend) pour ses propres appels.
                if (!refused.Contains(kind)) { refused.Add(kind); Host.Log("zone de notification : message de barre d'application " + kind + " refusé (non pris en charge)"); }
                return IntPtr.Zero;
            }
            if (msg == 0x0111 /* WM_COMMAND */ || (msg >= 0x0400 && msg <= 0x7FFF) || msg >= 0xC000) return Forward(msg, w, l);
        }
        catch (Exception ex) { Host.Log("zone de notification : " + ex.Message); }
        return DefWindowProc(h, msg, w, l);
    }

    IntPtr Forward(uint msg, IntPtr w, IntPtr l)
    {
        if (explorerTray == IntPtr.Zero || !N.IsWindow(explorerTray)) explorerTray = ExplorerTray();
        if (explorerTray == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(explorerTray, msg, w, l, 0x2 /* SMTO_ABORTIFHUNG */, 2000, out var res);
        return res;
    }

    static string Str(IntPtr p, int off, int chars) { var s = Marshal.PtrToStringUni(p + off, chars) ?? ""; int z = s.IndexOf('\0'); return z >= 0 ? s[..z] : s; }

    // SHELLTRAYDATA : signature, NIM_*, puis NOTIFYICONDATA avec des poignées de 32 bits (même disposition pour tous les programmes).
    bool Tray(IntPtr l)
    {
        int cb = Marshal.ReadInt32(l, IntPtr.Size);
        IntPtr p = Marshal.ReadIntPtr(l, IntPtr.Size * 2);
        if (cb < 32 || p == IntPtr.Zero) return false;
        uint nim = (uint)Marshal.ReadInt32(p, 4);
        int hwnd = Marshal.ReadInt32(p, 12); uint uid = (uint)Marshal.ReadInt32(p, 16), flags = (uint)Marshal.ReadInt32(p, 20);
        var owner = new IntPtr(hwnd);   // sign-extension voulue : c'est ainsi que Windows reforme une poignée de 32 bits
        string key = hwnd.ToString("x") + ":" + uid;

        if (nim == 2 /* NIM_DELETE */) { if (icons.Remove(key)) changed = true; return true; }
        if (nim == 4 /* NIM_SETVERSION */) { if (icons.TryGetValue(key, out var v) && cb >= 812) v.Version = Marshal.ReadInt32(p, 808); return true; }
        if (nim > 1) return true;   // NIM_SETFOCUS : rien à faire

        if (!icons.TryGetValue(key, out var ic))
        {
            if (icons.Count >= MaxIcons) return false;
            N.GetWindowThreadProcessId(owner, out uint pid);
            string exe = ""; try { exe = Process.GetProcessById((int)pid).ProcessName; } catch { }
            ic = new TrayIcon { Key = key, Hwnd = owner, Uid = uid, Pid = pid, Exe = exe, Seq = ++seq };
            icons[key] = ic; changed = true;
        }
        if ((flags & 0x01) != 0) ic.CbMsg = (uint)Marshal.ReadInt32(p, 24);
        if ((flags & 0x02) != 0) { var hi = new IntPtr(Marshal.ReadInt32(p, 28)); if (hi != ic.HIcon) { ic.HIcon = hi; ic.Dirty = true; } }
        if ((flags & 0x04) != 0 && cb >= 288) { var tip = Str(p, 32, 128); if (tip != ic.Tip) { ic.Tip = tip; if (Shown(ic)) changed = true; } }
        if ((flags & 0x08) != 0 && cb >= 296)
        {
            uint state = (uint)Marshal.ReadInt32(p, 288), mask = (uint)Marshal.ReadInt32(p, 292);
            if ((mask & 1) != 0) { bool hid = (state & 1) != 0; if (hid != ic.Hidden) { ic.Hidden = hid; changed = true; } }
        }
        if ((flags & 0x10) != 0 && cb >= 940)
        {
            string text = Str(p, 296, 256), title = Str(p, 812, 64);
            if (text.Length > 0 || title.Length > 0) OnBalloon?.Invoke(title, text, ic.Exe);
        }
        return true;
    }

    // ---------- état pour la barre ----------
    bool Shown(TrayIcon i) => !i.Hidden && i.Png != null && !HiddenExes.Contains(i.Exe);

    // True si ce que la barre affiche a changé depuis le dernier appel (ajout, retrait, infobulle, image).
    public bool Flush()
    {
        bool ch = changed; changed = false;
        foreach (var ic in icons.Values.ToList())
        {
            if (!N.IsWindow(ic.Hwnd)) { icons.Remove(ic.Key); ch = true; continue; }   // l'appli est partie sans retirer son icône
            if (!ic.Dirty) continue;
            ic.Dirty = false;
            var png = MakePng(ic.HIcon);
            if (png != ic.Png) { ic.Png = png; ch = true; }
        }
        return ch;
    }

    public List<object> Items() => icons.Values.Where(Shown).OrderBy(i => i.Seq).Select(i => (object)new { k = i.Key, tip = i.Tip, png = i.Png, exe = i.Exe }).ToList();

    public int Count => icons.Count;

    // Pour les tests : « clé programme version/état infobulle » de chaque icône reçue, affichée ou non.
    public List<string> Describe() => icons.Values.OrderBy(i => i.Seq).Select(i => $"{i.Key} {i.Exe} v{i.Version}{(i.Hidden ? " caché" : "")}{(Shown(i) ? "" : " non affiché")} {i.Tip.Replace('\n', ' ')}").ToList();

    static string MakePng(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        try
        {
            using var ic = Icon.FromHandle(h);
            using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawIcon(ic, new Rectangle(0, 0, 32, 32)); }
            using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    // ---------- clics ----------
    // button : « l » clic gauche, « d » double-clic, « r » clic droit. Même suite de messages que la barre de Windows
    // (les icônes de version 4 reçoivent aussi NIN_SELECT et WM_CONTEXTMENU, avec la position de la souris dans wParam).
    public bool Click(string key, string button)
    {
        if (!icons.TryGetValue(key, out var ic) || !N.IsWindow(ic.Hwnd) || ic.CbMsg == 0) return false;
        AllowSetForegroundWindow(ic.Pid);   // pour que l'appli puisse mettre son menu au premier plan
        var pt = Cursor.Position;
        void Send(uint ev)
        {
            IntPtr w, l;
            if (ic.Version >= 4) { w = new IntPtr((long)(uint)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF))); l = new IntPtr((long)(uint)(((ic.Uid & 0xFFFF) << 16) | ev)); }
            else { w = new IntPtr((long)ic.Uid); l = new IntPtr((long)ev); }
            N.PostMessage(ic.Hwnd, ic.CbMsg, w, l);
        }
        switch (button)
        {
            case "l": Send(0x201); Send(0x202); if (ic.Version >= 4) Send(0x400 /* NIN_SELECT */); break;
            case "d": Send(0x203); Send(0x202); break;
            case "r": Send(0x204); Send(0x205); if (ic.Version >= 4) Send(0x7B /* WM_CONTEXTMENU */); break;
            default: return false;
        }
        return true;
    }
}
