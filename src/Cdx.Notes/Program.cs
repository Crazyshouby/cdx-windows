using System.Text.Json;

namespace Cdx;

// cdx-notes.exe [--theme <nom>] [chemin d'une note]  ouvre « Notes » : le coffre de notes (fichiers Markdown, liens [[…]], projets, tâches), avec Claude qui y range (serveur MCP cdx-notes).
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (et d'ouvrir la note donnée) puis s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("notes");
        var (theme, target) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("notes", out var mutex)) { SingleInstance.Relay("notes", string.IsNullOrEmpty(target) ? "show" : "open-note", string.IsNullOrEmpty(target) ? new { } : new { path = target }); return; }
        using (mutex) CdxApp.Run("notes", () => new NotesApp(theme, target), app => app.Open());   // le démarrage commun : CdxApp.cs
    }
}
