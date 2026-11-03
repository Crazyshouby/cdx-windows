using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

// Une fenêtre qui héberge une page WebView2 de la charte.
class HostForm : Form
{
    protected readonly IHostApp app;
    // Les chemins des fichiers glissés sur la page depuis une autre application, pour le message en cours (postMessageWithAdditionalObjects) ; null sinon.
    public string[] DroppedPaths { get; private set; }
    public readonly WebView2 Web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x33, 0x33, 0x33) };

    public HostForm(IHostApp a, string name)
    {
        app = a; Name = name;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x33, 0x33, 0x33);
        // l'icône de la fenêtre (Alt+Tab, liste des tâches de la barre) : celle de l'exécutable (icons\<application>.ico, famille pixel de la charte)
        try { if (Environment.ProcessPath != null) Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch (Exception) { }
        Controls.Add(Web);
    }

    protected virtual bool ToolWindow => true;   // WS_EX_TOOLWINDOW : pas dans Alt+Tab ni dans la liste des tâches
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; if (ToolWindow) cp.ExStyle |= 0x80; return cp; } }

    protected virtual void MapExtraHosts(CoreWebView2 c) { }

    // `page` : chemin sous web/, par exemple « shell/bar.html?theme=rouge ». L'hôte virtuel cdx.shell pointe sur le dossier des pages.
    public async Task InitWebAsync(CoreWebView2Environment env, string root, string page, double scale)
    {
        await Web.EnsureCoreWebView2Async(env);
        var c = Web.CoreWebView2;
        c.SetVirtualHostNameToFolderMapping("cdx.shell", root, CoreWebView2HostResourceAccessKind.Allow);
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsPinchZoomEnabled = false;
        c.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Web.ZoomFactor = 1.0 / scale;   // 1 pixel CSS = 1 pixel d'écran, quelle que soit l'échelle de Windows
        MapExtraHosts(c);
        // Une navigation hors des pages de la charte est refusée : sans cela, le bouton « précédent » de la souris (ou un lien) remplacerait la page.
        c.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://cdx.shell/")) { e.Cancel = true; Host.Log("navigation refusée : " + e.Uri); } };
        // Une fenêtre qu'une page voudrait ouvrir (lien d'un courrier, d'un terminal, window.open) : jamais une fenêtre WebView2 sans contrôle.
        // Une adresse web part dans le navigateur CDX ; tout le reste (cdx.shell, file:, javascript:…) est refusé.
        c.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !u.Host.Equals("cdx.shell", StringComparison.OrdinalIgnoreCase))
            { string url = u.AbsoluteUri, from = Host.AppName; _ = Task.Run(() => Bus.Send("browser", "open-url", new { url }, from, launch: true)); }
            else Host.Log("fenêtre refusée : " + e.Uri);
        };
        c.WebMessageReceived += (s, e) =>
        {
            // seules les pages de la charte parlent au programme (pas un cadre ni une page qui aurait échappé au filtre de navigation)
            if (!e.Source.StartsWith("https://cdx.shell/", StringComparison.OrdinalIgnoreCase)) { Host.Log("message refusé : " + e.Source); return; }
            DroppedPaths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
            // les messages du correcteur orthographique (cdx-spell.js : sp.check, sp.suggest, sp.add) sont servis par l'hôte commun (Spell.cs)
            try { if (!Spell.Handle(this, e.WebMessageAsJson)) app.OnMessage(this, e.WebMessageAsJson); } finally { DroppedPaths = null; }
        };
        c.Navigate("https://cdx.shell/" + page);
    }

    // Sans MaximizedBounds, une fenêtre sans cadre agrandie couvrirait aussi la barre : on la limite à la zone de travail.
    // MaximizedBounds part dans WM_GETMINMAXINFO (ptMaxPosition), que Windows lit RELATIF au moniteur : en coordonnées absolues, une fenêtre d'un
    // écran secondaire était agrandie à (origine de l'écran x 2), hors de tout écran (« la fenêtre disparaît », 01/11/2026).
    public void Maximize()
    {
        var s = Screen.FromControl(this); var wa = s.WorkingArea;
        MaximizedBounds = new Rectangle(wa.X - s.Bounds.X, wa.Y - s.Bounds.Y, wa.Width, wa.Height);
        WindowState = FormWindowState.Maximized;
    }

    public void Send(object o)
    {
        if (Web.CoreWebView2 != null) Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o));
    }

    // ---------- aimantation (Snap.cs) ----------
    // Un déplacement ou un redimensionnement est confié à Windows (WM_NCLBUTTONDOWN, Host.WindowCommand) ; pendant le geste, WM_MOVING / WM_SIZING
    // proposent un rectangle que l'on corrige : la fenêtre colle aux autres fenêtres de la suite (cdx-*, Messages, Audio Linker) et aux bords de la zone de
    // travail de son écran. Les voisines sont relevées une fois, à l'entrée du geste (WM_ENTERSIZEMOVE).
    // Windows propose chaque rectangle à partir du DERNIER rendu (donc de la position aimantée), pas de la souris : une fenêtre collée serait ré-aimantée à chaque
    // petit mouvement et ne se décollerait plus (vu le 01/11/2026). On tient donc le rectangle « libre » (ce qu'il serait sans aimantation) en lui ajoutant l'écart
    // entre la proposition et le dernier rendu, et c'est lui qu'on aimante : au-delà de la distance d'attraction, la fenêtre se libère.
    List<Rectangle> snapOthers; Rectangle snapFree, snapLast;
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        switch (m.Msg)
        {
            case 0x0231: /* WM_ENTERSIZEMOVE */ snapOthers = SuiteWindows(Handle); snapFree = snapLast = Bounds; break;
            case 0x0232: /* WM_EXITSIZEMOVE */ snapOthers = null; break;
            case 0x0216: /* WM_MOVING */
            case 0x0214: /* WM_SIZING */
                if (snapOthers == null || WindowState != FormWindowState.Normal) break;
                var rc = Marshal.PtrToStructure<RECT>(m.LParam); var p = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                var r = Rectangle.FromLTRB(snapFree.Left + (p.Left - snapLast.Left), snapFree.Top + (p.Top - snapLast.Top), snapFree.Right + (p.Right - snapLast.Right), snapFree.Bottom + (p.Bottom - snapLast.Bottom));
                snapFree = r;
                double k = DeviceDpi / 96.0; int dist = (int)Math.Round(Snap.Dist * k), gap = (int)Math.Round(Snap.Gap * k);
                var area = Screen.FromRectangle(r).WorkingArea;
                var s = m.Msg == 0x0216 ? new Rectangle(Snap.Move(r, snapOthers, area, dist, gap), r.Size)
                    : Snap.Size(r, (int)m.WParam, snapOthers, area, dist, gap, Math.Max(1, MinimumSize.Width), Math.Max(1, MinimumSize.Height));
                snapLast = s;
                if (s != p) Marshal.StructureToPtr(new RECT { Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom }, m.LParam, false);
                m.Result = (IntPtr)1;
                break;
        }
    }

    // Les fenêtres visibles de la suite (cdx-*.exe, Messages, Audio Linker) autres que `self`, en pixels d'écran. Une fenêtre réduite ou vide ne compte pas.
    static readonly string[] SuiteExes = { "cdx-messages.exe", "cdx-audio_linker.exe" };
    public static List<Rectangle> SuiteWindows(IntPtr self)
    {
        var list = new List<Rectangle>();
        N.EnumWindows((h, _) =>
        {
            if (h == self || !N.IsWindowVisible(h) || N.IsIconic(h)) return true;
            if (!N.GetWindowRect(h, out var rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top) return true;
            var exe = Path.GetFileName(N.ExePathOf(h) ?? "").ToLowerInvariant();
            if (exe.StartsWith("cdx-") || SuiteExes.Contains(exe)) list.Add(Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
