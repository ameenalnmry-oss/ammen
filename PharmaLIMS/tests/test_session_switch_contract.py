"""Cross-file handoff safeguards; native CredUI/profile acceptance still requires Windows."""
import json
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def source(name): return (ROOT/name).read_text(encoding='utf-8-sig')
class SessionSwitchContractTests(unittest.TestCase):
    def test_child_must_finish_startup_before_ready_login_title(self):
        app=source('App.xaml.cs')
        self.assertGreater(app.index('loginWindow.SetStartupBusy(false)'),app.index('StartSessionTimeoutMonitor();'))
        login=source('Login.xaml.cs')
        self.assertIn('Title = SessionTransitionCoordinator.LoginTitle(isBusy);',login)
        self.assertIn('SessionTransitionCoordinator.CanFinishHandoff',source('Services/WindowsUserSwitchService.cs'))
    def test_effective_connection_target_is_checked_on_both_sides(self):
        self.assertIn('ReadTarget(AppConfig.ConnectionString)',source('Services/WindowsUserSwitchService.cs'))
        self.assertIn('ReadTarget(connectionString)',source('App.xaml.cs'))
    def test_pending_prm_open_blocks_manual_logout_and_ignores_closed_session(self):
        main=source('MainWindow.xaml.cs')
        gate=main.split('internal bool CanEndInteractiveSession()',1)[1].split('private async void BtnSessionAction_Click',1)[0]
        self.assertIn('!_workflowOpenRunning',gate)
        handler=main.split('private async void BtnPRMResults_Click',1)[1].split('private void BtnReports_Click',1)[0]
        self.assertLess(handler.index('_workflowOpenRunning = true'),handler.index('await PrmSchemaReadinessService'))
        self.assertLess(handler.index('if (_sessionClosed) return;',handler.index('await PrmSchemaReadinessService')),handler.index('ShowWorkspaceWindow'))
        self.assertIn('finally { _workflowOpenRunning = false; }',handler)
    def test_profile_bootstrap_precedes_any_configuration_connection(self):
        app=source('App.xaml.cs')
        self.assertLess(app.index('PrepareSwitchedProcess(e.Args)'),app.index('AppConfig.ConnectionString'))
    def test_desktop_manifest_changes_only_reviewed_ui_and_helpers(self):
        m=json.loads(source('scripts/Eq001-SessionSourcePrerequisites.json'))
        self.assertEqual({'App.xaml.cs','Login.xaml','Login.xaml.cs','MainWindow.xaml','MainWindow.xaml.cs','Services/SessionTransitionCoordinator.cs','Services/SwitchedProcessEnvironment.cs','Services/WindowsUserSwitchService.cs'}, {f['Path'] for f in m['Files']})
        self.assertFalse(any(f['Remove'] for f in m['Files']))

    def test_original_site_dashboard_keeps_controlled_coa_command_and_session_guards(self):
        manifest=json.loads(source('scripts/Eq001-SessionSourcePrerequisites.json'))
        entries={e['Path']:e for e in manifest['Files']}
        def installed(name):
            path=entries[name]['SourcePath'].removeprefix('PharmaLIMS/')
            return source(path)
        xaml=installed('MainWindow.xaml')
        code=installed('MainWindow.xaml.cs')
        self.assertIn('BtnControlledCoaCancellation',xaml)
        self.assertIn('Click="BtnControlledCoaCancellation_Click"',xaml)
        self.assertIn('BtnLockSession',xaml)
        self.assertIn('BtnSwitchUser',xaml)
        self.assertIn('AppConfig.IsDevelopment && Login.CanCancelCOA',code)
        self.assertIn('DatabaseHelper.CancelCertificateControlled(sampleId,',code)
        self.assertIn('signature.SignedBy, reason, signature.Meaning, signature.Reason',code)
        self.assertIn('private async void BtnSessionAction_Click',code)
        custom=code.split('private async void BtnControlledCoaCancellation_Click',1)[1].split('private void BtnSampleManagement_Click',1)[0]
        self.assertIn('_sessionClosed',custom)

    def test_site_build_compiles_both_dashboard_halves_and_verify_only_startup(self):
        script=source('scripts/Test-Eq001SessionSiteBuild.ps1')
        for name in ('AMEEN-App.xaml.cs.txt','AMEEN-MainWindow.xaml.cs.txt','AMEEN-MainWindow.xaml.txt'):
            self.assertIn(name,script)
        self.assertIn('Test-Eq001SessionSiteBuild.ps1',source('scripts/Invoke-Eq001WindowsUat.ps1'))
        app=source('scripts/site-overlays/AMEEN-App.xaml.cs.txt')
        development=app.split('if (AppConfig.IsDevelopment)',1)[1].split('                else',1)[0]
        self.assertIn('VerifyControlledMigrationAsync',development)
        self.assertNotIn('ApplyControlledMigrationAsync',development)

    def test_empty_native_return_cancels_before_logon_without_required_field_exception(self):
        service=source('Services/WindowsUserSwitchService.cs')
        self.assertIn('SessionTransitionCoordinator.ShouldCancelWindowsSwitch',service)
        self.assertLess(service.index('ShouldCancelWindowsSwitch'),service.index('if (!LogonUser'))
        self.assertNotIn('throw new InvalidOperationException("Enter a Windows account and its Windows password.")',service)
    def test_switch_clears_stale_inline_error_before_opening_native_prompt(self):
        login=source('Login.xaml.cs').split('internal async Task SwitchWindowsUserAsync()',1)[1]
        prompt=login.index('await WindowsUserSwitchService.SwitchAsync')
        self.assertLess(login.index('lblError.Text = string.Empty;'),prompt)
        self.assertLess(login.index('lblError.Visibility = Visibility.Collapsed;'),prompt)
