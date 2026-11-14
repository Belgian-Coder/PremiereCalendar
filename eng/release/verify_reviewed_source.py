"""Read-only binding of the reviewed inputs to the release commit; prints hashes and counts only.

Checks that source-review.json quotes the frozen manifest and patch, that the committed inventory and canonical
blob IDs equal the frozen ones, that the exact base-to-commit diff equals the reviewed patch, and (when the
manifest recorded one) that the tested-source fingerprint file is unchanged. It writes nothing.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys
from pathlib import PurePosixPath


class BindingError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise BindingError(message)


def read_json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'Duplicate JSON property')
            result[key] = value
        return result
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def safe_name(name):
    path = PurePosixPath(name)
    require(isinstance(name, str) and name and not path.is_absolute() and path.as_posix() == name
            and '..' not in path.parts and not any(c in name for c in ('\\', ':', '\r', '\n')), 'Unsafe repository path')
    return name


def validate(repo, bundle, revision):
    require(re.fullmatch('[0-9a-f]{40}', revision) is not None, 'Expected source must be an exact commit')
    review = read_json(bundle / 'source-review.json')
    manifest_path = bundle / 'review-inputs-manifest.json'
    manifest = read_json(manifest_path)
    patch_path = bundle / 'review-source.patch'
    require(review.get('sourceRevision') == revision, 'Source review identifies another commit')
    require(manifest.get('sourceCommit') == revision, 'Frozen manifest identifies another commit')
    require(review.get('inputManifestSha256') == sha(manifest_path), 'Reviewed input manifest SHA differs')
    base = manifest.get('baseCommit', '')
    require(re.fullmatch('[0-9a-f]{40}', base) is not None and review.get('baseCommit') == base, 'Reviewed base commit differs')
    patch_sha = sha(patch_path)
    require(review.get('reviewedDiffSha256') == manifest.get('workingDiffSha256') == patch_sha, 'Reviewed patch SHA differs')
    inventory = manifest.get('repoInventory')
    blobs = manifest.get('repositoryBlobIds', {})
    require(isinstance(inventory, list) and inventory and len(set(inventory)) == len(inventory), 'Repository inventory is absent or duplicated')
    require(set(map(safe_name, inventory)) == set(blobs) and all(re.fullmatch('[0-9a-f]{40}', o) for o in blobs.values()), 'Canonical reviewed blob inventory is incomplete')

    git = lambda *a, **k: subprocess.check_output(['git', '-C', str(repo), *a], **k)
    require(git('rev-parse', revision + '^{commit}').decode().strip() == revision, 'Expected commit does not resolve exactly')
    committed = {}
    for row in git('ls-tree', '-r', '-z', '--full-tree', revision).split(b'\0'):
        if not row:
            continue
        metadata, name = row.split(b'\t', 1)
        mode, kind, oid = metadata.split()
        require(kind == b'blob' and mode in (b'100644', b'100755'), 'Unsupported committed source entry')
        committed[safe_name(name.decode('utf-8'))] = oid.decode('ascii')
    require(committed == blobs, 'Committed inventory/blob IDs differ from the reviewed source')
    require(git('diff', '--no-ext-diff', '--no-textconv', '--no-color', base, revision, '--') == patch_path.read_bytes(),
            'Exact base-to-commit diff differs from the reviewed patch')
    fingerprints = bundle / 'tested-source-fingerprints.json'
    if manifest.get('testedSourceFingerprintSha256') is not None:
        require(fingerprints.exists() and sha(fingerprints) == manifest['testedSourceFingerprintSha256'], 'Tested fingerprint file differs from the one reviewed')
    return {'allChecksPass': True, 'sourceRevision': revision, 'baseCommit': base, 'inputManifestSha256': sha(manifest_path),
            'reviewedDiffSha256': patch_sha, 'repositoryFiles': len(inventory), 'canonicalGitBlobParity': True, 'exactReviewedDiffParity': True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=pathlib.Path, required=True)
    parser.add_argument('--bundle', type=pathlib.Path, required=True)
    parser.add_argument('--revision', required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(validate(args.repo.resolve(), args.bundle.resolve(), args.revision), sort_keys=True))
    except BindingError as error:
        print(json.dumps({'allChecksPass': False, 'error': str(error)}))
        return 1
    except Exception:
        print(json.dumps({'allChecksPass': False, 'error': 'Unable to read or validate required source evidence'}))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
