namespace Cdx;

// cdx-mail.exe [--theme <nom>]  ouvre « Courrier » : la boîte Gmail du compte Google relié à la suite (brique GoogleAuth de Cdx.Host), à la charte.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show ») et s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("mail");
        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("mail", out var mutex)) { SingleInstance.Relay("mail", "show", new { }); return; }
        using (mutex) CdxApp.Run("mail", () => new MailApp(theme), app => app.Open());   // le démarrage commun : CdxApp.cs
    }
}
