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
        self.assertLess(row('CleanupCommand'), row('CleanupFeedback'))
        self.assertLess(row('CleanupFeedback'), row('AutomaticCleanup'))
        self.assertLess(row('AutomaticCleanup'), row('AutomaticCleanupVerificationIndex'))
        self.assertLess(row('AutomaticCleanupVerificationIndex'), row('WindowsSuppressionLifetimeIndex'))
        feedback = self.control_with_binding('CleanupFeedback')
        triggers = [node for node in feedback.iter() if node.tag.endswith('DataTrigger')]
        self.assertEqual(1, len(triggers))
        self.assertEqual('{Binding CleanupFeedback, Mode=OneWay}', triggers[0].attrib['Binding'])
        self.assertEqual('', triggers[0].attrib['Value'])
        self.assertTrue(any(node.attrib.get('Property') == 'Visibility' and node.attrib.get('Value') == 'Collapsed'
                            for node in triggers[0].iter()))
        expanders = [control for control in self.controls if control.tag.endswith('Expander')]
        self.assertEqual(6, len(expanders))
        self.assertEqual('True', expanders[0].attrib['IsExpanded'])
        self.assertTrue(all(e.attrib['IsExpanded'] == 'False' for e in expanders[1:]))
        self.assertEqual('{Binding AutomaticCleanup, Mode=OneWay}',
                         self.control_with_binding('AutomaticCleanupVerificationIndex').attrib['IsEnabled'])
        self.assertFalse(any('CleanupKeyword' in str(control.attrib) for control in self.controls))

    def test_verification_label_and_manual_cleanup_tooltip(self):
        label = next(control for control in self.controls
                     if control.attrib.get('Text') == '{DynamicResource flowlauncher_plugin_clipboardplus_verification_label}')
        dropdown = self.control_with_binding('AutomaticCleanupVerificationIndex')
        self.assertEqual(label.attrib['Grid.Row'], dropdown.attrib['Grid.Row'])
        self.assertEqual('0', label.attrib['Grid.Column'])
        self.assertEqual('1', dropdown.attrib['Grid.Column'])
        self.assertEqual('Center', label.attrib['VerticalAlignment'])
        self.assertEqual('{StaticResource SettingPanelItemTopBottomMargin}', label.attrib['Margin'])
        self.assertEqual('{StaticResource SettingPanelItemLeftTopBottomMargin}', dropdown.attrib['Margin'])
        key = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
        for language in ('en', 'fr', 'zh-cn', 'zh-tw'):
            resources = ET.parse(LANGUAGES / (language + '.xaml')).getroot()
            values = {item.attrib.get(key): item.text for item in resources}
            self.assertTrue(values.get('flowlauncher_plugin_clipboardplus_verification_label'), language)
            self.assertTrue(values.get('flowlauncher_plugin_clipboardplus_tip_thorough'), language)
        self.assertIn('Manual cleanup always uses thorough verification',
                      {item.attrib.get(key): item.text for item in ET.parse(LANGUAGES / 'en.xaml').getroot()}
                      ['flowlauncher_plugin_clipboardplus_tip_thorough'])

    def test_explorer_style_full_width_accordion(self):
        x = '{http://schemas.microsoft.com/winfx/2006/xaml}'
        styles = [control for control in self.controls
                  if control.tag.endswith('Style') and control.attrib.get(x + 'Key') == 'ClipboardPlusExpanderStyle']
        self.assertEqual(1, len(styles))
        style = styles[0]
        setters = {node.attrib.get('Property'): node.attrib.get('Value')
                   for node in style if node.tag.endswith('Setter')}
        self.assertEqual('0 0 0 1', setters['BorderThickness'])
        self.assertEqual('-8 18 24 14', setters['Padding'])
        header = next(node for node in style.iter() if node.attrib.get(x + 'Name') == 'HeaderSite')
        self.assertEqual('{StaticResource ExpanderHeaderRightArrowStyle}', header.attrib['Style'])
        self.assertEqual('{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}',
                         header.attrib['IsChecked'])
        container = next(node for node in self.controls if node.attrib.get(x + 'Name') == 'ExpanderContainer')
        self.assertEqual('-2 0 -2 0', container.attrib['Margin'])
        sections = [node for node in container if node.tag.endswith('Expander')]
        self.assertEqual(6, len(sections))
        for section in sections:
            self.assertEqual('{StaticResource ClipboardPlusExpanderStyle}', section.attrib['Style'])
            self.assertEqual('Expander_Expanded', section.attrib['Expanded'])
            grid = next(node for node in section if node.tag.endswith('Grid'))
            self.assertEqual('32 -10 0 0', grid.attrib['Margin'])
        source = (PANEL.with_suffix('.xaml.cs')).read_text(encoding='utf-8-sig')
        self.assertIn('ExpanderContainer.Children.OfType<Expander>()', source)
        self.assertIn('other.IsExpanded = false', source)

    def test_tooltips_describe_the_bound_control(self):
        for binding in ('AllTextKeepTimes', 'AllImagesKeepTimes', 'AllFilesKeepTimes'):
            self.assertEqual('{DynamicResource flowlauncher_plugin_clipboardplus_tip_retention}',
                             self.control_with_binding(binding).attrib['ToolTip'])
        self.assertEqual('{DynamicResource flowlauncher_plugin_clipboardplus_tip_thorough}',
                         self.control_with_binding('AutomaticCleanupVerificationIndex').attrib['ToolTip'])

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
