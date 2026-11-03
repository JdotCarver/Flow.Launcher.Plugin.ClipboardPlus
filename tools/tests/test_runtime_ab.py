# test_runtime_ab.py
"""Check the scratch-only runtime A/B seam without accessing clipboard data."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class RuntimeAbTests(unittest.TestCase):
    def test_gc_candidate_transforms_exactly_one_method(self):
        source = (ROOT / 'src/Flow.Launcher.Plugin.ClipboardPlus/Main.cs').read_text(encoding='utf-8-sig')
        pattern = (r'private static void GarbageCollect\(\)\s*\{\s*GC\.Collect\(\);'
                   r'\s*GC\.WaitForPendingFinalizers\(\);\s*GC\.Collect\(\);\s*\}')
        result, count = re.subn(pattern, 'private static void GarbageCollect() { }', source, flags=re.S)
        self.assertEqual(1, count)
        self.assertIn('private static void GarbageCollect() { }', result)
        self.assertNotEqual(source, result)

    def test_gc_comparison_exercises_capture_and_pin_paths(self):
        benchmark = (ROOT / 'tools/performance-baseline/RuntimeAbBenchmark.cs').read_text(encoding='utf-8')
        for token in ('ClipboardMonitor_OnClipboardChanged', 'PinRecordAsync', 'KeepText = true',
                      'WaitTaskQueueAsync', 'SELECT COUNT(*) FROM record'):
            self.assertIn(token, benchmark)

    def test_workflow_runs_only_synthetic_entry_points_in_scratch(self):
        workflow = (ROOT / '.github/workflows/runner-request.yml').read_text(encoding='utf-8')
        step = workflow.split('      - name: Compare synthetic runtime candidates', 1)[1].split('      - name:', 1)[0]
        for token in ('git archive', '$scratch\\candidate', '--gc-ab-only', '--startup-image-$variant',
                      'before', 'metadata', 'Remove-Item $scratch'):
            self.assertIn(token, step)
        self.assertNotIn('--local-pin-probe', step)


if __name__ == '__main__':
    unittest.main()
