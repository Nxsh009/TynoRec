// TynoRec : Cloudflare Worker "tynorec-sign"
// Rôle : vérifier que l'utilisateur est connecté (compte Supabase), puis lui donner une URL signée
// valable 15 min pour envoyer / télécharger / supprimer SES clips dans R2 (dossier <user_id>/).
// Les vidéos ne passent pas par le Worker : l'app parle directement à R2 (aucune limite de taille côté Worker).
//
// Variables à définir dans Cloudflare > Workers > tynorec-sign > Settings > Variables and Secrets :
//   SUPABASE_URL, SUPABASE_ANON_KEY, R2_ACCOUNT_ID, R2_BUCKET            (texte)
//   R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY                               (secrets)
//   QUOTA_MB                                                             (texte, optionnel, 1500 par défaut : espace max par compte)

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

// URL signée AWS SigV4 (compatible R2)
async function presign(env, method, key, expires) {
  const host = `${env.R2_ACCOUNT_ID}.r2.cloudflarestorage.com`;
  const path = `/${env.R2_BUCKET}/${key.split("/").map(uri).join("/")}`;
  const now = new Date();
  const amzDate = now.toISOString().replace(/[:-]|\.\d{3}/g, ""); // 20261114T184500Z
  const date = amzDate.slice(0, 8);
  const scope = `${date}/auto/s3/aws4_request`;
  const params = {
    "X-Amz-Algorithm": "AWS4-HMAC-SHA256",
    "X-Amz-Credential": `${env.R2_ACCESS_KEY_ID}/${scope}`,
    "X-Amz-Date": amzDate,
    "X-Amz-Expires": String(expires),
    "X-Amz-SignedHeaders": "host",
  };
  const query = Object.keys(params).sort().map((k) => `${uri(k)}=${uri(params[k])}`).join("&");
  const canonical = [method, path, query, `host:${host}\n`, "host", "UNSIGNED-PAYLOAD"].join("\n");
  const toSign = ["AWS4-HMAC-SHA256", amzDate, scope, await sha256hex(canonical)].join("\n");
  let k = await hmac(enc.encode("AWS4" + env.R2_SECRET_ACCESS_KEY), date);
  k = await hmac(k, "auto");
  k = await hmac(k, "s3");
  k = await hmac(k, "aws4_request");
  const sig = hex(await hmac(k, toSign));
  return `https://${host}${path}?${query}&X-Amz-Signature=${sig}`;
}

const json = (o, status = 200) =>
  new Response(JSON.stringify(o), { status, headers: { "Content-Type": "application/json" } });

export default {
  async fetch(req, env) {
    if (req.method !== "POST") return new Response("TynoRec sign OK");

    // 1) qui es-tu ? (on demande à Supabase de valider le jeton de connexion)
    const auth = req.headers.get("Authorization") || "";
    const u = await fetch(`${env.SUPABASE_URL}/auth/v1/user`, {
      headers: { apikey: env.SUPABASE_ANON_KEY, Authorization: auth },
    });
    if (!u.ok) return json({ error: "Non connecté." }, 401);
    const uid = (await u.json()).id;
    if (!uid) return json({ error: "Non connecté." }, 401);

    // 2) que veux-tu faire ?
    let body;
    try { body = await req.json(); } catch { return json({ error: "Requête invalide." }, 400); }
    const name = String(body.name || "");
    const method = { put: "PUT", get: "GET", delete: "DELETE" }[body.op];
    if (!method || !/^[A-Za-z0-9._-]{1,120}\.mp4$/.test(name) || name.includes("..")) return json({ error: "Requête invalide." }, 400);

    // 3) quota par compte (le total R2 gratuit est partagé entre tous les comptes)
    if (method === "PUT") {
      const quota = (parseInt(env.QUOTA_MB, 10) || 1500) * 1048576;
      const r = await fetch(`${env.SUPABASE_URL}/rest/v1/clips?select=size`, {
        headers: { apikey: env.SUPABASE_ANON_KEY, Authorization: auth },
      });
      if (r.ok) {
        const used = (await r.json()).reduce((s, c) => s + (c.size || 0), 0);
        if (used >= quota) return json({ error: `Espace du compte plein (${Math.round(quota / 1048576)} Mo).` }, 413);
      }
    }

    return json({ url: await presign(env, method, `${uid}/${name}`, 900) });
  },
};
