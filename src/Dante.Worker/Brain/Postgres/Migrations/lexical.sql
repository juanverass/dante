CREATE SCHEMA IF NOT EXISTS brain_index;
CREATE TABLE IF NOT EXISTS brain_index.lexical (
 id_record uuid PRIMARY KEY, revision bigint NOT NULL, id_tenant uuid NOT NULL,id_user uuid NOT NULL,
 id_space uuid NOT NULL, scope_project uuid NOT NULL, representation tsvector NOT NULL,
 indexer_version integer NOT NULL DEFAULT 1, configuration text NOT NULL DEFAULT 'simple');
CREATE INDEX IF NOT EXISTS lexical_gin ON brain_index.lexical USING gin(representation);
