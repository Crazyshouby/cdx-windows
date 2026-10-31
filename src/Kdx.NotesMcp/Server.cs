using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kdx.Coffre;

namespace Kdx.NotesMcp;

// Le serveur MCP (Model Context Protocol) des notes : JSON-RPC 2.0, un message par ligne sur l'entrée et la sortie standard.
// La sortie standard ne porte que des réponses ; tout journal va sur l'erreur standard. Le coffre est relu à chaque appel : ce qu'on modifie
// dans l'application Notes est vu aussitôt, et inversement.
static class TextExt { public static StringBuilder AppendLn(this StringBuilder sb, string s) => sb.Append(s).Append('\n'); }   // sauts de ligne « \n » partout, quel que soit le système

sealed class Server
{
    public static readonly string[] Protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
    const int MaxArgChars = 2_100_000;
    readonly Func<Vault> open; Vault vault;

    public Server(Func<Vault> open) { this.open = open; }

    // Rend la réponse à envoyer (une ligne de JSON), ou null s'il n'y en a pas (notification).
    public string Handle(string line)
    {
        JsonNode msg;
        try { msg = JsonNode.Parse(line); }
        catch { return Error(null, -32700, "JSON illisible"); }
        if (msg is not JsonObject o) return Error(null, -32600, "Requête invalide (un objet JSON est attendu)");
        string method = o["method"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
        bool isNotification = !o.ContainsKey("id");
        JsonNode id = isNotification ? null : o["id"]?.DeepClone();
        if (method == null) return isNotification ? null : Error(id, -32600, "Requête invalide (méthode absente)");
        try
        {
            switch (method)
            {
                case "initialize": return Result(id, Initialize(o["params"] as JsonObject));
                case "ping": return isNotification ? null : Result(id, new JsonObject());
                case "tools/list": return isNotification ? null : Result(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call": return isNotification ? null : Result(id, Call(o["params"] as JsonObject));
                default:
                    if (isNotification) return null;   // notifications/initialized, notifications/cancelled… : rien à répondre
                    return Error(id, -32601, "Méthode inconnue : " + method);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("kdx-notes : " + ex); return isNotification ? null : Error(id, -32603, "Erreur interne du serveur"); }
    }

    static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    static string Error(JsonNode id, int code, string message) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    JsonNode Initialize(JsonObject p)
    {
        string want = p?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string use = want != null && Protocols.Contains(want) ? want : Protocols[0];
        return new JsonObject
        {
            ["protocolVersion"] = use,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "kdx-notes", ["version"] = "1.0.0" },
            ["instructions"] = "Les notes de Valentin : un coffre de fichiers Markdown (liens [[Nom]], propriétés en tête de note, #étiquettes, tâches « - [ ] »). "
                + "Commence par vault_info pour les conventions. Pour modifier une note : notes_read (il donne l'empreinte), puis notes_edit / notes_append / notes_write avec cette empreinte. "
                + "Rien ne se supprime : notes_trash déplace dans .trash, et seulement quand Valentin le demande. Les fiches de projet ont « type: projet » ; projects_list et tasks_list en donnent la vue d'ensemble."
        };
    }

    // ---------- outils ----------
    sealed record Tool(string Name, string Description, JsonObject Schema, bool ReadOnly, Func<JsonObject, string> Run);
    List<Tool> tools;

    static JsonObject Obj(params (string name, string type, string desc)[] props) => Obj(props, props.Where(p => !p.desc.StartsWith("(facultatif)")).Select(p => p.name).ToArray());
    static JsonObject Obj((string name, string type, string desc)[] props, string[] required)
    {
        var p = new JsonObject();
        foreach (var (name, type, desc) in props) p[name] = type == "string|array" ? new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }), ["description"] = desc } : new JsonObject { ["type"] = type, ["description"] = desc };
        var r = new JsonArray(); foreach (var x in required) r.Add(x);
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = r, ["additionalProperties"] = false };
    }

    List<Tool> Tools() => tools ??= new List<Tool>
    {
        new("vault_info", "Vue d'ensemble du coffre de notes : nombre de notes, dossiers, étiquettes les plus utilisées, projets, tâches ouvertes, et les conventions d'écriture. À lire en premier.", Obj(), true, a => Info()),
        new("notes_list", "Liste les notes d'un dossier (le coffre entier par défaut) avec leur titre, leur type et leurs étiquettes.", Obj((new[] { ("folder", "string", "(facultatif) Dossier, par exemple « Projets »."), ("recursive", "boolean", "(facultatif) Inclure les sous-dossiers (oui par défaut)."), ("limit", "integer", "(facultatif) Nombre maximal de notes (200 par défaut, 500 au plus).") }), Array.Empty<string>()), true, ListNotes),
        new("notes_search", "Cherche dans les notes (titres, étiquettes, titres de section, contenu). Tous les mots sont requis, accents et casse ignorés ; « phrase entre guillemets » ; filtres : tag:projet  in:Dossier  type:projet.", Obj((new[] { ("query", "string", "Mots à chercher, avec filtres éventuels."), ("limit", "integer", "(facultatif) Nombre maximal de résultats (20 par défaut, 50 au plus).") }), new[] { "query" }), true, Search),
        new("notes_read", "Lit une note : propriétés, contenu, liens sortants et liens entrants. Donne aussi son empreinte (hash), à renvoyer pour la modifier.", Obj((new[] { ("path", "string", "Chemin de la note dans le coffre, par exemple « Projets/Zeta.md » (le .md est facultatif)."), ("max_chars", "integer", "(facultatif) Longueur maximale du contenu rendu (60000 par défaut).") }), new[] { "path" }), true, Read),
        new("notes_create", "Crée une note (et ses dossiers). Refuse si elle existe déjà. Écrire le contenu en Markdown : titre « # », liens [[Nom]], propriétés entre « --- » en tête, #étiquettes, tâches « - [ ] ».", Obj((new[] { ("path", "string", "Chemin de la nouvelle note, par exemple « Idées/Piste.md »."), ("content", "string", "Contenu complet de la note.") }), new[] { "path", "content" }), false, Create),
        new("notes_write", "Remplace tout le contenu d'une note qui existe. Exige l'empreinte (hash) donnée par notes_read : si la note a changé depuis, l'appel est refusé. Préférer notes_edit ou notes_append pour une petite modification.", Obj((new[] { ("path", "string", "Chemin de la note."), ("content", "string", "Nouveau contenu complet."), ("hash", "string", "Empreinte lue avec notes_read.") }), new[] { "path", "content", "hash" }), false, Write),
        new("notes_append", "Ajoute du texte à la fin d'une note (journal, nouvelle action…).", Obj((new[] { ("path", "string", "Chemin de la note."), ("text", "string", "Texte à ajouter.") }), new[] { "path", "text" }), false, Append),
        new("notes_edit", "Remplace un passage d'une note. Le passage doit apparaître une seule fois (sinon donner plus de contexte). L'empreinte est facultative mais protège d'un changement fait entre-temps.", Obj((new[] { ("path", "string", "Chemin de la note."), ("old_text", "string", "Passage à remplacer, tel qu'il est écrit."), ("new_text", "string", "Texte de remplacement."), ("hash", "string", "(facultatif) Empreinte lue avec notes_read.") }), new[] { "path", "old_text", "new_text" }), false, Edit),
        new("notes_set_property", "Fixe une propriété dans l'en-tête d'une note (statut, priorité, tags…). Une liste de texte devient « [a, b] ». Le reste de la note est inchangé.", Obj((new[] { ("path", "string", "Chemin de la note."), ("key", "string", "Nom de la propriété."), ("value", "string|array", "Valeur : un texte, ou une liste de textes.") }), new[] { "path", "key", "value" }), false, SetProp),
        new("notes_move", "Déplace ou renomme une note. Par défaut, les liens [[…]] qui la visent sont réécrits dans tout le coffre.", Obj((new[] { ("from", "string", "Chemin actuel."), ("to", "string", "Nouveau chemin."), ("update_links", "boolean", "(facultatif) Réécrire les liens (oui par défaut).") }), new[] { "from", "to" }), false, Move),
        new("notes_trash", "Retire une note : elle est déplacée dans le dossier .trash du coffre, jamais supprimée. À ne faire que sur demande de Valentin.", Obj((new[] { ("path", "string", "Chemin de la note.") }), new[] { "path" }), false, Trash),
        new("folders_create", "Crée un dossier (et ses parents) dans le coffre.", Obj((new[] { ("path", "string", "Chemin du dossier, par exemple « Projets/Archives ».") }), new[] { "path" }), false, Folder),
        new("notes_links", "Les liens d'une note : ceux qu'elle contient (avec ceux qui ne mènent nulle part) et les notes qui pointent vers elle. Sans chemin : tous les liens sans cible du coffre (des notes à écrire).", Obj((new[] { ("path", "string", "(facultatif) Chemin de la note.") }), Array.Empty<string>()), true, Links),
        new("notes_tags", "Les étiquettes du coffre avec leur nombre de notes ; avec « tag », les notes qui la portent.", Obj((new[] { ("tag", "string", "(facultatif) Une étiquette, sans le #.") }), Array.Empty<string>()), true, Tags),
        new("projects_list", "Les fiches de projet (propriété « type: projet ») : statut, priorité, dépôt, tâches ouvertes, prochaine échéance.", Obj((new[] { ("status", "string", "(facultatif) Ne garder que ce statut : actif, en pause, terminé…") }), Array.Empty<string>()), true, Projects),
        new("projects_create", "Crée une fiche de projet à partir du modèle, dans Projets/ : propriétés (type, statut, dépôt, priorité, résumé), objectif, prochaines actions, notes, journal.", Obj((new[] { ("name", "string", "Nom du projet."), ("repo", "string", "(facultatif) Chemin du dépôt git sur le disque, ou son adresse."), ("summary", "string", "(facultatif) Résumé en une phrase.") }), new[] { "name" }), false, ProjectCreate),
        new("tasks_list", "Les tâches « - [ ] » du coffre, triées par échéance (📅 AAAA-MM-JJ ou due:AAAA-MM-JJ). Les modèles sont ignorés.", Obj((new[] { ("under", "string", "(facultatif) Dossier ou note à examiner, par exemple « Projets »."), ("include_done", "boolean", "(facultatif) Inclure les tâches faites (non par défaut).") }), Array.Empty<string>()), true, TaskList),
    };

    JsonArray ToolList()
    {
        var a = new JsonArray();
        foreach (var t in Tools())
            a.Add(new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = false, ["idempotentHint"] = false, ["openWorldHint"] = false }
            });
        return a;
    }

    JsonNode Call(JsonObject p)
    {
        string name = p?["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
        var tool = Tools().FirstOrDefault(t => t.Name == name);
        if (tool == null) return Fail("Outil inconnu : " + name);
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        foreach (var kv in args) if (kv.Value is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > MaxArgChars) return Fail($"L'argument « {kv.Key} » est trop long.");
        try
        {
            vault ??= open();
            vault.Refresh();
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = tool.Run(args) }), ["isError"] = false };
        }
        catch (VaultException ex) { return Fail(ex.Message); }
        catch (ArgException ex) { return Fail(ex.Message); }
        catch (Exception ex) { Console.Error.WriteLine("kdx-notes : outil " + name + " : " + ex); return Fail("Erreur interne : l'opération n'a pas pu être faite."); }
    }

    static JsonNode Fail(string message) => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), ["isError"] = true };

    // ---------- arguments ----------
    sealed class ArgException : Exception { public ArgException(string m) : base(m) { } }
    static string S(JsonObject a, string k, bool required = true)
    {
        if (!a.TryGetPropertyValue(k, out var v) || v == null) { if (required) throw new ArgException($"Argument manquant : « {k} »."); return null; }
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        throw new ArgException($"L'argument « {k} » doit être un texte.");
    }
    static int I(JsonObject a, string k, int def, int min, int max)
    {
        if (!a.TryGetPropertyValue(k, out var v) || v == null) return def;
        if (v is JsonValue jv && jv.TryGetValue<int>(out var i)) return Math.Clamp(i, min, max);
        throw new ArgException($"L'argument « {k} » doit être un entier.");
    }
    static bool B(JsonObject a, string k, bool def)
    {
        if (!a.TryGetPropertyValue(k, out var v) || v == null) return def;
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        throw new ArgException($"L'argument « {k} » doit être vrai ou faux.");
    }

    // ---------- mise en forme ----------
    static string Line(NoteInfo n)
    {
        string t = n.Prop("type"), st = n.Prop("statut");
        return $"{n.Path}" + (t != null ? $"  [{t}{(st != null ? ", " + st : "")}]" : "") + (n.Tags.Count > 0 ? "  #" + string.Join(" #", n.Tags) : "");
    }
    static string Day(DateTime d) => d.ToString("yyyy-MM-dd");

    string Info()
    {
        var tags = vault.Tags().Take(15).Select(kv => $"#{kv.Key} ({kv.Value})");
        var sb = new StringBuilder();
        sb.AppendLn($"Coffre : {vault.Root}");
        sb.AppendLn($"{vault.Count} notes, {vault.Folders().Count} dossiers, {vault.Projects().Count} projets, {vault.Tasks().Count} tâches ouvertes, {vault.Unresolved().Count} liens sans cible.");
        sb.AppendLn("Dossiers : " + (vault.Folders().Count == 0 ? "(aucun)" : string.Join(", ", vault.Folders().Take(40))));
        sb.AppendLn("Étiquettes : " + (vault.Tags().Count == 0 ? "(aucune)" : string.Join("  ", tags)));
        sb.AppendLn("");
        sb.AppendLn("Conventions : des fichiers Markdown ; un lien s'écrit [[Nom de la note]] (ou [[Dossier/Note|texte]]) ; une étiquette #exemple ; une tâche « - [ ] à faire 📅 2026-12-01 ».");
        sb.AppendLn("Une fiche de projet est dans Projets/ avec l'en-tête « type: projet », « statut: actif | en pause | terminé », « depot: chemin du dépôt », « priorite: haute | normale | basse », « resume: … ».");
        sb.AppendLn("Les modèles sont dans Modèles/. Ranger : notes_move (les liens suivent). Retirer : notes_trash (récupérable dans .trash).");
        return sb.ToString().TrimEnd();
    }

    string ListNotes(JsonObject a)
    {
        string folder = S(a, "folder", false)?.Replace('\\', '/').Trim('/'); bool rec = B(a, "recursive", true); int limit = I(a, "limit", 200, 1, 500);
        var l = vault.Notes.Where(n => folder == null || folder.Length == 0 || n.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) || (rec && n.Folder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        if (l.Count == 0) return folder == null ? "Le coffre est vide." : $"Aucune note dans « {folder} ».";
        return $"{l.Count} note(s)" + (l.Count > limit ? $" (les {limit} premières)" : "") + " :\n" + string.Join("\n", l.Take(limit).Select(Line));
    }

    string Search(JsonObject a)
    {
        string q = S(a, "query"); if (q.Length > 500) throw new ArgException("La requête est trop longue (500 caractères au plus).");
        var hits = vault.Search(q, I(a, "limit", 20, 1, 50));
        if (hits.Count == 0) return "Aucun résultat.";
        return $"{hits.Count} résultat(s) :\n" + string.Join("\n", hits.Select((h, i) => $"{i + 1}. {h.Path}" + (h.Tags.Count > 0 ? "  #" + string.Join(" #", h.Tags) : "") + (h.Snippet.Length > 0 ? $"\n   ligne {h.Line} : {h.Snippet}" : "")));
    }

    string Read(JsonObject a)
    {
        string path = S(a, "path"); int max = I(a, "max_chars", 60000, 500, 500000);
        string text = vault.Read(path, out string hash); var n = vault.Get(path);
        bool cut = text.Length > max;
        var sb = new StringBuilder();
        sb.AppendLn($"Note : {n.Path}"); sb.AppendLn($"Empreinte (hash) : {hash}"); sb.AppendLn($"Modifiée le : {Day(n.Modified)}");
        foreach (var kv in n.Props) sb.AppendLn($"Propriété {kv.Key} : {(kv.Value is List<string> l ? string.Join(", ", l) : kv.Value)}");
        var back = vault.Backlinks(path);
        if (n.Links.Count > 0) sb.AppendLn("Liens : " + string.Join(", ", n.Links.Select(l => (l.Resolved != null ? l.Resolved : l.Target + " (sans note)")).Distinct()));
        if (back.Count > 0) sb.AppendLn("Liens entrants : " + string.Join(", ", back.Select(b => b.Path).Distinct()));
        if (cut) sb.AppendLn($"ATTENTION : contenu coupé à {max} caractères sur {text.Length}.");
        sb.AppendLn("------"); sb.Append(cut ? text[..max] : text);
        return sb.ToString();
    }

    // Après une écriture : le chemin et la nouvelle empreinte, pour enchaîner sans relire.
    string Done(NoteInfo n, string what) { vault.Read(n.Path, out string h); return $"{what} : {n.Path} (empreinte {h})"; }

    string Create(JsonObject a) => Done(vault.Create(S(a, "path"), S(a, "content")), "Note créée");
    string Write(JsonObject a) => Done(vault.Overwrite(S(a, "path"), S(a, "content"), S(a, "hash")), "Note remplacée");
    string Append(JsonObject a) => Done(vault.Append(S(a, "path"), S(a, "text")), "Texte ajouté");
    string Edit(JsonObject a) => Done(vault.ReplaceText(S(a, "path"), S(a, "old_text"), S(a, "new_text"), S(a, "hash", false)), "Note modifiée");

    string SetProp(JsonObject a)
    {
        object value;
        if (a["value"] is JsonArray arr) value = arr.Select(x => x?.GetValue<string>() ?? "").ToList();
        else value = S(a, "value");
        return Done(vault.SetProperty(S(a, "path"), S(a, "key"), value), "Propriété fixée");
    }

    string Move(JsonObject a)
    {
        string from = S(a, "from"), to = S(a, "to"); int n = vault.Move(from, to, B(a, "update_links", true));
        return $"Déplacée : {Vault.NormalizePath(from)} → {Vault.NormalizePath(to)}" + (n > 0 ? $" ; {n} note(s) dont les liens ont été réécrits." : ".");
    }

    string Trash(JsonObject a) => "Retirée : déplacée dans " + vault.Trash(S(a, "path")) + " (récupérable en la remettant à sa place).";

    string Folder(JsonObject a) { string p = S(a, "path"); vault.CreateFolder(p); return "Dossier prêt : " + Vault.NormalizePath(p, false); }

    string Links(JsonObject a)
    {
        string path = S(a, "path", false);
        if (path == null)
        {
            var u = vault.Unresolved();
            return u.Count == 0 ? "Aucun lien sans cible." : $"{u.Count} lien(s) sans note :\n" + string.Join("\n", u.Select(x => $"[[{x.Target}]] ×{x.Count} (par exemple dans {x.From})"));
        }
        var n = vault.Get(path) ?? throw new VaultException("introuvable", $"La note « {path} » n'existe pas.");
        var sb = new StringBuilder();
        sb.AppendLn("Liens sortants :");
        foreach (var g in n.Links.GroupBy(l => l.Resolved ?? ("?" + l.Target))) sb.AppendLn("  " + (g.Key.StartsWith("?") ? $"[[{g.Key[1..]}]] — pas de note" : g.Key) + (g.Count() > 1 ? $" ×{g.Count()}" : ""));
        if (n.Links.Count == 0) sb.AppendLn("  (aucun)");
        var back = vault.Backlinks(path);
        sb.AppendLn("Liens entrants :");
        foreach (var b in back) sb.AppendLn($"  {b.Path} (ligne {b.Line}) : {b.Context}");
        if (back.Count == 0) sb.AppendLn("  (aucun)");
        return sb.ToString().TrimEnd();
    }

    string Tags(JsonObject a)
    {
        string tag = S(a, "tag", false)?.TrimStart('#');
        if (tag != null) { var h = vault.Search("tag:" + tag, 100); return h.Count == 0 ? $"Aucune note avec #{tag}." : $"{h.Count} note(s) avec #{tag} :\n" + string.Join("\n", h.Select(x => x.Path)); }
        var t = vault.Tags(); return t.Count == 0 ? "Aucune étiquette." : string.Join("\n", t.Select(kv => $"#{kv.Key} ({kv.Value})"));
    }

    string Projects(JsonObject a)
    {
        var l = vault.Projects(S(a, "status", false));
        if (l.Count == 0) return "Aucun projet.";
        return $"{l.Count} projet(s) :\n" + string.Join("\n", l.Select(p => $"- {p.Name} [{(p.Status.Length > 0 ? p.Status : "sans statut")}" + (p.Priority != null ? $", priorité {p.Priority}" : "") + $"] {p.Path} — {p.OpenTasks} tâche(s) ouverte(s)" + (p.NextDue != null ? $", prochaine échéance {p.NextDue}" : "") + (!string.IsNullOrWhiteSpace(p.Repo) ? $", dépôt {p.Repo}" : "") + (!string.IsNullOrWhiteSpace(p.Summary) ? $"\n    {p.Summary}" : "")));
    }

    string ProjectCreate(JsonObject a) => Done(vault.CreateProject(S(a, "name"), S(a, "repo", false), S(a, "summary", false)), "Projet créé");

    string TaskList(JsonObject a)
    {
        var l = vault.Tasks(S(a, "under", false), B(a, "include_done", false));
        if (l.Count == 0) return "Aucune tâche.";
        return $"{l.Count} tâche(s) :\n" + string.Join("\n", l.Take(300).Select(t => $"- [{(t.Done ? "x" : " ")}] {t.Text}   ({t.Path}:{t.Line})")) + (l.Count > 300 ? "\n… (300 premières)" : "");
    }

    // ---------- boucle d'entrée-sortie ----------
    public static int Serve(Server s)
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            string r = s.Handle(line);
            if (r != null) output.WriteLine(r);
        }
        return 0;
    }
}


