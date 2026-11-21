-- CharPage loader store.
--
-- The gear SWFs a character load needs are kept here instead of being written to
-- the user's disk. One row per CDN path, carrying the name the character page
-- printed, so the cache is readable on its own:
--
--   path    items/helms/ksbacker27helm.swf
--   name    Gan's Hooded Judgement
--   linkage KSBacker27Helm
--   data    <base64 SWF>
--
-- Re-running this file is safe: every statement is idempotent, so an existing
-- table just picks up the two metadata columns.

create table if not exists public.char_swfs (
  path       text primary key,            -- items/helms/x.swf, classes/f/x.swf (lower case)
  name       text,                        -- display name at store time
  linkage    text,                        -- CharPage linkage, when the page gave one
  data       text not null,               -- base64 SWF bytes
  size       integer not null default 0,  -- byte length before base64
  created_at timestamptz not null default now()
);

-- Table created by the first revision (path/data/size/created_at only).
alter table public.char_swfs add column if not exists name text;
alter table public.char_swfs add column if not exists linkage text;

alter table public.char_swfs enable row level security;

grant select, insert, update on public.char_swfs to anon, authenticated;

drop policy if exists char_swfs_read on public.char_swfs;
create policy char_swfs_read on public.char_swfs
  for select to anon, authenticated using (true);

-- The app writes with the same publishable key it reads the catalog with, so
-- inserts and the upsert's update branch both need a policy.
drop policy if exists char_swfs_insert on public.char_swfs;
create policy char_swfs_insert on public.char_swfs
  for insert to anon, authenticated with check (true);

drop policy if exists char_swfs_update on public.char_swfs;
create policy char_swfs_update on public.char_swfs
  for update to anon, authenticated using (true) with check (true);
