"""Fingerprint the tracked source bytes that are about to be tested, or verify an earlier fingerprint file.

Freeze mode (default) requires HEAD to be the release commit and the tracked tree to be clean, requires every
*.cs file to be LF (the repository's .gitattributes checks C# out as LF and release evidence compares raw bytes
with the committed blobs), and writes {repository-relative path: SHA-256} for the source files that matter.
It refuses to overwrite an existing fingerprint file.

Verify mode (--verify FILE) re-hashes the working tree against FILE and, with --revision, compares the C# files
with the committed Git blobs byte for byte. Neither mode changes the repository.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess
import sys

SUFFIXES = {'.cs', '.razor', '.json', '.csproj', '.props', '.targets', '.sql', '.resx', '.css', '.js', '.ps1', '.py', '.sh'}
SPECIAL_NAMES = {'Dockerfile', 'PremiereCalendar.slnx', 'compose.yaml', 'compose.dev.yaml', '.dockerignore', '.gitattributes'}


def git(repo, *args, check=True):
    return subprocess.run(['git', '-C', str(repo), *args], check=check, capture_output=True).stdout


def tracked_files(repo):
    names = git(repo, 'ls-files', '-z').decode('utf-8').split('\0')
    return sorted(n for n in names if n)


def wanted(name):
    path = pathlib.PurePosixPath(name)
    return path.suffix.lower() in SUFFIXES or path.name in SPECIAL_NAMES


def sha(data):
    return hashlib.sha256(data).hexdigest()


def freeze(args):
    repo = pathlib.Path(args.repo).resolve()
    head = git(repo, 'rev-parse', 'HEAD').decode('ascii').strip()
    if head != args.revision:
        raise SystemExit(f'HEAD {head} is not the release commit {args.revision}')
    if git(repo, 'status', '--porcelain', '--untracked-files=no').strip():
        raise SystemExit('The tracked tree is not clean')
    output = pathlib.Path(args.output)
    if output.exists():
        raise SystemExit(f'Refusing to overwrite existing fingerprints: {output}')
    fingerprints, crlf = {}, []
    for name in tracked_files(repo):
        if not wanted(name):
            continue
        source = repo / name
        if not source.is_file() or source.is_symlink():
            raise SystemExit(f'Tracked source is missing or not a regular file: {name}')
        data = source.read_bytes()
        if name.endswith('.cs') and b'\r' in data:
            crlf.append(name)
        fingerprints[name] = sha(data)
    if crlf:
        shown = ', '.join(crlf[:20]) + (' ...' if len(crlf) > 20 else '')
        raise SystemExit(f'{len(crlf)} C# file(s) contain CR characters; every *.cs file must be LF before freezing '
                         f'(check with `git ls-files --eol`, then re-checkout them): {shown}')
    if not any(n.endswith('.cs') for n in fingerprints):
        raise SystemExit('No C# source was found to fingerprint')
    check(repo, fingerprints, args.revision)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(fingerprints, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'fingerprinted': len(fingerprints), 'csharp': sum(n.endswith('.cs') for n in fingerprints), 'revision': head}))


def committed_csharp(repo, revision):
    """Read exact committed C# blobs through one Git batch, preserving binary bytes."""
    expected = {}
    for entry in git(repo, 'ls-tree', '-r', '-z', revision).split(b'\0'):
        if not entry:
            continue
        metadata, path = entry.split(b'\t', 1)
        if path.endswith(b'.cs'):
            fields = metadata.split()
            if len(fields) != 3 or fields[1] != b'blob':
                raise SystemExit('Committed C# source is not a Git blob')
            name = path.decode('utf-8')
            if name in expected:
                raise SystemExit('Duplicate committed C# path')
            expected[name] = fields[2].decode('ascii')
    committed = {}
    with subprocess.Popen(['git', '-C', str(repo), 'cat-file', '--batch'],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE) as blobs:
        for name, oid in expected.items():
            blobs.stdin.write((oid + '\n').encode('ascii'))
            blobs.stdin.flush()
            header = blobs.stdout.readline().split()
            if len(header) != 3 or header[0].decode('ascii') != oid or header[1] != b'blob':
                raise SystemExit('Unexpected Git batch response while verifying committed source')
            try:
                size = int(header[2])
            except ValueError:
                raise SystemExit('Invalid Git blob size')
            if size < 0:
                raise SystemExit('Invalid Git blob size')
            raw = blobs.stdout.read(size)
            if len(raw) != size or blobs.stdout.read(1) != b'\n':
                raise SystemExit('Truncated Git blob while verifying committed source')
            identity = hashlib.sha1(b'blob ' + str(size).encode('ascii') + b'\0' + raw).hexdigest()
            if identity != oid:
                raise SystemExit('Git blob content does not match its object ID')
            committed[name] = raw
        blobs.stdin.close()
        if blobs.wait() != 0:
            raise SystemExit('Git batch source verification failed')
    return committed


def check(repo, fingerprints, revision):
    problems = []
    committed = committed_csharp(repo, revision) if revision else {}
    for name, want in fingerprints.items():
        path = repo / name
        if not path.is_file():
            problems.append(f'missing: {name}')
            continue
        data = path.read_bytes()
        if sha(data) != want:
            problems.append(f'changed since fingerprinting: {name}')
        if revision and name.endswith('.cs'):
            if committed.get(name) != data:
                problems.append(f'differs from the committed blob: {name}')
    if revision:
        committed_cs = set(committed)
        tested_cs = {n for n in fingerprints if n.endswith('.cs')}
        if committed_cs != tested_cs:
            problems.append(f'C# inventory differs: {len(committed_cs ^ tested_cs)} file(s) are committed but untested or vice versa')
    if problems:
        raise SystemExit('Tested-source verification failed:\n  ' + '\n  '.join(problems[:30]))


def verify(args):
    repo = pathlib.Path(args.repo).resolve()
    raw = pathlib.Path(args.verify).read_bytes()
    def unique_paths(pairs):
        result = {}
        for name, value in pairs:
            if name in result:
                raise SystemExit('Duplicate path in fingerprint file')
            result[name] = value
        return result
    fingerprints = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique_paths)
    if not isinstance(fingerprints, dict) or not fingerprints:
        raise SystemExit('The fingerprint file must be a non-empty object')
    for want in fingerprints.values():
        if not isinstance(want, str) or len(want) != 64 or any(c not in '0123456789abcdef' for c in want):
            raise SystemExit('Fingerprint values must be lowercase SHA-256 digests')
    check(repo, fingerprints, args.revision)
    print(json.dumps({'verified': len(fingerprints), 'revision': args.revision, 'fingerprintsSha256': sha(raw)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--revision', required=True, help='exact 40-character release commit')
    parser.add_argument('--output', help='new fingerprint file (freeze mode)')
    parser.add_argument('--verify', help='existing fingerprint file to verify')
    args = parser.parse_args()
    if len(args.revision) != 40 or any(c not in '0123456789abcdef' for c in args.revision):
        parser.error('--revision must be an exact lowercase 40-character commit SHA')
    if bool(args.output) == bool(args.verify):
        parser.error('pass exactly one of --output (freeze) or --verify')
    (verify if args.verify else freeze)(args)


if __name__ == '__main__':
    sys.exit(main())
