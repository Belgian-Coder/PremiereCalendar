"""Summarize the vulnerability scan, SBOM, NuGet audit and archive audit into one pass/fail report.

Reads <bundle>/release-<version>-{trivy.json,sbom.cdx.json,audit.json,scanners.json} and release-nuget-audit.log, creates
release-<version>-scan-summary.json exclusively and exits non-zero unless every check passes. With
--ignore-unfixed only vulnerabilities that have a fixed version count (the former hosted workflow's policy); that
choice is recorded in the report. The scanners evidence records the Trivy vulnerability DB metadata read from the
persistent release cache; the report fails unless that DB was updated at most 48 hours before the scan.
"""
import argparse
import collections
import datetime
import json
import pathlib
import re

MAX_TRIVY_DB_AGE_HOURS = 48


def parse_utc(text):
    """Parse an RFC 3339 UTC timestamp (Trivy writes up to nanoseconds); None when it cannot be read."""
    match = re.fullmatch(r'(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})', str(text or ''))
    if not match:
        return None
    fraction = (match.group(2) or '0')[:6].ljust(6, '0')
    zone = '+00:00' if match.group(3) == 'Z' else match.group(3)
    return datetime.datetime.fromisoformat(f'{match.group(1)}.{fraction}{zone}').astimezone(datetime.timezone.utc)


def trivy_db_status(scanners, max_age_hours=MAX_TRIVY_DB_AGE_HOURS):
    """Describe the Trivy vulnerability DB recorded in the scanners evidence and whether it is fresh enough."""
    try:
        metadata = json.loads(scanners.get('trivyDbMetadataRaw') or '')
    except (ValueError, AttributeError):
        metadata = {}
    updated = parse_utc(metadata.get('UpdatedAt')) if isinstance(metadata, dict) else None
    checked = parse_utc(scanners.get('trivyDbCheckedAtUtc'))
    age = None if updated is None or checked is None else (checked - updated).total_seconds() / 3600
    return {'updatedAt': metadata.get('UpdatedAt') if isinstance(metadata, dict) else None,
            'nextUpdate': metadata.get('NextUpdate') if isinstance(metadata, dict) else None,
            'downloadedAt': metadata.get('DownloadedAt') if isinstance(metadata, dict) else None,
            'checkedAtUtc': scanners.get('trivyDbCheckedAtUtc'), 'ageHours': None if age is None else round(age, 2),
            'maxAgeHours': max_age_hours, 'fresh': age is not None and -0.1 <= age <= max_age_hours}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--ignore-unfixed', action='store_true')
    args = parser.parse_args()
    p, v = pathlib.Path(args.bundle), args.version
    output = p / f'release-{v}-scan-summary.json'
    if output.exists():
        raise SystemExit(f'Refusing to overwrite existing scan summary: {output}')
    load = lambda name: json.loads((p / name).read_text(encoding='utf-8-sig'))
    scanners = load(f'release-{v}-scanners.json')
    db = trivy_db_status(scanners)
    trivy, sbom, audit = load(f'release-{v}-trivy.json'), load(f'release-{v}-sbom.cdx.json'), load(f'release-{v}-audit.json')
    counts, fixed, vulnerabilities = collections.Counter(), collections.Counter(), []
    for result in trivy.get('Results', []):
        for item in result.get('Vulnerabilities') or []:
            severity = item.get('Severity', 'UNKNOWN').upper()
            counts[severity] += 1
            if item.get('FixedVersion'):
                fixed[severity] += 1
            if item.get('FixedVersion') or not args.ignore_unfixed:
                vulnerabilities.append(item)
    misconfigurations = sum(len(x.get('Misconfigurations') or []) for x in trivy.get('Results', []))
    secrets = sum(len(x.get('Secrets') or []) for x in trivy.get('Results', []))
    nuget = (p / 'release-nuget-audit.log').read_text(encoding='utf-8-sig', errors='replace')
    checks = {
        'trivyZeroVulnerabilities': not vulnerabilities,
        'trivyZeroMisconfigurations': misconfigurations == 0,
        'trivyZeroSecrets': secrets == 0,
        'trivyDbFresh': db['fresh'],
        'sbomCycloneDxNonempty': sbom.get('bomFormat') == 'CycloneDX' and len(sbom.get('components', [])) > 0,
        'archiveIdentityPass': all(audit['checks'].values()),
        'nugetNoVulnerablePackages': 'has no vulnerable packages' in nuget}
    report = {
        'sourceRevision': audit['sourceRevision'], 'archiveSha256': audit['archiveSha256'],
        'policy': 'fixable vulnerabilities only (--ignore-unfixed)' if args.ignore_unfixed else 'any vulnerability fails',
        'trivy': {'results': len(trivy.get('Results', [])), 'vulnerabilities': sum(counts.values()), 'countedVulnerabilities': len(vulnerabilities),
                  'bySeverity': dict(counts), 'fixableBySeverity': dict(fixed), 'misconfigurations': misconfigurations, 'secrets': secrets, 'database': db},
        'sbom': {'format': sbom.get('bomFormat'), 'specVersion': sbom.get('specVersion'), 'components': len(sbom.get('components', []))},
        'checks': checks, 'allChecksPass': all(checks.values())}
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report['allChecksPass'] else 1)


if __name__ == '__main__':
    main()
