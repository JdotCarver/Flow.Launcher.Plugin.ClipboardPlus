# test_settings_panel.py
"""Static checks for settings layout and localized tooltip coverage."""
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PANEL = ROOT / 'src/Flow.Launcher.Plugin.ClipboardPlus.Panels/Views/SettingsPanel.xaml'
LANGUAGES = ROOT / 'src/Flow.Launcher.Plugin.ClipboardPlus/Languages'


class SettingsPanelTests(unittest.TestCase):
    def setUp(self):
        self.panel = ET.parse(PANEL).getroot()
        self.controls = list(self.panel.iter())

    def control_with_binding(self, binding):
        pattern = re.compile(r'\{Binding\s+' + re.escape(binding) + r'(?:[,}\s])')
        return next(control for control in self.controls
                    if any(pattern.search(value) for value in control.attrib.values()))

    def test_reordered_settings(self):
        def row(binding):
            return int(self.control_with_binding(binding).attrib['Grid.Row'])
        self.assertEqual(row('AllRecordOrders') + 1, row('UseRelativeTime'))
        self.assertLess(row('KeepFiles'), row('AutomaticCleanup'))
        expiry = self.control_with_binding('ProtectPinsFromExpiry')
        self.assertEqual('{DynamicResource flowlauncher_plugin_clipboardplus_tip_pin_expiry}', expiry.attrib['ToolTip'])
        self.assertEqual('{Binding RetentionSettingEnabled}', expiry.attrib['IsEnabled'])
        self.assertTrue(any('RetentionError' in str(control.attrib) for control in self.controls))
        self.assertLess(row('AutomaticCleanup'), row('AutomaticCleanupThorough'))
        self.assertLess(row('AutomaticCleanupThorough'), row('CleanupCommand'))
        self.assertLess(row('CleanupCommand'), row('WindowsSuppressionLifetimeIndex'))
        self.assertGreater(row('RestoreToDefaultCommand'), row('WindowsSuppressionLifetimeIndex'))
        self.assertFalse(any('CleanupKeyword' in str(control.attrib) for control in self.controls))

    def test_tooltips_describe_the_bound_control(self):
        for binding in ('AllTextKeepTimes', 'AllImagesKeepTimes', 'AllFilesKeepTimes'):
            self.assertEqual('{DynamicResource flowlauncher_plugin_clipboardplus_tip_retention}',
                             self.control_with_binding(binding).attrib['ToolTip'])
        self.assertEqual('{DynamicResource flowlauncher_plugin_clipboardplus_tip_thorough}',
                         self.control_with_binding('AutomaticCleanupThorough').attrib['ToolTip'])

    def test_tooltips_exist_in_every_language_and_work_when_disabled(self):
        keys = set()
        for control in self.controls:
            tip = control.attrib.get('ToolTip', '')
            if not tip:
                continue
            match = re.fullmatch(r'\{DynamicResource (flowlauncher_plugin_clipboardplus_tip_\w+)\}', tip)
            self.assertIsNotNone(match)
            self.assertEqual('True', control.attrib.get('ToolTipService.ShowOnDisabled'))
            keys.add(match.group(1))
        self.assertGreaterEqual(len(keys), 25)
        for language in ('en', 'fr', 'zh-cn', 'zh-tw'):
            resources = ET.parse(LANGUAGES / (language + '.xaml')).getroot()
            available = {item.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Key')
                         for item in resources if item.text and item.text.strip()}
            self.assertFalse(keys - available, language)


if __name__ == '__main__':
    unittest.main()
