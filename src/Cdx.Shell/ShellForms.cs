using System.Diagnostics;
using System.IO.Pipes;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

class BarForm : HostForm
{
    Shell shell => (Shell)app;
    public BarForm(Shell s) : base(s, "bar") { TopMost = true; }

    protected override void WndProc(ref Message m)
    {
        if (shell != null && m.Msg == (int)shell.CallbackMsg && shell.CallbackMsg != 0) shell.OnAppBarMessage(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); shell.Cleanup(); }
}

// Les fenêtres surgissantes du shell : toujours au-dessus, elles se ferment quand elles perdent le focus. Le menu Démarrer, le centre rapide
// (ancré en bas à droite), la liste des setups d'Audio Linker (au-dessus du bouton de la barre : un menu déroulant natif sortirait de l'écran vers
// le bas) et le menu contextuel d'un bouton de la barre (épingles, fenêtres). Avant, quatre classes identiques à la page et au rappel près.
sealed class PopupForm : HostForm
{
    readonly Action onDeactivate;
    public PopupForm(Shell s, string page, Action onDeactivate) : base(s, page) { TopMost = true; this.onDeactivate = onDeactivate; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); onDeactivate(); }
}
// Le sélecteur Alt+Tab : une petite fenêtre centrée, qui ne prend jamais le focus (le crochet clavier lit les touches).
class SwitcherForm : HostForm
{
    public SwitcherForm(Shell s) : base(s, "alttab") { TopMost = true; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }   // WS_EX_NOACTIVATE
}
