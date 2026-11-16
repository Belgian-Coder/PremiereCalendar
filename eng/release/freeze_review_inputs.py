"""Freeze what a reviewer is asked to approve: the exact base-to-release diff and the committed blob inventory.

Writes review-source.patch and review-inputs-manifest.json into the release bundle (never overwriting) after the
release commit exists. The reviewers' record (source-review.json) must then quote the manifest SHA-256, the patch
SHA-256 and the base commit, which verify_reviewed_source.py checks before Build/Load/Deploy/Close rely on it.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import re
import subprocess


def tree_blobs(data):
    blobs = {}
    for row in data.split(b'\0'):
        if not row:
            continue
        metadata, name = row.split(b'\t', 1)
        mode, kind, oid = metadata.split()
        if kind != b'blob' or mode not in (b'100644', b'100755') or not re.fullmatch(b'[0-9a-f]{40}', oid):
            raise SystemExit('Unsupported committed source entry')
        name = name.decode('utf-8')
        if not name or name in blobs:
            raise SystemExit('Blob inventory is inconsistent')
        blobs[name] = oid.decode('ascii')
    return blobs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--bundle', required=True)
    parser.add_argument('--base', required=True, help='commit the release is reviewed against (previous release source)')
    parser.add_argument('--revision', required=True, help='the release commit')
    args = parser.parse_args()
    for name in ('base', 'revision'):
        if not re.fullmatch(r'[0-9a-f]{40}', getattr(args, name)):
            parser.error(f'--{name} must be an exact 40-character commit SHA')
    repo, bundle = pathlib.Path(args.repo), pathlib.Path(args.bundle)
    git = lambda *a, **k: subprocess.check_output(['git', '-C', str(repo), *a], **k)
    if git('rev-parse', 'HEAD').decode().strip() != args.revision:
        raise SystemExit('HEAD is not the release commit')
    if git('status', '--porcelain', '--untracked-files=no').strip():
        raise SystemExit('The tracked tree is not clean')
    for name in ('review-source.patch', 'review-inputs-manifest.json'):
        if (bundle / name).exists():
            raise SystemExit(f'Refusing to overwrite existing review input: {name}')
    diff = git('diff', '--no-ext-diff', '--no-textconv', '--no-color', args.base, args.revision, '--')
    # Read names and IDs together; expanding every path into argv exceeds Windows' command-line limit.
    blobs = tree_blobs(git('ls-tree', '-r', '-z', '--full-tree', args.revision))
    names = sorted(blobs)
    fingerprints = bundle / 'tested-source-fingerprints.json'
    manifest = {
        'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'baseCommit': args.base, 'sourceCommit': args.revision,
        'workingDiffSha256': hashlib.sha256(diff).hexdigest(),
        'repoInventory': names, 'repositoryBlobIds': {name: blobs[name] for name in names},
        'testedSourceFingerprintSha256': hashlib.sha256(fingerprints.read_bytes()).hexdigest() if fingerprints.exists() else None,
        'policy': 'Frozen exact base-to-commit diff and committed blob IDs; verify_reviewed_source.py binds them to the release commit.'}
    with (bundle / 'review-source.patch').open('xb') as stream:
        stream.write(diff)
    with (bundle / 'review-inputs-manifest.json').open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'files': len(names), 'diffSha256': manifest['workingDiffSha256'],
                      'manifestSha256': hashlib.sha256((bundle / 'review-inputs-manifest.json').read_bytes()).hexdigest()}))


if __name__ == '__main__':
    main()
