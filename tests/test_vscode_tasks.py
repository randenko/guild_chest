"""Check compiler diagnostics resolve to the correct file in VS Code tasks."""
import json
from pathlib import Path
import re
import unittest

TASKS = json.loads((Path(__file__).resolve().parents[1] / '.vscode/tasks.json').read_text())


class TaskMatcherTests(unittest.TestCase):
    def test_compile_test_and_package_tasks_inherit_the_diagnostic_matcher(self):
        for task in TASKS['tasks']:
            if task['label'] not in ('Build Debug', 'Run Tests', 'Package Release'):
                continue
            with self.subTest(task=task['label']):
                matcher = task.get('problemMatcher', TASKS['problemMatcher'])
                self.assertEqual(matcher['base'], '$msCompile')
                self.assertIn('regexp', matcher['pattern'])

    def test_raw_and_nuke_prefixed_errors_and_warnings_keep_their_locations(self):
        pattern = TASKS['problemMatcher']['pattern']
        regex = re.compile(pattern['regexp'])
        samples = [
            ('01:59:40 [ERR] /workspace/My Project/Broken.cs(3,5): error CS1002: ; expected',
             '/workspace/My Project/Broken.cs', '3,5', 'error', 'CS1002', '; expected'),
            ('07:08:09 [DBG] 2>/workspace/Unused.cs(9,2,9,10): warning CS0168: Unused variable',
             '/workspace/Unused.cs', '9,2,9,10', 'warning', 'CS0168', 'Unused variable'),
            ('1>/workspace/Broken.cs(3,5): error CS1002: ; expected',
             '/workspace/Broken.cs', '3,5', 'error', 'CS1002', '; expected'),
            ('/workspace/LineOnly.cs(4): error CS1002: ; expected',
             '/workspace/LineOnly.cs', '4', 'error', 'CS1002', '; expected'),
            (r'18:14:21 [WRN] C:\work\My Project\Build.cs(2,8): warning CS0618: Obsolete member',
             r'C:\work\My Project\Build.cs', '2,8', 'warning', 'CS0618', 'Obsolete member'),
            ('/workspace/build/_build.csproj : error NU1004: Lock file changed',
             '/workspace/build/_build.csproj', None, 'error', 'NU1004', 'Lock file changed'),
        ]
        for diagnostic, filename, location, severity, code, message in samples:
            with self.subTest(diagnostic=diagnostic):
                match = regex.match(diagnostic)
                self.assertIsNotNone(match)
                self.assertEqual(match.group(pattern['file']), filename)
                self.assertEqual(match.group(pattern['location']), location)
                self.assertEqual(match.group(pattern['severity']), severity)
                self.assertEqual(match.group(pattern['code']), code)
                self.assertEqual(match.group(pattern['message']), message)


if __name__ == '__main__':
    unittest.main()
