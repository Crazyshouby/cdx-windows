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

namespace Kdx;

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

class StartForm : HostForm
{
    Shell shell => (Shell)app;
    public StartForm(Shell s) : base(s, "start") { TopMost = true; }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); shell.OnStartDeactivated(); }
}

// Le centre rapide : ancré en bas à droite, se ferme quand il perd le focus (comme le menu Démarrer).
// La liste des setups d'AudioLinker, ouverte au-dessus du bouton de la barre (un menu déroulant natif sortirait de l'écran vers le bas) ; se ferme comme le centre rapide.
class AudioMenuForm : HostForm
{
    Shell shell => (Shell)app;
    public AudioMenuForm(Shell s) : base(s, "audiomenu") { TopMost = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); shell.OnAudioMenuDeactivated(); }
}

class CenterForm : HostForm
{
    Shell shell => (Shell)app;
    public CenterForm(Shell s) : base(s, "center") { TopMost = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); shell.OnCenterDeactivated(); }
}

// Le sélecteur Alt+Tab : une petite fenêtre centrée, qui ne prend jamais le focus (le crochet clavier lit les touches).
class SwitcherForm : HostForm
{
    public SwitcherForm(Shell s) : base(s, "alttab") { TopMost = true; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }   // WS_EX_NOACTIVATE
}
