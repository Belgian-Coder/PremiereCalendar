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

SUFFIXES = {'.cs', '.razor', '.json', '.csproj', '.props', '.targets', '.sql', '.resx', '.css', '.js', '.ps1', '.py'}
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


def check(repo, fingerprints, revision):
    problems = []
    for name, want in fingerprints.items():
        path = repo / name
        if not path.is_file():
            problems.append(f'missing: {name}')
            continue
        data = path.read_bytes()
        if sha(data) != want:
            problems.append(f'changed since fingerprinting: {name}')
        if revision and name.endswith('.cs'):
            committed = git(repo, 'show', f'{revision}:{name}', check=False)
            if committed != data:
                problems.append(f'differs from the committed blob: {name}')
    if revision:
        committed_cs = {n for n in git(repo, 'ls-tree', '-r', '-z', '--name-only', revision).decode('utf-8').split('\0') if n.endswith('.cs')}
        tested_cs = {n for n in fingerprints if n.endswith('.cs')}
        if committed_cs != tested_cs:
            problems.append(f'C# inventory differs: {len(committed_cs ^ tested_cs)} file(s) are committed but untested or vice versa')
    if problems:
        raise SystemExit('Tested-source verification failed:\n  ' + '\n  '.join(problems[:30]))


def verify(args):
    repo = pathlib.Path(args.repo).resolve()
    raw = pathlib.Path(args.verify).read_bytes()
    fingerprints = json.loads(raw.decode('utf-8-sig'))
    if not isinstance(fingerprints, dict) or not fingerprints:
        raise SystemExit('The fingerprint file must be a non-empty object')
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
