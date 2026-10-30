// KDX-Frame : habille le cadre de toutes les fenêtres de Windows 11 avec la charte KDX-UI (couche 1).
//
// Pose sur chaque fenêtre de premier niveau, via DwmSetWindowAttribute :
//   - la couleur de la bordure et de la barre de titre (accent pour la fenêtre active, gris pour les autres),
//   - la couleur du texte du titre,
//   - les coins droits (DWMWCP_DONOTROUND),
//   - le mode sombre du cadre (boutons de titre blancs).
// Rien n'est modifié dans le système : tout disparaît avec "kdx-frame.exe --reset" ou quand la fenêtre se ferme.
//
//   kdx-frame.exe [thème]     résident (icône dans la zone de notification), thème : rouge bleu vert violet ambre sarcelle charbon
//   kdx-frame.exe --reset     rend leur cadre d'origine à toutes les fenêtres ouvertes
//   kdx-frame.exe --status    échantillonne les pixels réels de la bordure et du titre de chaque fenêtre (les couleurs posées
//                             ne se relisent pas : DwmGetWindowAttribute renvoie E_INVALIDARG)
//
// Écrit en C# 5 : se compile avec le csc de .NET Framework, présent sur tout Windows (voir appliquer.ps1).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class KdxFrame
{
    // ---- DWM ----
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CLOAKED = 14;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_TEXT_COLOR = 36;
    const int DWMWCP_DEFAULT = 0;
    const int DWMWCP_DONOTROUND = 1;
    const uint COLOR_DEFAULT = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out uint value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---- user32 ----
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    const uint GA_ROOT = 2;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_UNCLOAKED = 0x8018;

    // ---- charte : rampes d'accent de themes.css (bord, milieu) ----
    static readonly Dictionary<string, uint[]> Themes = new Dictionary<string, uint[]>
    {
        { "rouge",    new uint[] { C(0xcc, 0x00, 0x00), C(0x88, 0x00, 0x00) } },
        { "bleu",     new uint[] { C(0x00, 0x00, 0xff), C(0x00, 0x00, 0xaa) } },
        { "vert",     new uint[] { C(0x00, 0xcc, 0x00), C(0x00, 0x88, 0x00) } },
        { "violet",   new uint[] { C(0xe0, 0x00, 0xe0), C(0x96, 0x00, 0x96) } },
        { "ambre",    new uint[] { C(0xcc, 0x47, 0x04), C(0x88, 0x2f, 0x03) } },
        { "sarcelle", new uint[] { C(0x75, 0xeb, 0xeb), C(0x4e, 0x9c, 0x9c) } },
        { "charbon",  new uint[] { C(0xcc, 0xcc, 0xcc), C(0x88, 0x88, 0x88) } },
    };
    static uint C(int r, int g, int b) { return (uint)(r | (g << 8) | (b << 16)); }   // COLORREF = 0x00BBGGRR

    static uint activeBorder, activeCaption;
    static readonly uint TextActive = C(0xff, 0xff, 0xff);
    static readonly uint InactiveBorder = C(0x55, 0x55, 0x55), InactiveCaption = C(0x33, 0x33, 0x33), TextInactive = C(0x88, 0x88, 0x88);

    static readonly Dictionary<IntPtr, int> applied = new Dictionary<IntPtr, int>();   // 1 = active, 2 = inactive
    static IntPtr lastForeground = IntPtr.Zero;
    static WinEventProc hookProc;   // gardé en champ : le ramasse-miettes ne doit pas le libérer

    static readonly string[] SkipClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "SysShadow", "tooltips_class32" };

    static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    static string TitleOf(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

    static bool Eligible(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetAncestor(h, GA_ROOT) != h) return false;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        int st = GetWindowLong(h, GWL_STYLE);
        if ((st & WS_CAPTION) != WS_CAPTION && (st & WS_THICKFRAME) == 0) return false;
        int cloaked; DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4);
        if (cloaked != 0) return false;
        string cls = ClassOf(h);
        foreach (string s in SkipClasses) if (cls == s) return false;
        return true;
    }

    static void Apply(IntPtr h, bool active)
    {
        uint border = active ? activeBorder : InactiveBorder, caption = active ? activeCaption : InactiveCaption, text = active ? TextActive : TextInactive;
        int dark = 1, corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, 4);
        DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, 4);
        DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref text, 4);
        DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        applied[h] = active ? 1 : 2;
    }

    static void Sweep()
    {
        IntPtr fg = GetForegroundWindow();
        var dead = new List<IntPtr>();
        foreach (IntPtr h in applied.Keys) if (!IsWindow(h)) dead.Add(h);
        foreach (IntPtr h in dead) applied.Remove(h);
        EnumWindows(delegate (IntPtr h, IntPtr p)
        {
            if (!Eligible(h)) return true;
            int want = (h == fg) ? 1 : 2, have;
            if (!applied.TryGetValue(h, out have) || have != want) Apply(h, want == 1);
            return true;
        }, IntPtr.Zero);
    }

    static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;   // OBJID_WINDOW seulement
        if (evt == EVENT_SYSTEM_FOREGROUND)
        {
            if (lastForeground != IntPtr.Zero && lastForeground != hwnd && IsWindow(lastForeground) && Eligible(lastForeground)) Apply(lastForeground, false);
            if (Eligible(hwnd)) Apply(hwnd, true);
            lastForeground = hwnd;
        }
        else if (Eligible(hwnd)) Apply(hwnd, hwnd == GetForegroundWindow());
    }

    static void Reset()
    {
        EnumWindows(delegate (IntPtr h, IntPtr p)
        {
            if (GetAncestor(h, GA_ROOT) != h) return true;
            uint def = COLOR_DEFAULT; int corner = DWMWCP_DEFAULT;
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref def, 4);
            DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref def, 4);
            DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref def, 4);
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            return true;
        }, IntPtr.Zero);
    }

    // Windows refuse de relire les attributs de couleur (E_INVALIDARG) : on échantillonne donc les pixels réels à l'écran,
    // au milieu du bord gauche (la bordure DWM) et au milieu de la barre de titre.
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    static void Status()
    {
        SetProcessDPIAware();
        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr fg = GetForegroundWindow();
        EnumWindows(delegate (IntPtr h, IntPtr p)
        {
            if (!Eligible(h)) return true;
            RECT r; int k;
            DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16);
            DwmGetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, out k, 4);
            int midY = (r.Top + r.Bottom) / 2;
            string cls = ClassOf(h);
            Console.WriteLine("{0}{1,-24} coins={2} bord(x-1,x,x+1)={3},{4},{5} titre={6}  {7}", h == fg ? "* " : "  ", cls.Length > 24 ? cls.Substring(0, 24) : cls, k,
                Hex(GetPixel(dc, r.Left - 1, midY)), Hex(GetPixel(dc, r.Left, midY)), Hex(GetPixel(dc, r.Left + 1, midY)), Hex(GetPixel(dc, r.Left + 200, r.Top + 12)), TitleOf(h));
            return true;
        }, IntPtr.Zero);
        ReleaseDC(IntPtr.Zero, dc);
    }

    static string Hex(uint colorref)
    {
        if (colorref == 0xFFFFFFFF) return "hors-écran";
        return "#" + (colorref & 0xFF).ToString("x2") + ((colorref >> 8) & 0xFF).ToString("x2") + ((colorref >> 16) & 0xFF).ToString("x2");
    }

    static Icon MakeIcon(uint edge)
    {
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.FillRectangle(new SolidBrush(Color.FromArgb((int)(edge & 0xFF), (int)((edge >> 8) & 0xFF), (int)((edge >> 16) & 0xFF))), 2, 2, 12, 12);
                g.FillRectangle(Brushes.White, 2, 2, 12, 2);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "rouge";
        if (arg == "--reset") { Reset(); return 0; }
        if (arg == "--status") { Status(); return 0; }
        if (!Themes.ContainsKey(arg)) { Console.Error.WriteLine("Thème inconnu : " + arg); return 1; }
        activeBorder = Themes[arg][0]; activeCaption = Themes[arg][1];

        bool created;
        using (new Mutex(true, "Local\\KdxFrame", out created))
        {
            if (!created) return 0;   // déjà lancé
            FreeConsole();
            Application.EnableVisualStyles();
            Sweep();
            lastForeground = GetForegroundWindow();
            hookProc = OnEvent;
            IntPtr h1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, 0);
            IntPtr h2 = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, hookProc, 0, 0, 0);
            IntPtr h3 = SetWinEventHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, hookProc, 0, 0, 0);

            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += delegate { Sweep(); };
            timer.Start();

            var menu = new ContextMenu();
            menu.MenuItems.Add("Quitter KDX-Frame (garde le cadre actuel)", delegate { Application.Exit(); });
            menu.MenuItems.Add("Quitter et rendre le cadre d'origine", delegate { Reset(); Application.Exit(); });
            var tray = new NotifyIcon { Icon = MakeIcon(activeBorder), Text = "KDX-Frame : cadre des fenêtres (" + arg + ")", ContextMenu = menu, Visible = true };

            Application.Run();

            tray.Visible = false; timer.Stop();
            UnhookWinEvent(h1); UnhookWinEvent(h2); UnhookWinEvent(h3);
        }
        return 0;
    }
}
