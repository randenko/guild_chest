"""Exercise release gates against real disposable Git histories."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from check_release import check_release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git('init', '--initial-branch=main')
        self.git('config', 'user.name', 'Release Test')
        self.git('config', 'user.email', 'release-test@example.invalid')
        self.git('commit', '--allow-empty', '-m', 'Initial commit')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')

    def git(self, *arguments):
        return subprocess.run(['git', *arguments], cwd=self.root, capture_output=True,
                              text=True, check=True).stdout.strip()

    def test_matching_tag_on_main_is_accepted(self):
        self.assertEqual(check_release('v1.2.3', '1.2.3', self.root), '1.2.3')

    def test_older_commit_already_merged_into_main_is_accepted(self):
        release_commit = self.git('rev-parse', 'HEAD')
        self.git('commit', '--allow-empty', '-m', 'Later main commit')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.git('checkout', '--detach', release_commit)
        self.assertEqual(check_release('v1.2.3', '1.2.3', self.root), '1.2.3')

    def test_mismatched_manifest_version_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'does not match manifest version'):
            check_release('v1.2.4', '1.2.3', self.root)

    def test_malformed_and_prerelease_tags_are_rejected(self):
        for tag in ('1.2.3', 'v1.2', 'v1.2.3-beta.1', 'v01.2.3', 'v1.2.3\n'):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, 'form vX.Y.Z'):
                check_release(tag, '1.2.3', self.root)

    def test_unmerged_commit_is_rejected(self):
        self.git('checkout', '-b', 'unmerged-release')
        self.git('commit', '--allow-empty', '-m', 'Unmerged change')
        with self.assertRaisesRegex(ValueError, 'must belong to origin/main'):
            check_release('v1.2.3', '1.2.3', self.root)

    def test_missing_main_reference_is_rejected(self):
        self.git('update-ref', '-d', 'refs/remotes/origin/main')
        with self.assertRaisesRegex(ValueError, 'Cannot verify origin/main'):
            check_release('v1.2.3', '1.2.3', self.root)


if __name__ == '__main__':
    unittest.main()
