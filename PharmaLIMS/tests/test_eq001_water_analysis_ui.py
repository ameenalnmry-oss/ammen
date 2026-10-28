"""Portable source/wiring checks; these do not execute WPF or Windows DPAPI."""
import re
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
SITE = Path('/workspace/.cloud-setup/water-analysis/site-source')

def method(source, name):
    match = re.search(r'private\s+(?:async\s+)?(?:void|Task[^ ]*|bool|string)\s+' + re.escape(name) + r'\([^\n]*\)\s*\{', source)
    if not match:
        raise AssertionError('Missing method ' + name)
    start = match.end()
    depth = 1
    for pos in range(start, len(source)):
        depth += (source[pos] == '{') - (source[pos] == '}')
        if not depth:
            return source[start:pos]
    raise AssertionError('Unclosed method ' + name)

class TrustedWaterUiContract(unittest.TestCase):
    def test_six_handlers_route_before_legacy_work(self):
        source = (ROOT/'ResultsEntry.xaml.cs').read_text(encoding='utf-8-sig')
        for handler, operation in [('StartAnalysis','StartAnalysis'),('SaveResults','SaveResults'),('SubmitReview','SubmitReview'),('Review','Review'),('Approve','Approve'),('CorrectTimes','CorrectTimes')]:
            with self.subTest(handler=handler):
                body = method(source, 'Btn'+handler+'_Click').lstrip()
                self.assertTrue(body.startswith('if (AppConfig.TrustedSigningEnabled)'), 'Trusted '+operation+' still reaches local SQL')
                self.assertIn('await RunTrustedWaterWorkflowAsync("'+operation+'")', body)
                self.assertRegex(body, r'RunTrustedWaterWorkflowAsync\("'+operation+r'"\);\s*return;')

    def test_disabled_trusted_transport_retains_original_handler_bodies(self):
        if not SITE.exists(): self.skipTest('Installed source snapshot unavailable')
        source=(ROOT/'ResultsEntry.xaml.cs').read_text(encoding='utf-8-sig')
        original=(SITE/'ResultsEntry.xaml.cs').read_text(encoding='utf-8-sig')
        for handler in ['StartAnalysis','SaveResults','SubmitReview','Review','Approve','CorrectTimes']:
            body=method(source,'Btn'+handler+'_Click')
            body=re.sub(r'^\s*if \(AppConfig.TrustedSigningEnabled\)\s*\{\s*await RunTrustedWaterWorkflowAsync\("'+handler+r'"\);\s*return;\s*\}', '', body, count=1)
            self.assertEqual(method(original,'Btn'+handler+'_Click').strip(),body.strip(),handler+' legacy body changed')

    def test_pending_recovery_is_durable_and_keeps_receipt_identity(self):
        path = ROOT/'Services/WaterWorkflowRecovery.cs'
        self.assertTrue(path.exists(), 'Trusted analysis has no durable pending request')
        source = path.read_text()
        for text in ['DataProtectionScope.CurrentUser','FileOptions.WriteThrough','Flush(true)','File.Move(temp, path, false)','OriginalReason','OriginalMeaning','CanonicalPayload','FileMode.CreateNew']:
            self.assertIn(text, source)
        self.assertIn('state is not ("Committed" or "Cancelled")', source)

    def test_signed_payload_is_captured_before_signature_and_verified_after(self):
        path = ROOT/'ResultsEntry.TrustedWaterWorkflow.cs'
        self.assertTrue(path.exists(), 'Missing immutable trusted analysis adapter')
        source = path.read_text()
        self.assertLess(source.index('CaptureTrustedWaterRequest('),source.index('signature.ShowDialog()'))
        self.assertIn('EnsureTrustedWaterUiUnchanged(', source)
        self.assertIn('WaterWorkflowCancel', source)
        self.assertIn('pending.OriginalReason, pending.OriginalMeaning', source)
        self.assertIn('combo.IsEnabled = false', source)
        self.assertNotIn('SyncWaterPlanStatus', source)
        self.assertNotIn('EnsureGlobalWaterDateColumns', source)

    def test_load_hash_brackets_visible_grid_load(self):
        source = (ROOT/'ResultsEntry.xaml.cs').read_text(encoding='utf-8-sig')
        body = method(source, 'LoadSampleAsync')
        self.assertLess(body.index('CaptureTrustedWaterLoadSnapshot'),body.index('string query'))
        self.assertLess(body.index('await LoadTestsForSample'),body.index('FinishTrustedWaterLoadSnapshot'))

    def test_oversized_transport_is_rejected_before_creating_pending_identity(self):
        adapter=(ROOT/'ResultsEntry.TrustedWaterWorkflow.cs').read_text()
        self.assertIn('RequireWaterWorkflowFrame',adapter)
        self.assertLess(adapter.index('RequireWaterWorkflowFrame'),adapter.index('recovery.SaveNew'))
        session=(ROOT/'Services/TrustedApplicationSession.cs').read_text()
        self.assertIn('TrustedPipeProtocol.MaximumFrameBytes',session)
        self.assertNotIn('Substring(0,',session)

    def test_recovery_remains_reachable_after_committed_state_changes(self):
        xaml=(ROOT/'ResultsEntry.xaml').read_text()
        self.assertIn('Click="BtnRecoverWaterWorkflow_Click"',xaml)
        adapter=(ROOT/'ResultsEntry.TrustedWaterWorkflow.cs').read_text()
        self.assertIn('UpdateTrustedWaterRecoveryButton',adapter)
        self.assertIn('"RecoverOnly"',adapter)

    def test_actual_site_guards_and_legacy_reissue_are_preserved(self):
        main=(ROOT/'ResultsEntry.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(statusBeforeSave)',main)
        self.assertIn('EnsureWaterVisibleEvidenceSavedAtLoad()',main)
        for name in ['ResultsEntry.xaml.Part3.cs','ResultsEntry.xaml.Part5.cs','ResultsEntry.xaml.Part6.cs']:
            self.assertTrue((ROOT/name).exists(), 'Missing installed partial '+name)
            if SITE.exists():
                self.assertEqual((SITE/name).read_text(),(ROOT/name).read_text(), 'Installed clinical guard was replaced: '+name)

if __name__ == '__main__':
    unittest.main()
