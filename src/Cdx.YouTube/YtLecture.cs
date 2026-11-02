using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cdx;

// ============================================================================================================================================
// La couche de lecture YouTube : LE SEUL ENDROIT qui connaît la forme des données de YouTube (ytInitialData, réponses youtubei/v1 browse, search, next).
// Quand YouTube change, c'est ce fichier qu'on corrige ; le reste du programme et l'interface ne voient que le modèle ci-dessous.
//
// Formats connus (02/11/2026) :
//   videoRenderer, compactVideoRenderer, gridVideoRenderer   ancien format, encore utilisé par la recherche (title.runs, ownerText, lengthText…)
//   lockupViewModel                                          nouveau format de l'accueil et des suggestions (metadata.lockupMetadataViewModel…)
//   shortsLockupViewModel, reelItemRenderer                  les Shorts
//   videoPrimaryInfoRenderer, videoSecondaryInfoRenderer     la fiche d'une vidéo (réponse « next » ou ytInitialData d'une page /watch)
//   lockupViewModel de type PLAYLIST, PODCAST, SHOW          une liste de lecture (identifiant RD… : un mix) ; playlistRenderer, radioRenderer (ancien format)
//   pageHeaderViewModel + tabRenderer                         l'en-tête et les onglets d'une chaîne, l'en-tête d'une liste
//   playlist.playlist (playlistPanelVideoRenderer)             la liste en cours de lecture, dans la fiche
// Les publicités présentes dans les données (adSlotRenderer, feedAdMetadataViewModel…) sont retirées ici : uBOL les masque dans la page, mais elles
// restent dans le JSON. Tout format de vidéo inconnu est signalé au journal de veille (Veille.cs).
// ============================================================================================================================================

// Genre : video, short, direct, liste (liste de lecture, podcast, émission), mix. Pour une liste ou un mix : ListeId est la liste, Id sa première vidéo (peut manquer),
// Duree le nombre de vidéos (« 124 vidéos »). Pour une vidéo d'une liste : ListeId et Index la situent dans la liste (lecture dans l'ordre de la liste).
public sealed record Video(string Id, string Titre, string Chaine, string ChaineId, string Duree, string Miniature, string Vues, string Date, string Genre, string ListeId = null, int? Index = null);

public sealed class Onglet { public string Titre, Url, Params; public bool Actif; }

public sealed class Chaine
{
    public string Id, Nom, Avatar, Pseudo, Abonnes, NbVideos, Description;
    public List<Onglet> Onglets = new();
}

public sealed class EnteteListe { public string Id, Titre, Auteur, AuteurId, Infos, Description, PremierId; }

public sealed class ListeEnCours
{
    public string Id, Titre, Auteur, AuteurId;
    public int Index, Total;
    public List<Video> Videos = new();
}

public sealed class Liste
{
    public List<Video> Videos = new();
    public bool Suite;          // il y a une suite (YouTube la chargera au défilement)
    public bool EstSuite;       // cette réponse est elle-même une suite (à ajouter à la liste précédente)
    public int PubsFiltrees;
    public List<string> Messages = new();   // textes affichés par YouTube quand il n'y a rien (accueil sans historique…)
    public int ChainesProposees;            // abonnements d'un compte qui ne suit aucune chaîne : YouTube propose des chaînes (gridChannelRenderer)
}

public sealed class Fiche
{
    public string Id, Titre, Chaine, ChaineId, Avatar, Abonnes, Vues, Date, Description, Miniature;
    public List<Video> Suggestions = new();
    public ListeEnCours Liste;   // la vidéo est lue dans une liste (playlist.playlist de la réponse) ; absent pour un mix
    public int PubsFiltrees;
}

public static class YtLecture
{
    // ---------- listes : accueil, recherche, suggestions ----------

    static readonly HashSet<string> Classiques = new() { "videoRenderer", "compactVideoRenderer", "gridVideoRenderer", "playlistVideoRenderer", "reelItemRenderer" };

    // Les conteneurs de publicité : on ne descend pas dedans. (Les noms changent : un « renderer » dont le nom contient Ad/Promoted est aussi écarté.)
    static readonly HashSet<string> Pubs = new() { "adSlotRenderer", "inFeedAdLayoutRenderer", "promotedSparklesWebRenderer", "promotedVideoRenderer", "compactPromotedVideoRenderer",
        "displayAdRenderer", "searchPyvRenderer", "adSlotMetadata", "bannerPromoRenderer", "statementBannerRenderer", "primetimePromoRenderer", "brandVideoShelfRenderer", "brandVideoSingletonRenderer" };
    static readonly Regex NomDePub = new("^(ad|ads|promoted)[A-Z]|(Ad|Promoted|Promo)(Slot|Layout|Renderer|ViewModel|Metadata)", RegexOptions.Compiled);   // adSlotRenderer, adsControlFlow…, feedAdMetadataViewModel ; pas addToPlaylistCommand ni adaptiveFormats

    // Ce qui n'est pas une vidéo et n'en contient pas : on ne le signale pas comme format inconnu.
    static readonly HashSet<string> Ignores = new() { "menuRenderer", "thumbnailOverlayToggleButtonRenderer", "continuationItemRenderer", "channelRenderer",
        "lockupMetadataViewModel", "channelVideoPlayerRenderer", "playlistPanelVideoRenderer", "playlistSidebarRenderer", "playlistSidebarPrimaryInfoRenderer",
        "playlistSidebarSecondaryInfoRenderer", "listViewModel", "sheetViewModel", "buttonViewModel", "toggleButtonViewModel", "menuServiceItemRenderer", "chipCloudChipRenderer", "feedFilterChipBarRenderer" };

    // Une réponse qui prolonge une liste (défilement) : appendContinuationItemsAction sous onResponseReceivedActions / Commands / Endpoints.
    public static bool EstSuite(JsonElement r)
    {
        foreach (var k in new[] { "onResponseReceivedActions", "onResponseReceivedCommands", "onResponseReceivedEndpoints" })
            if (r.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array && a.EnumerateArray().Any(x => x.TryGetProperty("appendContinuationItemsAction", out _)))
                return true;
        return false;
    }

    public static Liste LireListe(JsonElement racine)
    {
        var l = new Liste();
        l.EstSuite = EstSuite(racine);
        var vus = new HashSet<string>();
        Parcourir(racine, null, l, vus, 0);
        if (l.Videos.Count == 0 && l.ChainesProposees > 0)
            l.Messages.Add($"Ce compte ne suit aucune chaîne : YouTube propose {l.ChainesProposees} chaînes à suivre.");
        return l;
    }

    // Parcourt l'arbre dans l'ordre du document. `parent` : la clé sous laquelle se trouve l'élément (pour reconnaître les éléments d'une liste de contenus).
    static int Parcourir(JsonElement e, string parent, Liste l, HashSet<string> vus, int profondeur)
    {
        if (profondeur > 60) return 0;
        int trouves = 0;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) trouves += Parcourir(x, parent, l, vus, profondeur + 1); return trouves; }
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var p in e.EnumerateObject())
        {
            string k = p.Name;
            if (k is "continuationItemRenderer" or "continuationItemViewModel" or "continuationEndpoint") { l.Suite = true; continue; }
            if (k == "gridChannelRenderer") { l.ChainesProposees++; continue; }
            if (Pubs.Contains(k) || NomDePub.IsMatch(k)) { l.PubsFiltrees++; Veille.Noter("pub", "conteneur de publicité dans les données, filtré : " + k); continue; }
            if (k is "feedNudgeRenderer" or "messageRenderer" or "backgroundPromoRenderer")
            {
                var t = new[] { "title", "subtitle", "text", "bodyText" }.Select(c => Texte(p.Value, c)).Where(x => !string.IsNullOrWhiteSpace(x));
                l.Messages.Add(string.Join(" ", t)); continue;
            }
            Video v = null; bool connu = true;
            if (Classiques.Contains(k)) v = Classique(p.Value, k);
            else if (k == "lockupViewModel")
            {
                if (Chemin(p.Value, "metadata", "feedAdMetadataViewModel") != null) { l.PubsFiltrees++; Veille.Noter("pub", "lockupViewModel publicitaire (feedAdMetadataViewModel), filtré"); continue; }
                v = Lockup(p.Value);
            }
            else if (k == "shortsLockupViewModel") v = Short(p.Value);
            else if (k is "playlistRenderer" or "compactPlaylistRenderer" or "gridPlaylistRenderer") v = AncienneListe(p.Value, false);
            else if (k is "radioRenderer" or "compactRadioRenderer") v = AncienneListe(p.Value, true);
            else connu = false;

            if (connu)
            {
                // une liste se reconnaît à sa liste, une vidéo à son identifiant (la même vidéo peut être à la fois seule et première d'une liste)
                string cle = v == null ? null : v.Genre is "liste" or "mix" ? "L:" + v.ListeId : v.Id;
                if (cle != null && cle != "L:" && vus.Add(cle)) { l.Videos.Add(v); trouves++; }
                continue;
            }
            int sous = Parcourir(p.Value, k, l, vus, profondeur + 1);
            trouves += sous;
            // Un élément d'une liste de contenus, d'un genre inconnu, qui contient une vidéo sans qu'on ait su la lire : YouTube a sans doute changé de format.
            if (sous == 0 && (k.EndsWith("Renderer") || k.EndsWith("ViewModel")) && !Ignores.Contains(k) && parent is "content" or "contents" or "items" or "results" or "continuationItems"
                && Contient(p.Value, "videoId", 8))
                Veille.Noter("format", $"élément inconnu contenant une vidéo : {k} (sous « {parent} »)");
        }
        return trouves;
    }

    static Video Classique(JsonElement r, string genre)
    {
        string id = Str(r, "videoId"); if (id == null) return null;
        string titre = Texte(r, "title") ?? Texte(r, "headline");
        if (titre == null) { Veille.Noter("format", genre + " sans titre lisible"); return null; }
        string chaine = Texte(r, "ownerText") ?? Texte(r, "longBylineText") ?? Texte(r, "shortBylineText");
        string chaineId = null;
        foreach (var c in new[] { "ownerText", "longBylineText", "shortBylineText" })
            if (Chemin(r, c, "runs") is { ValueKind: JsonValueKind.Array } runs && runs.GetArrayLength() > 0 && Chemin(runs[0], "navigationEndpoint", "browseEndpoint") is { } be) { chaineId = Str(be, "browseId"); break; }
        string duree = Texte(r, "lengthText"), g = genre == "reelItemRenderer" ? "short" : "video";
        if (r.TryGetProperty("thumbnailOverlays", out var ov) && ov.ValueKind == JsonValueKind.Array)
            foreach (var o in ov.EnumerateArray())
                if (o.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out var ts))
                {
                    duree ??= Texte(ts, "text");
                    string style = Str(ts, "style");
                    if (style == "LIVE") g = "direct"; else if (style == "SHORTS") g = "short";
                }
        if (r.TryGetProperty("badges", out var bd) && bd.ToString().Contains("BADGE_STYLE_TYPE_LIVE_NOW")) g = "direct";
        string vues = Texte(r, "viewCountText") ?? Texte(r, "shortViewCountText");
        var we = Chemin(r, "navigationEndpoint", "watchEndpoint");
        return new Video(id, titre, chaine, chaineId, duree, Miniature(Chemin(r, "thumbnail", "thumbnails")), vues, Texte(r, "publishedTimeText"), g,
            we == null ? null : Str(we.Value, "playlistId"), we != null && we.Value.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixn) ? ixn : null);
    }

    // playlistRenderer / radioRenderer (ancien format, résultats de recherche) : une liste de lecture, ou un mix
    static Video AncienneListe(JsonElement r, bool mix)
    {
        string id = Str(r, "playlistId"), titre = Texte(r, "title");
        if (id == null || titre == null) { Veille.Noter("format", (mix ? "radioRenderer" : "playlistRenderer") + " sans playlistId ou sans titre"); return null; }
        string chaine = Texte(r, "shortBylineText") ?? Texte(r, "longBylineText");
        string chaineId = Chercher(Chemin(r, "shortBylineText") ?? default, "browseEndpoint", 6) is { } be ? Str(be, "browseId") : null;
        string nombre = Texte(r, "videoCountText") ?? (Str(r, "videoCount") is { } n ? n + " vidéos" : null);
        var thumbs = Chemin(r, "thumbnails") is { ValueKind: JsonValueKind.Array } ta && ta.GetArrayLength() > 0 ? Chemin(ta[0], "thumbnails") : Chemin(r, "thumbnail", "thumbnails");
        string premier = Str(Chemin(r, "navigationEndpoint", "watchEndpoint") ?? default, "videoId");
        return new Video(premier, titre, mix ? null : chaine, chaineId, mix ? "Mix" : nombre, Miniature(thumbs), null, null, mix || id.StartsWith("RD") ? "mix" : "liste", id);
    }

    static Video Lockup(JsonElement r)
    {
        string type = Str(r, "contentType");
        bool liste = type is "LOCKUP_CONTENT_TYPE_PLAYLIST" or "LOCKUP_CONTENT_TYPE_PODCAST" or "LOCKUP_CONTENT_TYPE_SHOW" or "LOCKUP_CONTENT_TYPE_COURSE";
        bool estShort = type == "LOCKUP_CONTENT_TYPE_SHORT";   // vu le 02/11/2026 dans les suggestions
        if (type != null && type != "LOCKUP_CONTENT_TYPE_VIDEO" && !liste && !estShort) { Veille.Noter("format", "lockupViewModel d'un genre inconnu, ignoré : " + type); return null; }
        string id = Str(r, "contentId");
        var m = Chemin(r, "metadata", "lockupMetadataViewModel");
        if (id == null || m == null) { Veille.Noter("format", "lockupViewModel sans contentId ou sans lockupMetadataViewModel"); return null; }
        // le lien de l'élément : pour une vidéo d'une liste, sa liste et sa place ; pour une liste, sa première vidéo
        var we = Chemin(r, "rendererContext", "commandContext", "onTap", "innertubeCommand", "watchEndpoint");
        if (liste) return LockupListe(r, m.Value, id, we);
        string titre = Texte(m.Value, "title");
        // Les métadonnées, en lignes de « parts ». Deux formes vues le 02/11/2026 :
        //   une ligne    [chaîne, « 552 k » (icône PLAY_ARROW), « il y a 2 j »]                         (accueil)
        //   deux lignes  [chaîne, « et France TV » (collaboration)] puis [« 253 k vues », « il y a 1 j »]   (suggestions)
        // La première part de la première ligne est la chaîne ; les vues portent l'icône PLAY_ARROW ou le mot « vue(s) » ; la date est la part restante.
        var lignes = new List<List<JsonElement>>();
        if (Chemin(m.Value, "metadata", "contentMetadataViewModel", "metadataRows") is { ValueKind: JsonValueKind.Array } rows)
            foreach (var row in rows.EnumerateArray())
                if (Chemin(row, "metadataParts") is { ValueKind: JsonValueKind.Array } ps && ps.GetArrayLength() > 0) lignes.Add(ps.EnumerateArray().ToList());
        string chaine = null, chaineId = null, vues = null, date = null;
        // Sur la page d'une chaîne, YouTube n'écrit pas son nom : la première part est alors déjà les vues (« 4,5 k », icône PLAY_ARROW) ou la date.
        if (lignes.Count > 0 && (EstVues(lignes[0][0]) || EstDate(Texte(lignes[0][0], "text"))))
            lignes.Insert(0, new List<JsonElement>());
        if (lignes.Count > 0 && lignes[0].Count == 0)
        {
            foreach (var x in lignes.Skip(1).SelectMany(x => x))
            {
                string t = Texte(x, "text"); if (string.IsNullOrWhiteSpace(t)) continue;
                if (vues == null && EstVues(x)) vues = t; else date ??= t;
            }
        }
        else if (lignes.Count > 0)
        {
            var p0 = lignes[0][0];
            chaine = Texte(p0, "text");
            chaineId = Chemin(p0, "text", "commandRuns") is { ValueKind: JsonValueKind.Array } cr && cr.GetArrayLength() > 0
                ? Str(Chemin(cr[0], "onTap", "innertubeCommand", "browseEndpoint") ?? default, "browseId") : null;
            // le reste de la ligne de la chaîne : des vues et une date (une ligne), ou des co-auteurs (plusieurs lignes)
            var autres = lignes.Count == 1 ? lignes[0].Skip(1) : lignes.Skip(1).SelectMany(x => x);
            if (lignes.Count > 1)
                foreach (var x in lignes[0].Skip(1)) if (Texte(x, "text") is { Length: > 0 } t) chaine += " " + t;
            foreach (var x in autres)
            {
                string t = Texte(x, "text"); if (string.IsNullOrWhiteSpace(t)) continue;
                bool icone = Str(Chemin(x, "leadingIcon") ?? default, "name")?.StartsWith("PLAY_ARROW") == true;
                if (vues == null && (icone || Regex.IsMatch(t, @"\bvues?\b|\bviews?\b|spectateur|watching", RegexOptions.IgnoreCase))) vues = t;
                else date ??= t;
            }
        }
        chaineId ??= Str(Chemin(m.Value, "image", "decoratedAvatarViewModel", "rendererContext", "commandContext", "onTap", "innertubeCommand", "browseEndpoint") ?? default, "browseId");
        string duree = null, g = estShort ? "short" : "video";
        var vm = Chemin(r, "contentImage", "thumbnailViewModel");
        if (vm != null && Chemin(vm.Value, "overlays") is { ValueKind: JsonValueKind.Array } ov)
            foreach (var o in ov.EnumerateArray())
            {
                // deux formes vues : thumbnailBottomOverlayViewModel.badges[] et thumbnailOverlayBadgeViewModel.thumbnailBadges[]
                var badges = Chemin(o, "thumbnailBottomOverlayViewModel", "badges") ?? Chemin(o, "thumbnailOverlayBadgeViewModel", "thumbnailBadges");
                if (badges is not { ValueKind: JsonValueKind.Array } b) continue;
                foreach (var x in b.EnumerateArray())
                {
                    var bv = Chemin(x, "thumbnailBadgeViewModel"); if (bv == null) continue;
                    string t = Str(bv.Value, "text"); if (t == null) continue;
                    if (Str(bv.Value, "badgeStyle") == "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" || Regex.IsMatch(t, "^(EN DIRECT|LIVE)$", RegexOptions.IgnoreCase)) g = "direct";
                    else if (Regex.IsMatch(t, @"^\d+(:\d\d)+$")) duree = t;
                }
            }
        if (titre == null) { Veille.Noter("format", "lockupViewModel sans titre"); return null; }
        return new Video(id, titre, chaine, chaineId, duree, vm == null ? null : Miniature(Chemin(vm.Value, "image", "sources")), vues, date, g,
            we == null ? null : Str(we.Value, "playlistId"), we != null && we.Value.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixn) ? ixn : null);
    }

    static bool EstVues(JsonElement part)
    {
        string t = Texte(part, "text") ?? "";
        return Str(Chemin(part, "leadingIcon") ?? default, "name")?.StartsWith("PLAY_ARROW") == true
            || Regex.IsMatch(t, @"\bvues?\b|\bviews?\b|spectateur|watching", RegexOptions.IgnoreCase)
            || Regex.IsMatch(t, @"^[\d\s,.\u202f]+\s?(k|M|Md|B)?$");
    }
    static bool EstDate(string t) => t != null && Regex.IsMatch(t, @"^(il y a|Diffusé|Streamed|Premiered|Première|Planifié|\d+ (an|mois|sem|jour|h|min))|ago$", RegexOptions.IgnoreCase);

    // Une liste de lecture (ou un podcast, une émission, un mix) au format lockupViewModel : miniature dans collectionThumbnailViewModel, nombre de vidéos dans
    // le badge (« 124 vidéos »), chaîne dans la première part si elle mène à une chaîne (sur l'onglet Playlists d'une chaîne, la première part est « Playlist »).
    static Video LockupListe(JsonElement r, JsonElement m, string id, JsonElement? we)
    {
        string titre = Texte(m, "title");
        if (titre == null) { Veille.Noter("format", "lockupViewModel de liste sans titre"); return null; }
        string chaine = null, chaineId = null;
        if (Chemin(m, "metadata", "contentMetadataViewModel", "metadataRows") is { ValueKind: JsonValueKind.Array } rows && rows.GetArrayLength() > 0
            && Chemin(rows[0], "metadataParts") is { ValueKind: JsonValueKind.Array } ps && ps.GetArrayLength() > 0)
        {
            var be = Chercher(ps[0], "browseEndpoint", 8);
            string t0 = Texte(ps[0], "text");
            // sur l'onglet Playlists d'une chaîne, la seule part est le genre (« Playlist »), avec un lien vers la chaîne : ce n'est pas un nom de chaîne
            if (be != null && Str(be.Value, "browseId") is { } bid && bid.StartsWith("UC") && !Regex.IsMatch(t0 ?? "", "^(Playlist|Podcast|Émission|Show|Mix|Liste de lecture)$", RegexOptions.IgnoreCase))
            { chaine = t0; chaineId = bid; }
        }
        var tv = Chemin(r, "contentImage", "collectionThumbnailViewModel", "primaryThumbnail", "thumbnailViewModel") ?? Chemin(r, "contentImage", "thumbnailViewModel");
        string nombre = null;
        if (tv != null && Chercher(tv.Value, "thumbnailBadgeViewModel", 8) is { } bv) nombre = Str(bv, "text");
        bool mix = id.StartsWith("RD");
        return new Video(we == null ? null : Str(we.Value, "videoId"), titre, chaine, chaineId, nombre ?? (mix ? "Mix" : null), tv == null ? null : Miniature(Chemin(tv.Value, "image", "sources")),
            null, null, mix ? "mix" : "liste", id);
    }

    // ---------- chaîne : en-tête et onglets (réponse « browse » d'une chaîne) ----------

    public static Chaine LireChaine(JsonElement racine, string id)
    {
        var c = new Chaine { Id = id };
        var h = Chemin(racine, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
        if (h != null)
        {
            c.Nom = Texte(Chemin(h.Value, "title", "dynamicTextViewModel") ?? default, "text");
            c.Avatar = Miniature(Chemin(h.Value, "image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources"), 88);
            var parts = new List<string>();
            if (Chemin(h.Value, "metadata", "contentMetadataViewModel", "metadataRows") is { ValueKind: JsonValueKind.Array } rows)
                foreach (var row in rows.EnumerateArray())
                    if (Chemin(row, "metadataParts") is { ValueKind: JsonValueKind.Array } ps)
                        foreach (var x in ps.EnumerateArray()) if (Texte(x, "text") is { Length: > 0 } t) parts.Add(t);
            c.Pseudo = parts.FirstOrDefault(x => x.StartsWith("@"));
            c.Abonnes = parts.FirstOrDefault(x => x.Contains("abonné") || x.Contains("subscriber"));
            c.NbVideos = parts.FirstOrDefault(x => x.Contains("vidéo") || x.Contains("video"));
            c.Description = Texte(Chemin(h.Value, "description", "descriptionPreviewViewModel") ?? default, "description");
        }
        // repli (ancien en-tête, ou description complète) : metadata.channelMetadataRenderer
        if (Chemin(racine, "metadata", "channelMetadataRenderer") is { } md)
        {
            c.Nom ??= Str(md, "title");
            if (Str(md, "description") is { Length: > 0 } d) c.Description = d;
            c.Avatar ??= Miniature(Chemin(md, "avatar", "thumbnails"), 88);
            c.Id ??= Str(md, "externalId");
        }
        if (c.Nom == null) Veille.Noter("format", "chaîne : en-tête illisible (pageHeaderViewModel, channelMetadataRenderer)");
        if (Chemin(racine, "contents", "twoColumnBrowseResultsRenderer", "tabs") is { ValueKind: JsonValueKind.Array } tabs)
            foreach (var t in tabs.EnumerateArray())
            {
                if (!t.TryGetProperty("tabRenderer", out var tr)) continue;
                var be = Chemin(tr, "endpoint", "browseEndpoint");
                string url = Str(Chemin(tr, "endpoint", "commandMetadata", "webCommandMetadata") ?? default, "url");
                if (be == null || url == null) continue;
                c.Onglets.Add(new Onglet { Titre = Str(tr, "title"), Url = url, Params = Str(be.Value, "params"), Actif = tr.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.True });
            }
        else Veille.Noter("format", "chaîne : pas d'onglets (contents.twoColumnBrowseResultsRenderer.tabs)");
        return c;
    }

    // ---------- page d'une liste de lecture : son en-tête ----------

    public static EnteteListe LireEnteteListe(JsonElement racine, string id)
    {
        var e = new EnteteListe { Id = id };
        var h = Chemin(racine, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
        if (h != null)
        {
            e.Titre = Texte(Chemin(h.Value, "title", "dynamicTextViewModel") ?? default, "text");
            var infos = new List<string>();
            if (Chemin(h.Value, "metadata", "contentMetadataViewModel", "metadataRows") is { ValueKind: JsonValueKind.Array } rows)
                foreach (var row in rows.EnumerateArray())
                    if (Chemin(row, "metadataParts") is { ValueKind: JsonValueKind.Array } ps)
                        foreach (var x in ps.EnumerateArray())
                        {
                            if (Chemin(x, "avatarStack", "avatarStackViewModel") is { } av) { e.Auteur = Regex.Replace(Texte(av, "text") ?? "", "^de |^by ", ""); continue; }
                            if (Texte(x, "text") is { Length: > 0 } t && t != "Playlist") infos.Add(t);
                        }
            e.Infos = string.Join(" · ", infos);
            e.Description = Texte(Chemin(h.Value, "description", "descriptionPreviewViewModel") ?? default, "description");
        }
        if (Chemin(racine, "metadata", "playlistMetadataRenderer") is { } md) { e.Titre ??= Str(md, "title"); if (Str(md, "description") is { Length: > 0 } d) e.Description = d; }
        // l'auteur et son identifiant : le propriétaire indiqué dans la colonne de la liste
        if (Chercher(racine, "videoOwnerRenderer", 20) is { } ow)
        {
            e.Auteur ??= Texte(ow, "title");
            e.AuteurId = Str(Chemin(ow, "navigationEndpoint", "browseEndpoint") ?? default, "browseId");
        }
        if (e.Titre == null) Veille.Noter("format", "liste : en-tête illisible (pageHeaderViewModel, playlistMetadataRenderer)");
        return e;
    }

    static Video Short(JsonElement r)
    {
        string id = Str(Chemin(r, "onTap", "innertubeCommand", "reelWatchEndpoint") ?? default, "videoId");
        string titre = Str(Chemin(r, "overlayMetadata", "primaryText") ?? default, "content");
        if (id == null || titre == null) { Veille.Noter("format", "shortsLockupViewModel sans identifiant ou sans titre"); return null; }
        // miniature verticale : thumbnailViewModel.thumbnailViewModel.image.sources (02/11/2026), auparavant thumbnail.sources
        var img = Chemin(r, "thumbnailViewModel", "thumbnailViewModel", "image", "sources") ?? Chemin(r, "thumbnail", "sources");
        if (img == null) Veille.Noter("format", "shortsLockupViewModel sans miniature reconnue");
        return new Video(id, titre, null, null, null, Miniature(img, 300), Str(Chemin(r, "overlayMetadata", "secondaryText") ?? default, "content"), null, "short");
    }

    // ---------- fiche d'une vidéo : réponse « next », « get_watch », ou ytInitialData d'une page /watch ----------

    // Ramène une réponse à la forme « next » quand c'en est une variante : get_watch = [ { playerResponse }, { watchNextResponse } ] (02/11/2026) ;
    // les données de ytd-page-manager peuvent avoir la même forme. Toute autre réponse est rendue telle quelle.
    public static JsonElement Normaliser(JsonElement r)
    {
        if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in r.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("watchNextResponse", out var w)) return w;
            return r;
        }
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("watchNextResponse", out var w2)) return w2;
        return r;
    }

    public static string IdDeChaine(JsonElement r) => Str(Chemin(r, "metadata", "channelMetadataRenderer") ?? default, "externalId");

    // Le compte connecté : réponse account/account_menu (menu de l'avatar), activeAccountHeaderRenderer.
    public static string LireCompte(JsonElement r)
    {
        if (Chercher(r, "activeAccountHeaderRenderer", 20) is not { } h) { Veille.Noter("format", "compte : activeAccountHeaderRenderer absent du menu du compte"); return null; }
        string nom = Texte(h, "accountName"), pseudo = Texte(h, "channelHandle"), mail = Texte(h, "email");
        return nom == null ? null : nom + (pseudo != null ? " (" + pseudo + ")" : mail != null ? " (" + mail + ")" : "");
    }

    public static string IdDeFiche(JsonElement r) => Str(Chemin(r, "currentVideoEndpoint", "watchEndpoint") ?? default, "videoId");

    public static Fiche LireFiche(JsonElement racine)
    {
        var f = new Fiche { Id = Str(Chemin(racine, "currentVideoEndpoint", "watchEndpoint") ?? default, "videoId") };
        var two = Chemin(racine, "contents", "twoColumnWatchNextResults");
        if (two == null) { Veille.Noter("format", "fiche : pas de contents.twoColumnWatchNextResults"); return f; }
        if (Chemin(two.Value, "results", "results", "contents") is { ValueKind: JsonValueKind.Array } cs)
            foreach (var c in cs.EnumerateArray())
            {
                if (c.TryGetProperty("videoPrimaryInfoRenderer", out var pr))
                {
                    f.Titre = Texte(pr, "title");
                    var vc = Chemin(pr, "viewCount", "videoViewCountRenderer");
                    if (vc != null) f.Vues = Texte(vc.Value, "viewCount") ?? Texte(vc.Value, "shortViewCount");
                    f.Date = Texte(pr, "dateText") ?? Texte(pr, "relativeDateText");
                }
                else if (c.TryGetProperty("videoSecondaryInfoRenderer", out var sr))
                {
                    var ow = Chemin(sr, "owner", "videoOwnerRenderer");
                    if (ow != null)
                    {
                        // une chaîne : title, subscriberCountText, thumbnail, navigationEndpoint.browseEndpoint ;
                        // une collaboration (02/11/2026) : attributedTitle « RMC DECOUVERTE et RMC+ », avatarStack, et la liste des chaînes dans un dialogue
                        f.Chaine = Texte(ow.Value, "title") ?? Texte(ow.Value, "attributedTitle");
                        f.Abonnes = Texte(ow.Value, "subscriberCountText");
                        f.Avatar = Miniature(Chemin(ow.Value, "thumbnail", "thumbnails"), 48);
                        if (f.Avatar == null && Chemin(ow.Value, "avatarStack", "avatarStackViewModel", "avatars") is { ValueKind: JsonValueKind.Array } av && av.GetArrayLength() > 0)
                            f.Avatar = Miniature(Chemin(av[0], "avatarViewModel", "image", "sources"), 48);
                        f.ChaineId = Str(Chemin(ow.Value, "navigationEndpoint", "browseEndpoint") ?? default, "browseId")
                            ?? (Chercher(ow.Value, "browseEndpoint", 25) is { } be ? Str(be, "browseId") : null);
                    }
                    f.Description = Texte(sr, "attributedDescription") ?? Texte(sr, "description");
                }
            }
        // la description complète est parfois seulement dans le panneau « Description »
        if (string.IsNullOrEmpty(f.Description) && Chercher(racine, "expandableVideoDescriptionBodyRenderer", 30) is { } ed)
            f.Description = Texte(ed, "attributedDescriptionBodyText") ?? Texte(ed, "descriptionBodyText");
        if (f.Titre == null) Veille.Noter("format", "fiche : videoPrimaryInfoRenderer absent ou sans titre");
        if (f.Chaine == null) Veille.Noter("format", "fiche : chaîne illisible (videoOwnerRenderer absent, ou sans title ni attributedTitle)");
        if (f.Id != null) f.Miniature = $"https://i.ytimg.com/vi/{f.Id}/hqdefault.jpg";
        // lue dans une liste : le panneau de la liste (pas pour un mix, que YouTube enchaîne sans panneau)
        if (Chemin(two.Value, "playlist", "playlist") is { } pl)
        {
            var le = new ListeEnCours
            {
                Id = Str(pl, "playlistId"), Titre = Texte(pl, "title"), Auteur = Texte(pl, "ownerName") ?? Texte(pl, "shortBylineText"),
                Index = pl.TryGetProperty("currentIndex", out var ci) && ci.TryGetInt32(out var cin) ? cin : 0,
                Total = pl.TryGetProperty("totalVideos", out var tv) && tv.TryGetInt32(out var tvn) ? tvn : 0,
            };
            le.AuteurId = Chercher(Chemin(pl, "shortBylineText") ?? default, "browseEndpoint", 6) is { } be ? Str(be, "browseId") : null;
            if (Chemin(pl, "contents") is { ValueKind: JsonValueKind.Array } cs2)
                foreach (var x in cs2.EnumerateArray())
                {
                    if (!x.TryGetProperty("playlistPanelVideoRenderer", out var pv) || Str(pv, "videoId") is not { } vid) continue;
                    var we = Chemin(pv, "navigationEndpoint", "watchEndpoint");
                    int? ix = we != null && we.Value.TryGetProperty("index", out var i2) && i2.TryGetInt32(out var i2n) ? i2n : null;
                    le.Videos.Add(new Video(vid, Texte(pv, "title"), Texte(pv, "longBylineText") ?? Texte(pv, "shortBylineText"), null, Texte(pv, "lengthText"),
                        Miniature(Chemin(pv, "thumbnail", "thumbnails"), 160), null, null, "video", le.Id, ix));
                }
            if (le.Id != null) f.Liste = le;
        }
        var sec = Chemin(two.Value, "secondaryResults", "secondaryResults");
        if (sec != null)
        {
            var l = LireListe(sec.Value);
            f.Suggestions = l.Videos.Where(v => v.Id != f.Id).ToList(); f.PubsFiltrees = l.PubsFiltrees;
        }
        else Veille.Noter("format", "fiche : pas de secondaryResults (suggestions)");
        return f;
    }

    // ---------- aides ----------

    // Une miniature de taille raisonnable pour une grille : la plus petite d'au moins `largeur` px, sinon la plus grande.
    static string Miniature(JsonElement? sources, int largeur = 320)
    {
        if (sources is not { ValueKind: JsonValueKind.Array } s || s.GetArrayLength() == 0) return null;
        var l = s.EnumerateArray().Select(x => (url: Str(x, "url"), w: x.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : 0)).Where(x => x.url != null).ToList();
        if (l.Count == 0) return null;
        var choix = l.Where(x => x.w >= largeur).OrderBy(x => x.w).FirstOrDefault();
        string u = (choix.url ?? l.OrderByDescending(x => x.w).First().url);
        return u.StartsWith("//") ? "https:" + u : u;
    }

    static JsonElement? Chemin(JsonElement e, params string[] cles)
    {
        foreach (var c in cles) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(c, out e)) return null; }
        return e;
    }

    static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Un texte YouTube : chaîne, {simpleText}, {runs:[{text}]} ou {content}.
    static string Texte(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind != JsonValueKind.Object) return null;
        if (Str(t, "simpleText") is { } s) return s;
        if (Str(t, "content") is { } c) return c;
        if (t.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array) return string.Concat(runs.EnumerateArray().Select(r => Str(r, "text")));
        return null;
    }

    static bool Contient(JsonElement e, string cle, int profondeur)
    {
        if (profondeur < 0) return false;
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().Any(x => Contient(x, cle, profondeur - 1));
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject()) if (p.Name == cle || Contient(p.Value, cle, profondeur - 1)) return true;
        return false;
    }

    static JsonElement? Chercher(JsonElement e, string cle, int profondeur)
    {
        if (profondeur < 0) return null;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) if (Chercher(x, cle, profondeur - 1) is { } r) return r; return null; }
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject()) { if (p.Name == cle) return p.Value; if (Chercher(p.Value, cle, profondeur - 1) is { } r) return r; }
        return null;
    }
}
