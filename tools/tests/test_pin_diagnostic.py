# test_pin_diagnostic.py
"""Privacy checks for the local-only PowerShell pin diagnostic."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'tools/diagnostics/Inspect-WindowsPinMetadata.ps1'


class PinDiagnosticPrivacyTests(unittest.TestCase):
    def test_actions_are_rejected_and_output_is_limited_to_counts(self):
        source = SCRIPT.read_text(encoding='utf-8')
        self.assertIn("$env:GITHUB_ACTIONS -eq 'true'", source)
        self.assertIn('must be run locally', source)
        output = [line.strip() for line in source.splitlines() if line.strip().startswith('Write-Host')]
        self.assertTrue(output)
        for line in output:
            self.assertNotIn('$PinnedFolder', line)
            self.assertNotIn('$entry', line)
            self.assertNotIn('$metadata', line.replace('$metadataPresent', '').replace('$metadataMissing', '')
                             .replace('$metadataUnreadable', '').replace('$metadataMalformed', '')
                             .replace('$metadataUnexpected', ''))
            self.assertNotIn('$raw', line)
            self.assertNotIn('$document', line)


if __name__ == '__main__':
    unittest.main()
