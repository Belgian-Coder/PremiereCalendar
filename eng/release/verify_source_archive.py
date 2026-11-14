"""Verify the raw archived C#/SQL bytes against the exact Git blobs before any candidate image is built.

`git archive` output is compared, file by file, with the committed blobs read through `git cat-file` (raw bytes,
no checkout or attribute conversion). Optionally the tested-source fingerprints are bound to the same bytes.
The evidence file is created with exclusive access: an existing one is never replaced.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import tarfile


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate tested fingerprint path: ' + key)
        result[key] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repo', 'revision', 'archive', 'output', 'version'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--tested-fingerprints', help='optional tested-source-fingerprints.json to bind to the archive')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.revision):
        parser.error('revision must be an exact commit SHA')
    output = pathlib.Path(args.output)
    if output.exists():
        parser.error('existing archive verification evidence must be retained')

    tested_cs, fingerprint_bytes = {}, None
    if args.tested_fingerprints:
        fingerprint_bytes = pathlib.Path(args.tested_fingerprints).read_bytes()
        fingerprints = json.loads(fingerprint_bytes.decode('utf-8-sig'), object_pairs_hook=unique_pairs)
        if not isinstance(fingerprints, dict) or not fingerprints:
            raise ValueError('tested fingerprints must be a nonempty path-to-SHA256 object')
        for path, sha in fingerprints.items():
            canonical = pathlib.PurePosixPath(path)
            if (canonical.is_absolute() or canonical.as_posix() != path or '\\' in path or '..' in canonical.parts
                    or ':' in path or not isinstance(sha, str) or not re.fullmatch(r'[0-9a-fA-F]{64}', sha)):
                raise ValueError('invalid tested fingerprint path or SHA256: ' + path)
            if canonical.suffix.lower() == '.cs':
                tested_cs[path] = sha.lower()
        if not tested_cs:
            raise ValueError('tested fingerprints contain no C# source')

    git = ['git', '-C', args.repo]
    commit = subprocess.check_output(git + ['rev-parse', args.revision + '^{commit}']).decode().strip()
    if commit != args.revision:
        parser.error('revision did not resolve to the requested commit')
    tree = subprocess.check_output(git + ['ls-tree', '-r', '-z', '--full-tree', commit])
    expected = {}
    for row in tree.split(b'\0'):
        if row:
            metadata, path = row.split(b'\t', 1)
            mode, kind, oid = metadata.split()
            path = path.decode('utf-8')
            if pathlib.PurePosixPath(path).suffix.lower() in ('.cs', '.sql'):
                if kind != b'blob' or mode not in (b'100644', b'100755'):
                    raise ValueError('unexpected source tree entry: ' + path)
                expected[path] = oid.decode('ascii')
    if not any(path.lower().endswith('.cs') for path in expected):
        raise ValueError('commit contains no C# source')

    mismatches = []
    verified = {'.cs': 0, '.sql': 0}
    committed_hashes, archived_hashes = {}, {}
    aggregate = hashlib.sha256()
    archive_path = pathlib.Path(args.archive)
    with tarfile.open(archive_path, 'r:') as archive, subprocess.Popen(
        git + ['cat-file', '--batch'], stdin=subprocess.PIPE, stdout=subprocess.PIPE
    ) as blobs:
        names = [member.name for member in archive.getmembers()]
        if len(names) != len(set(names)):
            raise ValueError('duplicate entries in source archive')
        archived = {member.name: member for member in archive.getmembers()
                    if pathlib.PurePosixPath(member.name).suffix.lower() in verified}
        for path in sorted(set(expected) ^ set(archived)):
            mismatches.append({'path': path, 'reason': 'archive/commit source inventory differs'})
        for path in sorted(expected):
            blobs.stdin.write((expected[path] + '\n').encode('ascii'))
            blobs.stdin.flush()
            header = blobs.stdout.readline().split()
            if len(header) != 3 or header[0].decode('ascii') != expected[path] or header[1] != b'blob':
                raise ValueError('unexpected git cat-file response')
            size = int(header[2])
            raw = blobs.stdout.read(size)
            if len(raw) != size or blobs.stdout.read(1) != b'\n':
                raise ValueError('truncated git blob response')
            committed_sha = hashlib.sha256(raw).hexdigest()
            committed_hashes[path] = committed_sha
            member = archived.get(path)
            if member is None:
                continue
            if not member.isfile():
                mismatches.append({'path': path, 'reason': 'archived source is not a regular file'})
                continue
            with archive.extractfile(member) as stream:
                archived_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
            archived_hashes[path] = archived_sha
            if archived_sha != committed_sha:
                mismatches.append({'path': path, 'reason': 'raw SHA256 differs',
                                   'committedSha256': committed_sha, 'archivedSha256': archived_sha})
            else:
                verified[pathlib.PurePosixPath(path).suffix.lower()] += 1
                aggregate.update(path.encode('utf-8') + b'\0' + committed_sha.encode('ascii') + b'\n')
        archive_commit = archive.pax_headers.get('comment')
        if archive_commit != commit:
            mismatches.append({'reason': 'archive Git commit header differs', 'archiveCommit': archive_commit})
        blobs.stdin.close()
        if blobs.wait() != 0:
            raise ValueError('git cat-file failed')

    committed_cs = {path for path in expected if path.lower().endswith('.cs')}
    tested_mismatches = []
    if args.tested_fingerprints:
        for path in sorted(committed_cs - set(tested_cs)):
            tested_mismatches.append({'path': path, 'reason': 'committed C# has no tested fingerprint'})
        for path, sha in sorted(tested_cs.items()):
            if sha != committed_hashes.get(path) or sha != archived_hashes.get(path):
                tested_mismatches.append({'path': path, 'reason': 'tested C# differs from commit/archive',
                                          'testedSha256': sha, 'committedSha256': committed_hashes.get(path),
                                          'archivedSha256': archived_hashes.get(path)})
    mismatches.extend(tested_mismatches)
    with archive_path.open('rb') as stream:
        archive_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
    report = {
        'version': args.version, 'sourceRevision': commit, 'sourceArchive': archive_path.name,
        'sourceArchiveSha256': archive_sha, 'allChecksPass': not mismatches,
        'verifiedFiles': verified, 'verifiedSourceAggregateSha256': aggregate.hexdigest(),
        'testedSourceFingerprintsSha256': hashlib.sha256(fingerprint_bytes).hexdigest() if fingerprint_bytes else None,
        'testedCSharpFiles': len(tested_cs) if args.tested_fingerprints else None,
        'testedCSharpInventoryComplete': (set(tested_cs) == committed_cs) if args.tested_fingerprints else None,
        'testedCSharpCommittedBlobParity': (not tested_mismatches) if args.tested_fingerprints else None,
        'verification': 'raw archived bytes SHA256 compared with exact committed Git blobs',
        'archivePolicy': {
            'gitScopedConfig': 'core.autocrlf=false',
            'csharpLineEndings': 'committed *.cs text eol=lf attribute plus raw blob/archive verification',
            'otherFiles': 'Git attributes/native EOL rules apply; core.autocrlf=false alone does not force LF'},
        'mismatches': mismatches}
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'allChecksPass': report['allChecksPass'], 'verifiedFiles': verified,
                      'mismatchCount': len(mismatches), 'sourceRevision': commit}))
    return 0 if report['allChecksPass'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
