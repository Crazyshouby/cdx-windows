namespace Kdx;

// La surveillance d'un dossier ouvert dans un onglet : quand quelque chose y change (création, suppression, renommage, taille, date, attributs), la page est prévenue (« fs.changed »)
// et relit le dossier, si bien que la liste suit sans F5. Les rafales d'événements (une copie, un téléchargement en cours) sont regroupées : au plus un message tous les 250 ms.
sealed class DirWatcher : IDisposable
{
    readonly FileSystemWatcher fsw;
    readonly System.Threading.Timer timer;
    int scheduled;   // 1 : un message est déjà prévu

    // `fire` est appelé (sur un fil du pool) au plus toutes les 250 ms tant que des événements arrivent ; il doit rejoindre le fil d'interface lui-même.
    public DirWatcher(string path, Action fire)
    {
        timer = new System.Threading.Timer(_ => { Interlocked.Exchange(ref scheduled, 0); try { fire(); } catch (Exception ex) { Host.Log("surveillance : " + ex.Message); } }, null, Timeout.Infinite, Timeout.Infinite);
        fsw = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        fsw.Created += (s, e) => Poke(); fsw.Deleted += (s, e) => Poke(); fsw.Renamed += (s, e) => Poke(); fsw.Changed += (s, e) => Poke();
        fsw.Error += (s, e) => Poke();   // tampon débordé : on ne sait plus quoi a changé, la page relit tout
        fsw.EnableRaisingEvents = true;
    }

    void Poke() { if (Interlocked.Exchange(ref scheduled, 1) == 0) timer.Change(250, Timeout.Infinite); }

    public void Dispose() { try { fsw.EnableRaisingEvents = false; fsw.Dispose(); timer.Dispose(); } catch { } }
}
