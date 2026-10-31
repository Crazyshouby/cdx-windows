namespace Cdx;

// Les tâches de fond de l'Explorateur (copier, déplacer, supprimer, chercher) : une file d'attente où elles s'exécutent l'une après l'autre sur un fil de travail, avec état,
// avancement, vitesse, pause, reprise et annulation. Le corps d'une tâche (JobBody) rend compte de son avancement par Job.Report et vérifie Job.Token ; il appelle Job.WaitIfPaused
// aux points où l'on peut s'arrêter. Chaque changement est signalé par l'événement Changed (la page le reçoit en message « fs.job » et le montre dans la fenêtre de progression, lot 4).
enum JobState { Queued, Running, Paused, Done, Failed, Canceled }

delegate void JobBody(Job job);

sealed class Job
{
    public int Id { get; init; }
    public string Kind { get; init; }          // « copy », « move », « delete », « search »...
    public string Title { get; init; }
    public JobState State { get; internal set; } = JobState.Queued;
    public long Done { get; private set; }       // octets (ou éléments) traités
    public long Total { get; private set; }      // total connu, 0 tant qu'on ne sait pas
    public string Current { get; private set; }  // l'élément en cours
    public string Error { get; internal set; }
    public double Speed { get; private set; }    // octets par seconde, lissé
    public DateTime Started { get; internal set; }
    public string From, To, Path;                // d'où, vers où, et ce que « Ouvrir » désigne (pour la fenêtre des transferts)
    public int Items;                            // éléments de premier niveau
    public List<string> Failed = new();          // ceux qui n'ont pas pu être traités : de quoi réessayer
    public object Tag;                           // le résultat du travail, pour celui qui l'a demandé
    public Func<Job> Retry;                      // remet les éléments en échec dans la file ; rend la nouvelle tâche
    internal Action<Job> Finished;               // appelé à la fin (réussie, annulée ou en échec), hors du fil d'interface
    public CancellationToken Token => cts.Token;
    internal readonly CancellationTokenSource cts = new();
    readonly ManualResetEventSlim gate = new(true);   // levé : on avance ; baissé : en pause
    internal JobBody Body;
    internal Action<Job> Notify;
    DateTime lastTick; long lastDone;

    // Le corps de la tâche dit où il en est. `total` : 0 = inchangé.
    public void Report(long done, long total = 0, string current = null)
    {
        Done = done; if (total > 0) Total = total; if (current != null) Current = current;
        var now = DateTime.UtcNow; double dt = (now - lastTick).TotalSeconds;
        if (dt >= 0.25)   // la vitesse se lisse (moyenne mobile) au plus quatre fois par seconde
        {
            double inst = (done - lastDone) / dt; Speed = Speed <= 0 ? inst : Speed * 0.7 + inst * 0.3;
            lastTick = now; lastDone = done; Notify?.Invoke(this);
        }
    }

    // À appeler aux points d'arrêt possibles : bloque pendant une pause, lève OperationCanceledException si la tâche est annulée.
    public void WaitIfPaused() { gate.Wait(cts.Token); Token.ThrowIfCancellationRequested(); }

    // La même attente pour un rappel natif (où une exception ne doit pas traverser) : bloque pendant une pause, rend true si la tâche est annulée.
    public bool WaitPaused() { try { gate.Wait(cts.Token); } catch (OperationCanceledException) { } return Token.IsCancellationRequested; }

    internal void SetPaused(bool paused) { if (paused) gate.Reset(); else gate.Set(); }
    internal void ResetClock() { lastTick = DateTime.UtcNow; lastDone = Done; }
}

sealed class JobQueue
{
    readonly object lk = new();
    readonly List<Job> jobs = new();
    readonly Queue<Job> waiting = new();
    int nextId = 1; bool running;
    public event Action<Job> Changed;

    public IReadOnlyList<Job> Snapshot() { lock (lk) return jobs.ToList(); }

    // Met une tâche en file ; elle part quand les précédentes ont fini. Rend la tâche (son Id sert à la piloter).
    public Job Enqueue(string kind, string title, JobBody body, Action<Job> finished = null, Action<Job> setup = null)
    {
        Job j;
        lock (lk)
        {
            j = new Job { Id = nextId++, Kind = kind, Title = title, Body = body, Notify = Fire, Finished = finished };
            setup?.Invoke(j);
            jobs.Add(j); waiting.Enqueue(j);
            if (!running) { running = true; Task.Run(Pump); }
        }
        Fire(j);
        return j;
    }

    void Fire(Job j) { try { Changed?.Invoke(j); } catch (Exception ex) { Host.Log("tâche : " + ex.Message); } }

    void Pump()
    {
        while (true)
        {
            Job j;
            lock (lk) { if (waiting.Count == 0) { running = false; return; } j = waiting.Dequeue(); }
            if (j.State == JobState.Canceled) continue;   // annulée avant d'avoir commencé
            j.State = JobState.Running; j.Started = DateTime.UtcNow; j.ResetClock(); Fire(j);
            try { j.Body(j); j.State = j.Token.IsCancellationRequested ? JobState.Canceled : JobState.Done; }
            catch (OperationCanceledException) { j.State = JobState.Canceled; }
            catch (Exception ex) { j.State = JobState.Failed; j.Error = ex.Message; Host.Log("tâche " + j.Id + " (" + j.Kind + ") : " + ex.Message); }
            Fire(j);
            try { j.Finished?.Invoke(j); } catch (Exception ex) { Host.Log("tâche " + j.Id + " : fin : " + ex.Message); }
        }
    }

    public bool Busy { get { lock (lk) return jobs.Any(j => j.State is JobState.Queued or JobState.Running or JobState.Paused); } }

    public Job Get(int id) => Find(id);

    Job Find(int id) { lock (lk) return jobs.FirstOrDefault(j => j.Id == id); }

    public bool Pause(int id) { var j = Find(id); if (j == null || j.State != JobState.Running) return false; j.SetPaused(true); j.State = JobState.Paused; Fire(j); return true; }
    public bool Resume(int id) { var j = Find(id); if (j == null || j.State != JobState.Paused) return false; j.State = JobState.Running; j.ResetClock(); j.SetPaused(false); Fire(j); return true; }

    // Annule : une tâche en attente est retirée sans démarrer ; une tâche en cours reçoit le signal (et sort d'une éventuelle pause).
    public bool Cancel(int id)
    {
        var j = Find(id); if (j == null || j.State is JobState.Done or JobState.Failed or JobState.Canceled) return false;
        if (j.State == JobState.Queued) { j.State = JobState.Canceled; Fire(j); }
        j.cts.Cancel(); j.SetPaused(false);
        return true;
    }

    // Les tâches terminées qui encombrent la liste (le plus ancien d'abord) : on n'en garde que `keep`.
    public void Prune(int keep = 20)
    {
        lock (lk)
        {
            var finished = jobs.Where(j => j.State is JobState.Done or JobState.Failed or JobState.Canceled).ToList();
            foreach (var j in finished.Take(Math.Max(0, finished.Count - keep))) jobs.Remove(j);
        }
    }
}
