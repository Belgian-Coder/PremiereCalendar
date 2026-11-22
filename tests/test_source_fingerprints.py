"""Exact-byte source fingerprint and Git batch protocol regressions; no SDK or services."""
import sys
sys.dont_write_bytecode = True
import atexit, concurrent.futures, hashlib, importlib.util, io, json, os, pathlib, shutil, stat, subprocess, tempfile, threading, time, unittest
from unittest.mock import patch
REPO = pathlib.Path(__file__).resolve().parents[1]
FOLDER = REPO / 'artifacts/source-fingerprint-tests'
FOLDER.mkdir(parents=True, exist_ok=True)
CHECKER = REPO / 'eng/release/freeze_test_source.py'
spec = importlib.util.spec_from_file_location('batch_checker', CHECKER)
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)

def git_in(root, *args):
    return subprocess.check_output(['git', '-c', 'core.hooksPath=/dev/null', '-c', 'gc.auto=0', '-c', 'maintenance.auto=false', '-c', 'commit.gpgsign=false', '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', '-C', str(root), *args], stderr=subprocess.PIPE, text=True)


def remove_owned(root):
    """Delete a directory this run created (read-only Git objects included); never anything outside it."""
    root = pathlib.Path(root)
    assert root.resolve().is_relative_to(FOLDER.resolve())

    def remove_owned_readonly(function, path, error):
        assert pathlib.Path(path).resolve().is_relative_to(root.resolve())
        if not isinstance(error[1], PermissionError):
            raise error[1]
        os.chmod(path, stat.S_IREAD | stat.S_IWRITE)
        function(path)
    shutil.rmtree(root, onerror=remove_owned_readonly)


# One real committed repository is built per run (unique directory, removed at exit) and copied for every case:
# five Git processes per case dominated the runtime, while each case still gets its own independent repository.
_template = None
_template_lock = threading.Lock()


def template():
    global _template
    with _template_lock:
        if _template is None:
            root = pathlib.Path(tempfile.mkdtemp(prefix='template-', dir=FOLDER))
            assert root.resolve().is_relative_to(FOLDER.resolve())
            atexit.register(remove_owned, root)
            git_in(root, 'init', '--quiet')
            (root / 'src').mkdir()
            (root / "src/é O'Brien $(NEVER).cs").write_bytes(b'class A {}\n')
            (root / 'src/B.cs').write_bytes(b'class A {}\n')
            (root / 'settings.json').write_text('{}\n')
            git_in(root, 'add', '--', 'src', 'settings.json')
            git_in(root, 'commit', '--quiet', '-m', 'fixture')
            _template = (root, git_in(root, 'rev-parse', 'HEAD').strip())
        return _template


class Functional(unittest.TestCase):

    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp(prefix='fixture-', dir=FOLDER))
        assert self.root.resolve().is_relative_to(FOLDER.resolve())
        source, self.sha = template()
        shutil.copytree(source, self.root, dirs_exist_ok=True)
        self.a = self.root / "src/é O'Brien $(NEVER).cs"
        self.b = self.root / 'src/B.cs'
        self.config = self.root / 'settings.json'
        self.manifest = self.root / 'fingerprints.json'
        self.map = {str(p.relative_to(self.root)).replace('\\', '/'): hashlib.sha256(p.read_bytes()).hexdigest() for p in (self.a, self.b, self.config)}
        self.write()

    def tearDown(self):
        remove_owned(self.root)

    def git(self, *args):
        return git_in(self.root, *args)

    def write(self):
        self.manifest.write_text(json.dumps(self.map), encoding='utf-8')

    def run_checker(self, freeze=False, revision=None):
        return subprocess.run([sys.executable, '-I', '-B', str(CHECKER), '--repo', str(self.root), '--revision', revision or self.sha, *(['--output', str(self.root / 'frozen.json')] if freeze else ['--verify', str(self.manifest)])], capture_output=True, text=True)

    def reject(self):
        p = self.run_checker()
        self.assertNotEqual(p.returncode, 0, p.stdout)

    def test_valid_unicode_spaces_same_blob_multiple_paths(self):
        p = self.run_checker()
        self.assertEqual(p.returncode, 0, p.stderr)
        output = json.loads(p.stdout)
        self.assertEqual(output['verified'], 3)
        self.assertEqual(output['revision'], self.sha)

    def test_freeze_output_identical(self):
        p = self.run_checker(freeze=True)
        self.assertEqual(p.returncode, 0, p.stderr)
        expected = (json.dumps(dict(sorted(self.map.items())), indent=2) + '\n').encode('utf-8')
        self.assertEqual((self.root / 'frozen.json').read_bytes(), expected)

    def test_modified_csharp(self):
        self.a.write_bytes(b'class Changed {}\n')
        self.reject()

    def test_shell_launcher_bytes_frozen_and_verified(self):
        launcher = self.root / 'src/launcher.sh'
        launcher.write_bytes(b'#!/bin/sh\nexit 0\n')
        self.git('add', '--', 'src/launcher.sh')
        self.git('commit', '--quiet', '-m', 'shell fixture')
        self.sha = self.git('rev-parse', 'HEAD').strip()
        p = self.run_checker(freeze=True)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.map = json.loads((self.root / 'frozen.json').read_text())
        self.assertEqual(self.map['src/launcher.sh'], hashlib.sha256(launcher.read_bytes()).hexdigest())
        self.write()
        launcher.write_bytes(b'#!/bin/sh\nexit 1\n')
        self.reject()

    def test_rewritten_hash_still_rejects_uncommitted_blob(self):
        self.a.write_bytes(b'class Changed {}\n')
        self.map[str(self.a.relative_to(self.root)).replace('\\', '/')] = hashlib.sha256(self.a.read_bytes()).hexdigest()
        self.write()
        self.reject()

    def test_missing_file(self):
        self.b.unlink()
        self.reject()

    def test_missing_csharp_fingerprint(self):
        del self.map['src/B.cs']
        self.write()
        self.reject()

    def test_extra_uncommitted_csharp(self):
        extra = self.root / 'src/Extra.cs'
        extra.write_bytes(b'class Extra {}\n')
        self.map['src/Extra.cs'] = hashlib.sha256(extra.read_bytes()).hexdigest()
        self.write()
        self.reject()

    def test_changed_non_csharp(self):
        self.config.write_text('{"changed":true}\n')
        self.reject()

    def test_wrong_revision_freeze_refuses(self):
        self.a.write_bytes(b'class OtherCommit {}\n')
        self.git('add', '--', 'src')
        self.git('commit', '--quiet', '-m', 'changed')
        self.assertNotEqual(self.run_checker(freeze=True).returncode, 0)

    def test_wrong_revision_verify_refuses_rewritten_fingerprints(self):
        self.a.write_bytes(b'class OtherCommit {}\n')
        self.git('add', '--', 'src')
        self.git('commit', '--quiet', '-m', 'changed')
        self.map[str(self.a.relative_to(self.root)).replace('\\', '/')] = hashlib.sha256(self.a.read_bytes()).hexdigest()
        self.write()
        self.reject()

    def test_crlf_freeze_refuses(self):
        self.a.write_bytes(b'class A {}\r\n')
        self.assertNotEqual(self.run_checker(freeze=True).returncode, 0)

    def test_duplicate_json_path_refuses(self):
        raw = json.dumps(self.map)
        self.manifest.write_text(raw[:-1] + ',"src/B.cs":"' + self.map['src/B.cs'] + '"}')
        p = self.run_checker()
        self.assertNotEqual(p.returncode, 0)
        self.assertIn('Duplicate path', p.stderr)

    def test_invalid_hash_type_refuses(self):
        self.map['src/B.cs'] = 42
        self.write()
        self.reject()

    def test_invalid_hash_value_refuses(self):
        self.map['src/B.cs'] = 'z' * 64
        self.write()
        self.reject()

class FakeBatch:

    def __init__(self, payload, rc=0):
        self.stdin = io.BytesIO()
        self.stdout = io.BytesIO(payload)
        self.rc = rc

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.stdin.close()
        self.stdout.close()

    def wait(self):
        return self.rc

class Protocol(unittest.TestCase):
    raw = b'class A { }\n\x00\n'
    oid = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\x00' + raw).hexdigest()

    def tree(self, path=b'src/A.cs', kind=b'blob'):
        return b'100644 ' + kind + b' ' + self.oid.encode() + b'\t' + path + b'\x00'

    def frame(self, raw=None, oid=None, size=None, kind=b'blob', end=b'\n'):
        raw = self.raw if raw is None else raw
        return (oid or self.oid).encode() + b' ' + kind + b' ' + str(len(raw) if size is None else size).encode() + b'\n' + raw + end

    def check(self, payload, tree=None, rc=0):
        with patch.object(checker, 'git', return_value=self.tree() if tree is None else tree), patch.object(checker.subprocess, 'Popen', return_value=FakeBatch(payload, rc)):
            return checker.committed_csharp(pathlib.Path('.'), 'a' * 40)

    def test_binary_declared_size_exact(self):
        self.assertEqual(self.check(self.frame()), {'src/A.cs': self.raw})

    def test_missing_object_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.oid.encode() + b' missing\n')

    def test_truncated_header_refused(self):
        with self.assertRaises(SystemExit):
            self.check(b'')

    def test_truncated_body_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(size=len(self.raw) + 20))

    def test_wrong_frame_newline_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(end=b'X'))

    def test_wrong_object_identity_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(oid='0' * 40))

    def test_wrong_object_type_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(kind=b'commit'))

    def test_invalid_size_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(size='garbage'))

    def test_negative_size_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(size=-1))

    def test_payload_hash_mismatch_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(raw=b'wrong bytes'))

    def test_duplicate_tree_path_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(), tree=self.tree() + self.tree())

    def test_non_blob_tree_entry_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(), tree=self.tree(kind=b'commit'))

    def test_distinct_paths_same_oid_allowed(self):
        self.assertEqual(self.check(self.frame() + self.frame(), tree=self.tree() + self.tree(b'src/B.cs')), {'src/A.cs': self.raw, 'src/B.cs': self.raw})

    def test_batch_failure_refused(self):
        with self.assertRaises(SystemExit):
            self.check(self.frame(), rc=1)


class ConcurrentRunner(unittest.TextTestRunner):
    """Run the independent Functional cases (own repository, subprocess-only) on worker threads.

    Protocol cases patch module attributes of the checker (including the global subprocess module), so they run
    serially on the calling thread before any worker starts; nothing else runs while a patch is active.
    """

    def run(self, test):
        def flatten(suite):
            for item in suite:
                yield from flatten(item) if isinstance(item, unittest.TestSuite) else [item]
        cases = list(flatten(test))
        parallel = [c for c in cases if isinstance(c, Functional)]
        serial = [c for c in cases if not isinstance(c, Functional)]
        result = self._makeResult()
        unittest.registerResult(result)
        result.failfast, result.buffer, result.tb_locals = self.failfast, self.buffer, self.tb_locals
        started = time.perf_counter()
        result.startTestRun()
        try:
            unittest.TestSuite(serial).run(result)
            with concurrent.futures.ThreadPoolExecutor(max_workers=min(6, os.cpu_count() or 2)) as pool:
                futures = [pool.submit(unittest.TestSuite([c]).run, result) for c in parallel]
                for future in futures:
                    future.result()
        finally:
            result.stopTestRun()
        self.stream.writeln()
        result.printErrors()
        self.stream.writeln('-' * 70)
        self.stream.writeln(f'Ran {result.testsRun} tests in {time.perf_counter() - started:.3f}s')
        self.stream.writeln()
        if result.wasSuccessful():
            self.stream.writeln('OK')
        else:
            self.stream.writeln(f'FAILED (failures={len(result.failures)}, errors={len(result.errors)})')
        return result


if __name__ == '__main__':
    unittest.main(testRunner=ConcurrentRunner, verbosity=1)
