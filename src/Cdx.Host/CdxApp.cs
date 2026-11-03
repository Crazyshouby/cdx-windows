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
        Host.StartControlPipe(name, app.Anchor, app.HandleControl, () => app.Closing);
        Application.Run(app);
    }

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
