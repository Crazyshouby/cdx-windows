using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// Une fenêtre qui héberge une page WebView2 de la charte.
class HostForm : Form
{
    protected readonly IHostApp app;
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

    // `page` : chemin sous web/, par exemple « shell/bar.html?theme=rouge ». L'hôte virtuel kdx.shell pointe sur le dossier des pages.
    public async Task InitWebAsync(CoreWebView2Environment env, string root, string page, double scale)
    {
        await Web.EnsureCoreWebView2Async(env);
        var c = Web.CoreWebView2;
        c.SetVirtualHostNameToFolderMapping("kdx.shell", root, CoreWebView2HostResourceAccessKind.Allow);
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsPinchZoomEnabled = false;
        c.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Web.ZoomFactor = 1.0 / scale;   // 1 pixel CSS = 1 pixel d'écran, quelle que soit l'échelle de Windows
        MapExtraHosts(c);
        // Une navigation hors des pages de la charte est refusée : sans cela, le bouton « précédent » de la souris (ou un lien) remplacerait la page.
        c.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://kdx.shell/")) { e.Cancel = true; Host.Log("navigation refusée : " + e.Uri); } };
        c.WebMessageReceived += (s, e) => app.OnMessage(this, e.WebMessageAsJson);
        c.Navigate("https://kdx.shell/" + page);
    }

    // Sans MaximizedBounds, une fenêtre sans cadre agrandie couvrirait aussi la barre : on la limite à la zone de travail.
    public void Maximize() { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = FormWindowState.Maximized; }

    public void Send(object o)
    {
        if (Web.CoreWebView2 != null) Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o));
    }
}
