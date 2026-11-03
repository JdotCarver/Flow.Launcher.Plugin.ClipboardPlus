# test_whisper_source_diagnostic.py
"""Exercise the privacy-safe WhisperWriter structural inspection on synthetic code."""
import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('inspect_whisper_writer', ROOT / 'tools/diagnostics/inspect_whisper_writer.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class WhisperSourceDiagnosticTests(unittest.TestCase):
    def test_sequence_guard_and_clipboard_operations_are_detected(self):
        result = MODULE.inspect_source('''
def _paste_with_clipboard_preservation():
    saved = clipboard.GetClipboardSequenceNumber()
    clipboard.EmptyClipboard()
    clipboard.SetClipboardText("synthetic")
    sleep(0.1)
    if clipboard.GetClipboardSequenceNumber() == saved:
        clipboard.SetClipboardData(1, b"synthetic")
''')
        for key in ('paste_function', 'paste_sequence_call', 'paste_sequence_guard',
                    'paste_restores_formats', 'paste_writes_text', 'paste_empties_clipboard',
                    'paste_waits', 'paste_100ms_constant'):
            self.assertTrue(result[key], key)
        self.assertFalse(result['paste_enumerates_formats'])

    def test_absent_function_has_only_a_fixed_status(self):
        self.assertEqual({'paste_function': False}, MODULE.inspect_source('value = 1'))


if __name__ == '__main__':
    unittest.main()
