using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Un seul environnement WebView2 pour les programmes « outils » de la suite (Dépôts, Agenda, Courrier, Notes, Transferts, Terminal, Explorateur) :
// même dossier de données ET mêmes options, donc WebView2 ne lance qu'UN processus navigateur (plus son GPU et ses utilitaires) pour tous, au lieu
// d'un arbre complet par programme (mesure du 03/11/2026 : 42 processus msedgewebview2, 2,4 Go pour ces sept programmes).
// Restent à part : le shell (sa barre ne doit pas tomber avec les autres), le navigateur (pages du web, extensions) et la page cachée de YouTube.
// Les pages partagent l'origine https://cdx.shell, donc leur localStorage : les clés sont propres à chaque programme (cdx.agenda.*, cdx.notes.*,
// cdx.mail.*, cdx.split.<page>, cdx.cols.<page>, cdx.pv pour l'Explorateur).
static class WebEnv
{
    static Task<CoreWebView2Environment> ui;
    static readonly object gate = new();

    public static string UiDataDir => Path.Combine(Host.DataDir, "webview-ui");

    public static Task<CoreWebView2Environment> Ui()
    {
        lock (gate) return ui ??= CoreWebView2Environment.CreateAsync(null, UiDataDir, new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
    }

    // Le processus navigateur partagé est mort (rare) : les pages de toutes les fenêtres de ce programme sont perdues. La fenêtre est fermée et
    // l'environnement oublié : le prochain lancement en recrée un.
    public static void Watch(HostForm form, CoreWebView2 c)
    {
        c.ProcessFailed += (s, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
            Host.Log("processus navigateur WebView2 partagé arrêté : fenêtre fermée");
            lock (gate) ui = null;
            try { form.BeginInvoke(new Action(form.Close)); } catch (Exception ex) { Host.Log("fermeture après arrêt de WebView2 : " + ex.Message); }
        };
    }
}
