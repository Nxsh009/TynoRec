// TynoRec : Cloudflare Worker "tynorec-sign"
// Rôle : vérifier que l'utilisateur est connecté (compte Supabase), puis lui donner une URL signée
// valable 15 min pour envoyer / télécharger / supprimer SES clips dans R2 (dossier <user_id>/).
// Les vidéos ne passent pas par le Worker : l'app parle directement à R2 (aucune limite de taille côté Worker).
//
// Variables à définir dans Cloudflare > Workers > tynorec-sign > Settings > Variables and Secrets :
//   SUPABASE_URL, SUPABASE_ANON_KEY, R2_ACCOUNT_ID, R2_BUCKET            (texte)
//   R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY                               (secrets)
//   QUOTA_MB                                                             (texte, optionnel, 1500 par défaut : espace max par compte)
//   LIMITER                                                              (optionnel : liaison "Rate Limiting" Cloudflare ; sinon compteur local)

const enc = new TextEncoder();
const hex = (b) => [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
const uri = (s) => encodeURIComponent(s).replace(/[!'()*]/g, (c) => "%" + c.charCodeAt(0).toString(16).toUpperCase());

async function hmac(key, data) {
  const k = await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return new Uint8Array(await crypto.subtle.sign("HMAC", k, enc.encode(data)));
}
async function sha256hex(s) {
  return hex(new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(s))));
}

// URL signée AWS SigV4 (compatible R2).
//  - key vide  : opération sur le bucket (liste des objets)
//  - len       : taille imposée (Content-Length signé : le stockage refuse tout envoi de taille différente)
//  - extra     : paramètres de requête supplémentaires (liste)
async function presign(env, method, key, expires, len = null, extra = {}) {
  const host = `${env.R2_ACCOUNT_ID}.r2.cloudflarestorage.com`;
  const path = `/${env.R2_BUCKET}` + (key ? `/${key.split("/").map(uri).join("/")}` : "");
  const now = new Date();
  const amzDate = now.toISOString().replace(/[:-]|\.\d{3}/g, ""); // 20261114T184500Z
  const date = amzDate.slice(0, 8);
  const scope = `${date}/auto/s3/aws4_request`;
  const signed = len == null ? "host" : "content-length;host";
  const params = {
    ...extra,
    "X-Amz-Algorithm": "AWS4-HMAC-SHA256",
    "X-Amz-Credential": `${env.R2_ACCESS_KEY_ID}/${scope}`,
    "X-Amz-Date": amzDate,
    "X-Amz-Expires": String(expires),
    "X-Amz-SignedHeaders": signed,
  };
  const query = Object.keys(params).sort().map((k) => `${uri(k)}=${uri(params[k])}`).join("&");
  const headers = (len == null ? "" : `content-length:${len}\n`) + `host:${host}\n`;
  const canonical = [method, path, query, headers, signed, "UNSIGNED-PAYLOAD"].join("\n");
  const toSign = ["AWS4-HMAC-SHA256", amzDate, scope, await sha256hex(canonical)].join("\n");
  let k = await hmac(enc.encode("AWS4" + env.R2_SECRET_ACCESS_KEY), date);
  k = await hmac(k, "auto");
  k = await hmac(k, "s3");
  k = await hmac(k, "aws4_request");
  const sig = hex(await hmac(k, toSign));
  return `https://${host}${path}?${query}&X-Amz-Signature=${sig}`;
}

// Espace réellement occupé dans R2 par ce compte (et taille de l'objet qu'on s'apprête à remplacer).
// null si la liste est indisponible : l'appelant se rabat alors sur la table Supabase.
async function r2Usage(env, uid, name) {
  let total = 0, replaced = 0, token = null;
  for (let page = 0; page < 20; page++) {
    const extra = { "list-type": "2", prefix: `${uid}/` };
    if (token) extra["continuation-token"] = token;
    const r = await fetch(await presign(env, "GET", "", 120, null, extra));
    if (!r.ok) return null;
    const xml = await r.text();
    for (const m of xml.matchAll(/<Contents>([\s\S]*?)<\/Contents>/g)) {
      const key = (m[1].match(/<Key>([^<]*)<\/Key>/) || [])[1];
      const size = parseInt((m[1].match(/<Size>(\d+)<\/Size>/) || [])[1], 10) || 0;
      total += size;
      if (key === `${uid}/${name}`) replaced = size;
    }
    if (!/<IsTruncated>true<\/IsTruncated>/.test(xml)) return { total, replaced };
    token = ((xml.match(/<NextContinuationToken>([^<]*)<\/NextContinuationToken>/) || [])[1] || "").replace(/&amp;/g, "&");
    if (!token) return null;
  }
  return null;
}

// Limite de requêtes : liaison Cloudflare "LIMITER" si elle est configurée, sinon compteur local (par instance du Worker)
const hits = new Map();
async function tooMany(env, id, max) {
  if (env.LIMITER) { try { return !(await env.LIMITER.limit({ key: id })).success; } catch { return false; } }
  const t = Date.now();
  if (hits.size > 5000) for (const [k, v] of hits) if (t - v.t > 60000) hits.delete(k);
  const h = hits.get(id);
  if (!h || t - h.t > 60000) { hits.set(id, { t, n: 1 }); return false; }
  return ++h.n > max;
}

const json = (o, status = 200) =>
  new Response(JSON.stringify(o), { status, headers: { "Content-Type": "application/json" } });

export default {
  async fetch(req, env) {
    if (req.method !== "POST") return new Response("TynoRec sign OK");

    // 0) anti-abus : limite par adresse avant même de contacter Supabase
    const ip = req.headers.get("CF-Connecting-IP") || "?";
    if (await tooMany(env, "ip:" + ip, 600)) return json({ error: "Trop de requêtes, réessaie dans une minute." }, 429);

    // 1) qui es-tu ? (on demande à Supabase de valider le jeton de connexion)
    const auth = req.headers.get("Authorization") || "";
    const u = await fetch(`${env.SUPABASE_URL}/auth/v1/user`, {
      headers: { apikey: env.SUPABASE_ANON_KEY, Authorization: auth },
    });
    if (!u.ok) return json({ error: "Non connecté." }, 401);
    const uid = (await u.json()).id;
    if (!uid) return json({ error: "Non connecté." }, 401);
    if (await tooMany(env, "u:" + uid, 300)) return json({ error: "Trop de requêtes, réessaie dans une minute." }, 429);

    // 2) que veux-tu faire ?
    let body;
    try { body = await req.json(); } catch { return json({ error: "Requête invalide." }, 400); }
    const name = String(body.name || "");
    const method = { put: "PUT", get: "GET", delete: "DELETE" }[body.op];
    if (!method || !/^[A-Za-z0-9._-]{1,120}\.mp4$/.test(name) || name.includes("..")) return json({ error: "Requête invalide." }, 400);

    // 3) quota par compte (le total R2 gratuit est partagé entre tous les comptes)
    let len = null;
    if (method === "PUT") {
      len = Number(body.size);
      if (!Number.isSafeInteger(len) || len <= 0) return json({ error: "Version de TynoRec trop ancienne : mets l'application à jour." }, 400);
      const quota = (parseInt(env.QUOTA_MB, 10) || 1500) * 1048576;
      let used = null;
      const usage = await r2Usage(env, uid, name);          // occupation réelle du stockage (taille de l'ancien fichier de même nom déduite)
      if (usage) used = usage.total - usage.replaced;
      else {                                                 // liste indisponible : on se rabat sur la table Supabase
        const r = await fetch(`${env.SUPABASE_URL}/rest/v1/clips?select=size`, {
          headers: { apikey: env.SUPABASE_ANON_KEY, Authorization: auth },
        });
        if (r.ok) used = (await r.json()).reduce((s, c) => s + (c.size || 0), 0);
      }
      if (used !== null && used + len > quota) return json({ error: `Espace du compte plein (${Math.round(quota / 1048576)} Mo).` }, 413);
    }

    // la taille est signée dans l'URL : impossible d'envoyer plus que ce qui a été vérifié
    return json({ url: await presign(env, method, `${uid}/${name}`, 900, len) });
  },
};
