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
        c.WebMessageReceived += (s, e) =>
        {
            DroppedPaths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
            try { app.OnMessage(this, e.WebMessageAsJson); } finally { DroppedPaths = null; }
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
}
