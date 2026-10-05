-- Run explicitly as administrator using psql -X -v ON_ERROR_STOP=1 -f deploy/brain/bootstrap.sql.
-- Passwords come from private environment, not -v arguments or tracked files.
\getenv migrator_password BRAIN_MIGRATOR_PASSWORD
\getenv runtime_password BRAIN_RUNTIME_PASSWORD
\if :{?migrator_password}
\else
SELECT 1/0;
\endif
\if :{?runtime_password}
\else
SELECT 1/0;
\endif
SELECT length(:'migrator_password') >= 16 AND length(:'runtime_password') >= 16
       AND :'migrator_password' <> :'runtime_password' AS passwords_valid \gset
\if :passwords_valid
\else
SELECT 1/0;
\endif
SELECT 'CREATE ROLE brain_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION'
WHERE NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='brain_migrator') \gexec
SELECT 'CREATE ROLE brain_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION'
WHERE NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='brain_runtime') \gexec
ALTER ROLE brain_migrator NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'migrator_password';
ALTER ROLE brain_runtime NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'runtime_password';
SELECT 'CREATE DATABASE dante_brain OWNER brain_migrator'
WHERE NOT EXISTS(SELECT 1 FROM pg_database WHERE datname='dante_brain') \gexec
\connect dante_brain
REVOKE ALL ON DATABASE dante_brain FROM PUBLIC;
GRANT CONNECT ON DATABASE dante_brain TO brain_runtime;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SET ROLE brain_migrator;
CREATE SCHEMA IF NOT EXISTS brain_data;
CREATE SCHEMA IF NOT EXISTS brain_meta;
CREATE SCHEMA IF NOT EXISTS brain_index;
GRANT USAGE ON SCHEMA brain_data,brain_meta,brain_index TO brain_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA brain_data GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO brain_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA brain_index GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO brain_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA brain_meta GRANT SELECT ON TABLES TO brain_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA brain_data,brain_index TO brain_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA brain_meta TO brain_runtime;
-- index_pending DML grant is applied after migrations via runtime-grants.sql.
RESET ROLE;
