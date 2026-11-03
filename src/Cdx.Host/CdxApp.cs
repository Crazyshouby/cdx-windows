using System.Text.Json;

namespace Cdx;

// Un programme de la suite tel que son démarrage le voit (les classes *App : Agenda, Courrier, Dépôts...).
interface ICdxApp : IHostApp
{
    Control Anchor { get; }                    // installe le contexte de synchronisation de l'interface et sert à y poser un travail
    bool Closing { get; }
    bool OnBus(string type, string from, JsonElement data);
    Task<string> HandleControl(string line);   // le canal de test (debug.txt)
}

// Un programme dont le canal de test vise une seule page : `eval` et `cmd` y sont traités en commun (CdxApp.Control). Le navigateur, dont `eval`
// vise l'onglet actif et non sa fenêtre, ne l'implémente pas et garde les siens.
interface IPageApp
{
    HostForm ControlTarget { get; }   // la fenêtre visée, ou null s'il n'y en a pas
}

// Le démarrage d'un programme, écrit une fois : avant, les onze Program.Main répétaient les mêmes quinze lignes, et une correction faite dans l'un
// (l'activation d'une fenêtre réduite, présente dans Agenda) n'atteignait pas les autres. Ce qui précède (Host.Init, arguments, auto-tests,
// instance unique et son relais) reste propre à chaque programme.
static class CdxApp
{
    // À appeler une fois l'instance unique obtenue (le mutex reste à l'appelant).
    // `open` : la première fenêtre, ouverte dans la boucle de messages (WebView2 en a besoin). `register` : inscrire l'exécutable dans apps.json.
    public static void Run<T>(string name, Func<T> create, Action<T> open, bool register = true) where T : ApplicationContext, ICdxApp
    {
        ConfigureWinForms();
        var app = create();
        Bus.Listen(name, app.OnBus);
        if (register) Suite.RegisterApp(name, Environment.ProcessPath);
        app.Anchor.BeginInvoke(new Action(() => open(app)));
        Host.StartControlPipe(name, app.Anchor, Control(app), () => app.Closing);
        Application.Run(app);
    }

    // Le gestionnaire du canal de test : `eval <js>` et `cmd <json>` vers la page d'un IPageApp (avant, recopiés dans neuf programmes), le reste au programme.
    public static Func<string, Task<string>> Control(ICdxApp app) => async line =>
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        if (app is IPageApp p && c is "eval" or "cmd")
        {
            var f = p.ControlTarget;
            if (c == "cmd") { if (f != null) app.OnMessage(f, a); return "ok"; }
            return f?.Web.CoreWebView2 == null ? "pas de fenêtre" : await f.Web.CoreWebView2.ExecuteScriptAsync(a);
        }
        return await app.HandleControl(line);
    };

    // DPI par écran, styles visuels, et les exceptions journalisées (interface et fatales).
    public static void ConfigureWinForms()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);
    }
}
