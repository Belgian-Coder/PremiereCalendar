"""Compare the database state of a restored production backup before and after the candidate image started on it.

The inputs are the outputs of schema-state.sql (counts, migration metadata and one-way row fingerprints only; no
row payload). The restore proof (private backup-restore.json) and the backup summary must describe the same dump.
The report is created exclusively; the exit code is non-zero when any check fails.

When the candidate raises the schema version, the migrations of the old schema must be unchanged and the new
schema must be applied; tables that exist only afterwards are allowed, every table that existed before must keep
its row count.
"""
import argparse
import json
import pathlib

REQUIRED_PREFERENCE_TABLES = {'appparameters', 'calendarfilterusage', 'viewsyncgroups', 'viewsyncdevices', 'viewsyncgroupstate', 'simklsyncstate'}


def parse(path):
    schema, counts, migrations, prefs, retained = None, {}, {}, {}, None
    for line in pathlib.Path(path).read_text(encoding='utf-8-sig').splitlines():
        parts = line.strip().split('|')
        if not parts or not parts[0]:
            continue
        if parts[0] == 'SCHEMA_VERSION' and len(parts) > 1:
            schema = int(parts[1])
        elif parts[0] == 'MIGRATION_HISTORY' and len(parts) >= 4:
            migrations[int(parts[1])] = {'name': parts[2], 'checksum': parts[3]}
        elif parts[0] == 'PREF_FINGERPRINT' and len(parts) >= 4:
            prefs[parts[1]] = {'rows': int(parts[2]), 'md5': parts[3]}
        elif parts[0] == 'RETAINED_ITEM_ENRICHMENTS' and len(parts) >= 3:
            retained = {'rows': int(parts[1]), 'md5': parts[2]}
        elif len(parts) == 2 and parts[1].isdigit():
            counts[parts[0]] = int(parts[1])
    return {'schema': schema, 'counts': counts, 'migrations': migrations, 'prefs': prefs, 'retainedItemEnrichments': retained}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', required=True)
    parser.add_argument('--after', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--restore-proof', required=True, help='private backup-restore.json')
    parser.add_argument('--backup-summary', required=True, help='private backup-summary.json')
    parser.add_argument('--old-schema', type=int, required=True)
    parser.add_argument('--new-schema', type=int, required=True)
    args = parser.parse_args()
    output = pathlib.Path(args.output)
    if output.exists():
        raise SystemExit('Refusing to replace existing compatibility evidence')
    before, after = parse(args.before), parse(args.after)
    old, new = args.old_schema, args.new_schema
    count_changes = {name: {'before': before['counts'].get(name), 'after': after['counts'].get(name)}
                     for name in sorted(set(before['counts']) | set(after['counts']))
                     if before['counts'].get(name) != after['counts'].get(name)
                     and not (new > old and name not in before['counts'])}
    pref_changes = {name: {'before': before['prefs'].get(name), 'after': after['prefs'].get(name)}
                    for name in sorted(set(before['prefs']) | set(after['prefs'])) if before['prefs'].get(name) != after['prefs'].get(name)}
    versions = range(1, old + 1)
    checks = {
        'backupAtExpectedOldSchema': before['schema'] == old,
        'postStartupAtNewSchema': after['schema'] == new,
        'oldMigrationHistoryPreserved': all(before['migrations'].get(v) is not None and before['migrations'].get(v) == after['migrations'].get(v) for v in versions),
        'allPreexistingTableCountsPreserved': not count_changes,
        'preferenceFingerprintsPreserved': not pref_changes and set(before['prefs']) == set(after['prefs']) == REQUIRED_PREFERENCE_TABLES,
        'retainedItemEnrichmentsPresentBeforeAndAfter': before['retainedItemEnrichments'] is not None and after['retainedItemEnrichments'] is not None,
        'retainedItemEnrichmentsUnchanged': before['retainedItemEnrichments'] == after['retainedItemEnrichments'] and before['retainedItemEnrichments'] is not None}
    report = {
        'beforeSchemaVersion': before['schema'], 'afterSchemaVersion': after['schema'],
        'beforePublicTableCounts': before['counts'], 'afterPublicTableCounts': after['counts'],
        'migrationHistory': {str(v): before['migrations'].get(v) for v in versions},
        'preferenceFingerprints': before['prefs'],
        'retainedItemEnrichmentsBefore': before['retainedItemEnrichments'], 'retainedItemEnrichmentsAfter': after['retainedItemEnrichments'],
        'tableCountChanges': count_changes, 'preferenceFingerprintChanges': pref_changes,
        'checks': checks, 'allChecksPass': all(checks.values()),
        'dataPolicy': 'Counts, migration metadata, and one-way row fingerprints only; no row payload stored.'}
    backup = json.loads(pathlib.Path(args.backup_summary).read_text(encoding='utf-8-sig'))
    restore = json.loads(pathlib.Path(args.restore_proof).read_text(encoding='utf-8-sig'))
    assert restore['restoreExitCode'] == 0 and restore['inputPipeFailed'] is False and restore['timedOut'] is False
    assert restore['backupSha256'].lower() == backup['DatabaseDumpSha256'].lower() and restore['backupBytes'] == backup['DatabaseDumpBytes']
    report['backupSha256'] = restore['backupSha256'].lower()
    report['backupBytes'] = restore['backupBytes']
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'checks': checks, 'tableCountChanges': count_changes, 'preferenceFingerprintChanges': pref_changes}, indent=2))
    raise SystemExit(0 if report['allChecksPass'] else 1)


if __name__ == '__main__':
    main()
