/* grammaire-worker.js : la vérification grammaticale (accords, conjugaison, confusions) par Grammalecte, dans un worker de la page.
   Grammalecte (Olivier R., GPL 3, https://grammalecte.net) : les fichiers de web/third_party/grammalecte viennent de l'extension Firefox 2.3.0 ; seuls
   le moteur, ses règles et le lexique sont gardés (ni thésaurus, ni formateur de texte, ni tests). Le chargement (lexique de 4 Mo en JSON) prend une à deux
   secondes, hors du fil de la page ; les demandes arrivées avant sont servies ensuite.

   Contrat (cdx-spell.js, option `grammar`) :
     page → worker : { type: "init", options: { conj: true, … } }   { type: "parse", id, text }
     worker → page : { type: "ready" } | { type: "error", message }   { type: "result", id, errors: [{ s, e, type, msg, sugg: [...], rule }] }
   Les positions s / e sont celles du texte reçu (aucune normalisation : les paragraphes sont découpés sur les sauts de ligne et vérifiés un à un). */
"use strict";
const BASE = "/third_party/grammalecte/";
let ready = false, failed = null;
const queue = [];

// Les options par défaut : la grammaire et les verbes ; pas la typographie ni le style (apostrophes droites, espaces, majuscules… seraient du bruit dans une
// zone de saisie). Le contexte « JavaScript » de Grammalecte active aussi `md` (balises Markdown ignorées).
const DEFAULT_OPTIONS = {
  typo: false, apos: false, eepi: false, esp: false, tab: false, nbsp: false, tu: false, maj: false, minis: false, num: false, virg: false, poncfin: false, unit: false, nf: false, liga: false, mapos: false, chim: false, ocr: false,
  conf: true, loc: true, gn: true, infi: true, conj: true, ppas: true, imp: true, inte: true, vmode: true,
  bs: false, pleo: false, eleu: false, neg: false, redon1: false, redon2: false, date: false, mc: false, idrule: false, html: false, latex: false, md: true,
};

function load(options) {
  try {
    for (const f of ["graphspell/helpers.js", "graphspell/str_transform.js", "graphspell/char_player.js", "graphspell/lexgraph_fr.js", "graphspell/ibdawg.js", "graphspell/spellchecker.js", "text.js",
      "graphspell/tokenizer.js", "fr/conj.js", "fr/mfsp.js", "fr/phonet.js", "fr/cregex.js", "fr/gc_options.js", "fr/gc_functions.js", "fr/gc_rules.js", "fr/gc_rules_graph.js", "fr/gc_engine.js"]) importScripts(BASE + f);
    conj.init(helpers.loadFile(BASE + "fr/conj_data.json"));
    phonet.init(helpers.loadFile(BASE + "fr/phonet_data.json"));
    mfsp.init(helpers.loadFile(BASE + "fr/mfsp_data.json"));
    gc_engine.load("JavaScript", "aHSL", BASE + "graphspell/_dictionaries");
    if (!gc_engine.getSpellChecker() || !gc_engine.getSpellChecker().oMainDic) throw new Error("lexique non chargé");
    gc_engine.setOptions(helpers.objectToMap(Object.assign({}, DEFAULT_OPTIONS, options || {})));
    ready = true;
    postMessage({ type: "ready" });
    for (const m of queue.splice(0)) parse(m);
  } catch (e) {
    failed = String(e && e.message || e);
    postMessage({ type: "error", message: failed });
  }
}

function parse(m) {
  const errors = [];
  try {
    const text = String(m.text || "");
    let off = 0;
    for (const par of text.split("\n")) {
      if (par.trim()) {
        for (const err of gc_engine.parse(par, "FR", false, null, false)) {
          errors.push({ s: off + err.nStart, e: off + err.nEnd, type: err.sType, msg: String(err.sMessage || "").replace(/\s+/g, " ").trim(), sugg: (err.aSuggestions || []).slice(0, 8), rule: err.sRuleId });
        }
      }
      off += par.length + 1;
    }
  } catch (e) {
    postMessage({ type: "result", id: m.id, errors: [], warning: String(e && e.message || e) });
    return;
  }
  errors.sort((a, b) => a.s - b.s || a.e - b.e);
  postMessage({ type: "result", id: m.id, errors });
}

onmessage = e => {
  const m = e.data || {};
  if (m.type === "init") { if (!ready && !failed) load(m.options); else postMessage(failed ? { type: "error", message: failed } : { type: "ready" }); }
  else if (m.type === "parse") { if (ready) parse(m); else if (failed) postMessage({ type: "result", id: m.id, errors: [] }); else queue.push(m); }
};
