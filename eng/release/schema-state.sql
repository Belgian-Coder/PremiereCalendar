-- Sanitized state of a PremiereCalendar database: counts, migration metadata and one-way row fingerprints only.
-- Run with: psql -X -A -t -F '|' -q -v ON_ERROR_STOP=1 -v maxver=<old schema version> -f - < schema-state.sql
SELECT 'SCHEMA_VERSION', COALESCE(MAX(version), 0)::text FROM schemamigrations;
SELECT format('SELECT %L, count(*)::bigint FROM %I.%I;', table_name, table_schema, table_name)
FROM information_schema.tables
WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
ORDER BY table_name;
\gexec
SELECT 'MIGRATION_HISTORY', version::text, name, checksum FROM schemamigrations WHERE version <= :maxver ORDER BY version;
SELECT 'PREF_FINGERPRINT', 'appparameters', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM appparameters t;
SELECT 'PREF_FINGERPRINT', 'calendarfilterusage', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM calendarfilterusage t;
SELECT 'PREF_FINGERPRINT', 'viewsyncgroups', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM viewsyncgroups t;
SELECT 'PREF_FINGERPRINT', 'viewsyncdevices', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM viewsyncdevices t;
SELECT 'PREF_FINGERPRINT', 'viewsyncgroupstate', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM viewsyncgroupstate t;
SELECT 'PREF_FINGERPRINT', 'simklsyncstate', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM simklsyncstate t;
SELECT 'RETAINED_ITEM_ENRICHMENTS', count(*)::text, md5(COALESCE(string_agg(to_jsonb(t)::text, chr(10) ORDER BY to_jsonb(t)::text), '')) FROM itemenrichments t;
