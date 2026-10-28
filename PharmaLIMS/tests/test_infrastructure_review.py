"""Executable dispatch-policy evaluation and transport/date wiring regressions."""
import pathlib
import json
import unittest
import yaml

ROOT = pathlib.Path(__file__).resolve().parents[2]


def evaluate(expression, event, ref):
    expression = expression.removeprefix('${{').removesuffix('}}').strip()
    expression = expression.replace('github.event_name', repr(event)).replace('github.ref', repr(ref))
    return eval(expression.replace('&&', ' and ').replace('||', ' or '), {'__builtins__': {}}, {})


class InfrastructureReviewTests(unittest.TestCase):
    def test_dispatch_policy_excludes_untrusted_runner_and_secrets(self):
        for relative in ('.github/workflows/ci.yml', 'PharmaLIMS/.github/workflows/ci.yml'):
            jobs = yaml.safe_load((ROOT / relative).read_text(encoding='utf-8-sig'))['jobs']
            for event, ref in [('pull_request', 'refs/pull/62/merge'), ('push', 'refs/heads/main'),
                               ('push', 'refs/heads/codex-review'), ('workflow_dispatch', 'refs/heads/evil'),
                               ('workflow_dispatch', 'refs/tags/main'), ('workflow_dispatch', 'refs/heads/main')]:
                production = event == 'workflow_dispatch' and ref == 'refs/heads/main'
                for name, job in jobs.items():
                    if not evaluate(job.get('if', 'True'), event, ref):
                        continue
                    runner = str(job['runs-on'])
                    if '${{' in runner:
                        runner = str(evaluate(runner, event, ref))
                    if 'self-hosted' in runner:
                        self.assertTrue(production, (relative, name, event, ref, 'untrusted self-hosted allocation'))
                        self.assertEqual('Production', job.get('environment'))
                    for step in job.get('steps', []):
                        if not evaluate(step.get('if', 'True').replace('always()', 'True'), event, ref):
                            continue
                        if 'secrets.' in str(step):
                            self.assertTrue(production, (relative, name, event, ref, 'untrusted secret injection'))
                            self.assertEqual('Production', job.get('environment'))
            rejection = jobs.get('reject-untrusted-dispatch')
            self.assertIsNotNone(rejection, 'Rejected dispatch must fail visibly on a hosted runner')
            self.assertNotIn('checkout', str(rejection))

    def test_transport_build_cannot_contaminate_source_delivery(self):
        for relative in ('.github/workflows/ci.yml', 'PharmaLIMS/.github/workflows/ci.yml'):
            jobs = yaml.safe_load((ROOT / relative).read_text(encoding='utf-8-sig'))['jobs']
            steps = jobs['validate-build-publish']['steps']
            regression = next(s for s in steps if s.get('name') == 'Infrastructure caller transport and Gregorian report regressions')
            command = regression['run']
            self.assertIn('$env:RUNNER_TEMP', command)
            self.assertIn('-p:ArtifactsPath=', command)
            self.assertIn('dotnet build', command)

    def test_site_source_manifest_includes_new_transport_and_date_dependencies(self):
        manifest = json.loads((ROOT / 'PharmaLIMS/scripts/Eq001-AmeenSourcePrerequisites.json').read_text())
        sources = {entry['SourcePath'] for entry in manifest['Files'] if not entry.get('Remove')}
        for dependency in ('tools/PharmaLIMS.TrustedSigning/TrustedPipeConnectionPool.cs',
                           'Infrastructure/PreflightTimestamp.cs'):
            self.assertIn('PharmaLIMS/' + dependency, sources)

    def test_date_output_uses_one_invariant_formatter(self):
        ui = (ROOT / 'PharmaLIMS/SystemPreflight.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('ExecutedAt:yyyy', ui)
        self.assertGreaterEqual(ui.count('ExecutedAtDisplay'), 2)
        service = (ROOT / 'PharmaLIMS/Infrastructure/SystemPreflightService.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ExecutedAtDisplay => PreflightTimestamp.Format(ExecutedAt)', service)
        overlay = (ROOT / 'PharmaLIMS/scripts/site-overlays/AMEEN-SystemPreflightService.cs.txt').read_text(encoding='utf-8-sig')
        self.assertIn('ExecutedAtDisplay => PreflightTimestamp.Format(ExecutedAt)', overlay)

    def test_pipe_preserves_authorization_and_first_instance(self):
        source = (ROOT / 'PharmaLIMS/tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs').read_text()
        self.assertNotIn('WellKnownSidType.AuthenticatedUserSid', source)
        self.assertIn('_callers.AllowedWindowsSids', source)
        self.assertIn('_callers.IsAllowedCaller(callerSid, serviceSid)', source)
        self.assertIn('PipeOptions.FirstPipeInstance', source)
        self.assertIn('TrustedPipeConnectionPool.RunAsync', source)
        self.assertIn('SslProtocols.Tls12 | SslProtocols.Tls13', source)
        authentication = source.split('if (envelope.Authentication is not null)', 1)[1].split('Guid requestId', 1)[0]
        self.assertIn('_callers.IsAllowedCaller(callerSid, serviceSid)', authentication)
        self.assertNotIn('_callers.Resolve(', authentication, 'Login ticket usernames must remain independent of native account mapping')

if __name__ == '__main__':
    unittest.main()
