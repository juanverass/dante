-- Execute as brain_migrator after migrate, rebuild or restore. Never as the runtime role.
GRANT USAGE ON SCHEMA brain_data,brain_meta,brain_index TO brain_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA brain_data,brain_index TO brain_runtime;
GRANT SELECT ON brain_meta.migrations TO brain_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON brain_meta.index_pending TO brain_runtime;
