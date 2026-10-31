import base64
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'scripts/site_cohort_build.py'
if not MODULE.exists():
    MODULE = Path(__file__).with_name('site_cohort_build.py')
spec = importlib.util.spec_from_file_location('site_cohort_build', MODULE)
cohort = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cohort)


class SiteCohortBuildSafety(unittest.TestCase):
    def test_paths_cannot_escape_or_alias_reserved_build_directories(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for relative in ['../escape.cs', '/absolute.cs', 'C:/drive.cs', 'safe/../../escape.cs',
                             '.git/config', 'BIN/unsafe.cs', 'safe/OBJ/unsafe.cs', 'a/./b.cs',
                             'a//b.cs', 'a./b.cs', 'path\\file.cs']:
                with self.subTest(path=relative), self.assertRaises(ValueError):
                    cohort.contained(root, relative)

    def test_bad_hash_and_windows_alias_are_rejected_before_any_source_write(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            artifact = root / 'scripts/site-cohort-inputs/example.cs.txt'
            artifact.parent.mkdir(parents=True)
            artifact.write_bytes(b'new source\n')
            target = root / 'Example.cs'
            target.write_bytes(b'original source\n')
            entry = {'Path': 'Example.cs', 'Artifact': 'scripts/site-cohort-inputs/example.cs.txt',
                     'Sha256': cohort.digest(b'new source\n')}
            with self.assertRaises(ValueError):
                cohort.validate_inputs(root, [{**entry, 'Sha256': '0'*64}])
            with self.assertRaises(ValueError):
                cohort.validate_inputs(root, [entry, {**entry, 'Path': 'example.CS'}])
            self.assertEqual(b'original source\n', target.read_bytes())

    def test_controlled_sql_bytes_keep_bom_crlf_and_terminal_newlines(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            raw = b'\xef\xbb\xbfSELECT 1;\r\n\r\n'
            artifact = root / 'scripts/site-cohort-inputs/exact.sql.base64.txt'
            artifact.parent.mkdir(parents=True)
            artifact.write_bytes(base64.b64encode(raw)+b'\n')
            entry = {'Path': 'Database/Migrations/example.sql', 'Artifact': 'scripts/site-cohort-inputs/exact.sql.base64.txt',
                     'Encoding': 'base64', 'ExactSha256': cohort.digest(raw),
                     'Sha256': cohort.digest(cohort.normalized(raw, 'example.sql'))}
            prepared = cohort.validate_inputs(root, [entry])
            self.assertEqual(raw, prepared[entry['Path']][1])
            self.assertFalse((root / entry['Path']).exists())

    def test_mutation_entry_point_rejects_a_non_checkout_before_touching_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            target = root / 'Example.cs'
            target.write_bytes(b'original\n')
            with self.assertRaises(RuntimeError):
                cohort.prepare(root, {'Inputs': []})
            self.assertEqual(b'original\n', target.read_bytes())


if __name__ == '__main__':
    unittest.main()
