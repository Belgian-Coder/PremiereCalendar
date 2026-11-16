"""Native review inventory regression; no SDK, services or external repositories."""
import sys
sys.dont_write_bytecode = True
import hashlib, json, os, pathlib, runpy, shutil, stat, subprocess, tempfile, unittest

REPO = pathlib.Path(__file__).resolve().parents[1]
FOLDER = REPO / 'artifacts/review-input-tests'
FOLDER.mkdir(parents=True, exist_ok=True)
FREEZER = REPO / 'eng/release/freeze_review_inputs.py'
FREEZER_API = runpy.run_path(str(FREEZER), run_name='review_freezer_test')
VERIFIER = runpy.run_path(str(REPO / 'eng/release/verify_reviewed_source.py'), run_name='review_binding_test')


class ReviewInputs(unittest.TestCase):
    def test_nul_records_preserve_unicode_spaces_and_shared_blobs(self):
        oid = b'1' * 40
        rows = b'100644 blob ' + oid + b'\tsrc/space name.txt\0' + b'100755 blob ' + oid + b'\tsrc/\xc3\xa9 executable.sh\0'
        self.assertEqual(FREEZER_API['tree_blobs'](rows), {'src/space name.txt': oid.decode(), 'src/é executable.sh': oid.decode()})

    def test_malformed_or_unsupported_tree_records_refused(self):
        valid = b'100644 blob ' + b'1' * 40 + b'\tsrc/A.txt\0'
        for bad in (b'missing-tab\0', b'100644 blob\tsrc/A.txt\0',
                    b'100644 blob invalid-oid\tsrc/A.txt\0', b'160000 commit ' + b'1' * 40 + b'\tsubmodule\0',
                    b'120000 blob ' + b'1' * 40 + b'\tlink\0', b'100644 blob ' + b'1' * 40 + b'\t\0', valid + valid):
            with self.subTest(record=bad):
                with self.assertRaises((SystemExit, ValueError)):
                    FREEZER_API['tree_blobs'](bad)

    def test_non_utf8_tree_name_refused(self):
        with self.assertRaises(UnicodeDecodeError):
            FREEZER_API['tree_blobs'](b'100644 blob ' + b'1' * 40 + b'\tsrc/\xff.txt\0')

    def test_large_inventory_preserves_exact_review_binding_and_refuses_overwrite(self):
        root = pathlib.Path(tempfile.mkdtemp(prefix='fixture-', dir=FOLDER))
        self.assertTrue(root.resolve().is_relative_to(FOLDER.resolve()))
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith('GIT_')}
        env.update(GIT_OPTIONAL_LOCKS='0', GIT_TERMINAL_PROMPT='0')

        def git(*args):
            return subprocess.check_output(['git', '-c', 'core.hooksPath=/dev/null', '-c', 'gc.auto=0',
                '-c', 'maintenance.auto=false', '-c', 'commit.gpgsign=false', '-c', 'user.name=Fixture',
                '-c', 'user.email=fixture@example.invalid', '-C', str(root), *args], env=env, stderr=subprocess.PIPE, timeout=20)

        def freeze():
            return subprocess.run([sys.executable, '-I', '-B', str(FREEZER), '--repo', str(root),
                '--bundle', str(bundle), '--base', base, '--revision', revision],
                env=env, capture_output=True, text=True, encoding='utf-8', timeout=20)

        try:
            git('init', '--quiet')
            (root / 'README.md').write_bytes(b'base\n')
            git('add', '--', 'README.md'); git('commit', '--quiet', '-m', 'base')
            base = git('rev-parse', 'HEAD').decode().strip()
            (root / 'src').mkdir()
            bodies = {'README.md': b'base\n', "src/é O'Brien $(NEVER).txt": b'unicode path\n'}
            for i in range(400):
                bodies[f'src/{i:04d}-' + 'long-source-name-' * 4 + '.txt'] = b'shared blob\n'
            for name, body in bodies.items():
                (root / name).write_bytes(body)
            git('add', '--', 'src'); git('commit', '--quiet', '-m', 'large inventory')
            revision = git('rev-parse', 'HEAD').decode().strip()
            old_argv = ['git', '-C', str(root), 'rev-parse', *[f'{revision}:{n}' for n in sorted(bodies)]]
            self.assertGreater(len(subprocess.list2cmdline(old_argv)), 32767)
            bundle = root / 'bundle'; bundle.mkdir()
            result = freeze()
            self.assertEqual(result.returncode, 0, result.stderr)
            manifest_path = bundle / 'review-inputs-manifest.json'
            manifest = json.loads(manifest_path.read_text())
            expected = {n: hashlib.sha1(b'blob ' + str(len(b)).encode() + b'\0' + b).hexdigest() for n, b in sorted(bodies.items())}
            self.assertEqual(manifest['repoInventory'], sorted(bodies))
            self.assertEqual(manifest['repositoryBlobIds'], expected)
            patch = (bundle / 'review-source.patch').read_bytes()
            self.assertEqual(patch, git('diff', '--no-ext-diff', '--no-textconv', '--no-color', base, revision, '--'))
            review = {'sourceRevision': revision, 'baseCommit': base,
                'inputManifestSha256': hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
                'reviewedDiffSha256': hashlib.sha256(patch).hexdigest()}
            (bundle / 'source-review.json').write_text(json.dumps(review))
            self.assertTrue(VERIFIER['validate'](root, bundle, revision)['allChecksPass'])
            original = manifest_path.read_bytes()
            self.assertNotEqual(freeze().returncode, 0)
            self.assertEqual(manifest_path.read_bytes(), original)
            self.assertEqual((bundle / 'review-source.patch').read_bytes(), patch)
            manifest['repositoryBlobIds']['README.md'] = '0' * 40
            manifest_path.write_text(json.dumps(manifest))
            review['inputManifestSha256'] = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
            (bundle / 'source-review.json').write_text(json.dumps(review))
            with self.assertRaisesRegex(VERIFIER['BindingError'], 'Committed inventory/blob IDs differ'):
                VERIFIER['validate'](root, bundle, revision)
        finally:
            def remove_owned_readonly(function, path, error):
                self.assertTrue(pathlib.Path(path).resolve().is_relative_to(root.resolve()))
                if not isinstance(error[1], PermissionError):
                    raise error[1]
                os.chmod(path, stat.S_IREAD | stat.S_IWRITE)
                function(path)
            self.assertTrue(root.resolve().is_relative_to(FOLDER.resolve()))
            shutil.rmtree(root, onerror=remove_owned_readonly)


if __name__ == '__main__':
    unittest.main()
