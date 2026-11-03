// node tools/csp-check.mjs [web]
// Chaque page de la suite porte une politique de sécurité du contenu (meta Content-Security-Policy) qui n'autorise que ses propres scripts :
// pas de script en ligne, pas d'attribut on…=, ni 'unsafe-inline' ni 'unsafe-eval' dans script-src. Une injection de HTML (un nom de fichier,
// un objet de courrier, une page mal échappée) ne peut alors rien exécuter. Code de sortie 1 au premier écart.
// Ignorés : les bibliothèques tierces (third_party, vendor), les dictionnaires, les pages d'essai (_test-*.html).
// Propre à cdx-windows (la charte, elle, est contrôlée par charte-lint.mjs, copié depuis cdx-ui).
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(process.argv[2] || 'web');
const skip = /[\\/](third_party|vendor|dict)[\\/]|[\\/]_test-[^\\/]*$/;
const files = [];
(function walk(d) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else if (!skip.test(p)) files.push(p); } })(root);

const problems = [];
const rel = p => path.relative(process.cwd(), p);
for (const f of files.filter(p => p.endsWith('.html'))) {
  const t = fs.readFileSync(f, 'utf8');
  const meta = t.match(/<meta\s+http-equiv="Content-Security-Policy"\s+content="([^"]+)"/i);
  if (!meta) { problems.push(`${rel(f)} : pas de meta Content-Security-Policy`); }
  else {
    const csp = meta[1], script = (csp.match(/(?:^|;)\s*script-src([^;]*)/) || csp.match(/(?:^|;)\s*default-src([^;]*)/) || [, ''])[1];
    if (!/'self'/.test(script)) problems.push(`${rel(f)} : script-src sans 'self'`);
    if (/'unsafe-inline'|'unsafe-eval'|\*/.test(script)) problems.push(`${rel(f)} : script-src trop large (${script.trim()})`);
    if (!/object-src\s+'none'/.test(csp)) problems.push(`${rel(f)} : object-src 'none' manquant`);
    if (!/base-uri\s+'none'/.test(csp)) problems.push(`${rel(f)} : base-uri 'none' manquant`);
  }
  for (const m of t.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>/gi)) problems.push(`${rel(f)}:${t.slice(0, m.index).split('\n').length} : script en ligne`);
  for (const m of t.matchAll(/<[a-z][^>]*\son[a-z]+\s*=/gi)) problems.push(`${rel(f)}:${t.slice(0, m.index).split('\n').length} : attribut on…= (${m[0].slice(0, 60)})`);
}
// les modèles de HTML écrits par les scripts des pages : pas d'attribut on…= non plus (la CSP les bloquerait sans bruit)
for (const f of files.filter(p => p.endsWith('.js'))) {
  const t = fs.readFileSync(f, 'utf8');
  for (const m of t.matchAll(/<[a-z][^<>`]*\son(?:click|error|load|input|change|key\w+|mouse\w+|submit|focus|blur)\s*=\s*\\?["']/gi)) problems.push(`${rel(f)}:${t.slice(0, m.index).split('\n').length} : attribut on…= dans un modèle HTML`);
}
for (const p of problems) console.log(p);
console.log(`${files.filter(p => p.endsWith('.html')).length} page(s), ${problems.length} écart(s)`);
process.exit(problems.length ? 1 : 0);
