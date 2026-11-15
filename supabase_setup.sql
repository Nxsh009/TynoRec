-- TynoRec : à coller et exécuter UNE SEULE FOIS dans Supabase > SQL Editor > New query

-- 1) Table des clips (une ligne par clip, rattachée à un compte)
create table if not exists public.clips (
  user_id    uuid not null default auth.uid() references auth.users(id) on delete cascade,
  name       text not null,
  game       text,
  dur        double precision default 0,
  fav        boolean default false,
  size       bigint default 0,
  created_at timestamptz not null default now(),
  primary key (user_id, name)
);

alter table public.clips enable row level security;

-- chaque utilisateur ne voit / ne modifie QUE ses propres lignes
create policy "clips_select" on public.clips for select to authenticated using (user_id = auth.uid());
create policy "clips_insert" on public.clips for insert to authenticated with check (user_id = auth.uid());
create policy "clips_update" on public.clips for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());
create policy "clips_delete" on public.clips for delete to authenticated using (user_id = auth.uid());

-- (Les vidéos ne sont plus dans Supabase : elles sont dans Cloudflare R2, voir worker.js)
