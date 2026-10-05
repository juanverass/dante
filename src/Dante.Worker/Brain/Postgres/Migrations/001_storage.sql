CREATE SCHEMA IF NOT EXISTS brain_data;
CREATE SCHEMA IF NOT EXISTS brain_index;
CREATE TABLE brain_data.tenants (id uuid PRIMARY KEY CHECK (id <> '00000000-0000-0000-0000-000000000000'));
CREATE TABLE brain_data.users (
 id uuid PRIMARY KEY, id_tenant uuid NOT NULL REFERENCES brain_data.tenants(id),
 telegram_user bigint NOT NULL UNIQUE CHECK (telegram_user > 0), UNIQUE(id_tenant,id));
CREATE TABLE brain_data.spaces (
 id uuid PRIMARY KEY, id_tenant uuid NOT NULL, id_user uuid NOT NULL,
 FOREIGN KEY(id_tenant,id_user) REFERENCES brain_data.users(id_tenant,id), UNIQUE(id_tenant,id_user,id));
CREATE TABLE brain_data.projects (
 id uuid PRIMARY KEY CHECK(id <> '00000000-0000-0000-0000-000000000000'),
 id_tenant uuid NOT NULL, id_user uuid NOT NULL, id_space uuid NOT NULL,
 FOREIGN KEY(id_tenant,id_user,id_space) REFERENCES brain_data.spaces(id_tenant,id_user,id),
 UNIQUE(id_tenant,id_user,id_space,id));
CREATE TABLE brain_data.records (
 id uuid PRIMARY KEY, id_tenant uuid NOT NULL, id_user uuid NOT NULL, id_space uuid NOT NULL,
 id_project uuid, scope_project uuid GENERATED ALWAYS AS (coalesce(id_project,'00000000-0000-0000-0000-000000000000'::uuid)) STORED,
 kind integer NOT NULL CHECK(kind BETWEEN 0 AND 2), revision bigint NOT NULL CHECK(revision > 0),
 content text NOT NULL, data jsonb NOT NULL, provenance jsonb NOT NULL,
 source_reference text, source_hash text, source_length bigint,
 FOREIGN KEY(id_tenant,id_user,id_space) REFERENCES brain_data.spaces(id_tenant,id_user,id),
 FOREIGN KEY(id_tenant,id_user,id_space,id_project) REFERENCES brain_data.projects(id_tenant,id_user,id_space,id),
 CHECK ((source_reference IS NULL AND source_hash IS NULL AND source_length IS NULL) OR
        (kind = 1 AND source_reference IS NOT NULL AND source_hash ~ '^[a-f0-9]{64}$' AND source_length >= 0)),
 UNIQUE(id_tenant,id_user,id_space,scope_project,id));
CREATE TABLE brain_data.revisions (
 id_record uuid NOT NULL REFERENCES brain_data.records(id) ON DELETE CASCADE,
 revision bigint NOT NULL, content text NOT NULL, data jsonb NOT NULL, provenance jsonb NOT NULL,
 source_reference text, source_hash text, source_length bigint,
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(id_record,revision));
CREATE TABLE brain_data.links (
 id_tenant uuid NOT NULL,id_user uuid NOT NULL,id_space uuid NOT NULL,scope_project uuid NOT NULL,
 id_from uuid NOT NULL,id_to uuid NOT NULL,kind text NOT NULL CHECK(length(kind)>0),
 PRIMARY KEY(id_from,id_to,kind),
 FOREIGN KEY(id_tenant,id_user,id_space,scope_project,id_from)
 REFERENCES brain_data.records(id_tenant,id_user,id_space,scope_project,id) ON DELETE CASCADE,
 FOREIGN KEY(id_tenant,id_user,id_space,scope_project,id_to)
 REFERENCES brain_data.records(id_tenant,id_user,id_space,scope_project,id) ON DELETE CASCADE);
CREATE TABLE brain_meta.index_pending (
 id_record uuid PRIMARY KEY REFERENCES brain_data.records(id) ON DELETE CASCADE,
 revision bigint NOT NULL);
