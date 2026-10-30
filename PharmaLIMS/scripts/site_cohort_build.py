"""Compile-only AMEEN cohort verification in one existing hosted CI checkout.

This tool never installs, publishes, reads protected host configuration or runs SQL.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def normalized(raw, name):
    if name.endswith('.png'):
        return raw
    return (raw.decode('utf-8-sig').lstrip('\ufeff').replace('\r\n', '\n').rstrip('\r\n') + '\n').encode('utf-8')


def contained(root, relative):
    if not isinstance(relative, str) or not re.fullmatch(r'[A-Za-z0-9_./-]+', relative):
        raise ValueError('Invalid cohort path.')
    parts = PurePosixPath(relative).parts
    if not parts or PurePosixPath(relative).as_posix() != relative or relative.startswith('/') or any(p.casefold() in ('.', '..', '.git', 'bin', 'obj') or p.endswith('.') for p in parts):
        raise ValueError('Unsafe cohort path: ' + relative)
    path = root.joinpath(*parts)
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError('Cohort path escapes its checkout.')
    return path


def git_bytes(repository, specification):
    return subprocess.run(['git', '-C', str(repository), 'show', specification], check=True, capture_output=True).stdout


def validate_inputs(project, entries, reader=git_bytes):
    prepared = {}
    seen = set()
    for item in entries:
        target = contained(project, item['Path'])
        key = item['Path'].casefold()
        if key in seen:
            raise ValueError('Duplicate cohort target: ' + item['Path'])
        seen.add(key)
        if 'Artifact' in item:
            source = item['Artifact']
            if not source.startswith(('scripts/site-overlays/', 'scripts/site-cohort-inputs/')) or not source.endswith('.txt'):
                raise ValueError('Source is not an inert controlled artifact.')
            raw = contained(project, source).read_bytes()
        else:
            revision = item['SourceCommit']
            if not re.fullmatch(r'[0-9a-f]{40}', revision):
                raise ValueError('An immutable source commit is required.')
            source = item['SourcePath']
            contained(project.parent, source)
            if not source.startswith('PharmaLIMS/'):
                raise ValueError('Unexpected Git source path.')
            raw = reader(project.parent, revision + ':' + source)
        if item.get('Encoding') == 'base64':
            raw = base64.b64decode(raw.strip(), validate=True)
        value = normalized(raw, item['Path'])
        if not re.fullmatch(r'[0-9a-f]{64}', item['Sha256']) or digest(value) != item['Sha256']:
            raise ValueError('Cohort source hash mismatch: ' + item['Path'])
        if item.get('ExactSha256'):
            if digest(raw) != item['ExactSha256']:
                raise ValueError('Exact database input bytes differ: ' + item['Path'])
            value = raw
        prepared[item['Path']] = (target, value)
    return prepared


def require_hosted_checkout(project):
    if os.name != 'nt' or os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise RuntimeError('Compile qualification can prepare sources only on a GitHub-hosted Windows runner.')
    if os.environ.get('COMPUTERNAME', '').casefold() == 'ameen':
        raise RuntimeError('Original AMEEN must never be changed by the CI build verifier.')
    workspace = os.environ.get('GITHUB_WORKSPACE')
    if not workspace or Path(workspace).resolve() != project.parent.resolve():
        raise RuntimeError('The existing CI checkout is required.')
    actual = subprocess.run(['git', 'rev-parse', '--show-toplevel'], cwd=project.parent, check=True, capture_output=True, text=True).stdout.strip()
    if Path(actual).resolve() != project.parent.resolve():
        raise RuntimeError('Unexpected Git checkout root.')


def compilation_files(project):
    for path in project.rglob('*'):
        if not path.is_file() or path.suffix not in ('.cs', '.xaml'):
            continue
        relative = path.relative_to(project).as_posix()
        if relative.startswith('tests/') or any(p in ('bin', 'obj', '.git') for p in path.relative_to(project).parts):
            continue
        if relative.startswith('tools/') and not relative.startswith(('tools/PharmaLIMS.TrustedSigning/', 'tools/PharmaLIMS.TrustedSigning.Host/')):
            continue
        yield relative, path


def save_report(project, report):
    folder = project / 'artifacts/site-cohort'
    folder.mkdir(parents=True, exist_ok=True)
    (folder / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


def prepare(project, plan):
    require_hosted_checkout(project)
    clean = subprocess.run(['git', 'status', '--porcelain'], cwd=project.parent, check=True, capture_output=True, text=True).stdout
    if clean.strip():
        raise RuntimeError('Dedicated cohort job must start from a clean checkout.')
    # Buffer and validate the entire plan before any source mutation.
    prepared = validate_inputs(project, plan['Inputs'])
    removed = [name for name, _ in compilation_files(project) if name not in prepared]
    if len(prepared) != plan['InputCount'] or len(prepared) < 230:
        raise RuntimeError('Incomplete cohort input plan.')
    for _, (path, value) in prepared.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value)
    for name in removed:
        contained(project, name).unlink()
    verify_materialized(project, plan)
    report = {'ArchiveSha256': plan['ArchiveSha256'], 'InputCount': len(prepared), 'Version': plan['Version'],
              'Commit': os.environ.get('GITHUB_SHA'), 'RemovedExtraCiCompilationInputs': removed,
              'Inputs': [{'Path': x['Path'], 'Sha256': x['Sha256']} for x in plan['Inputs']],
              'MaterializedHashesVerified': True, 'BuildPassed': False, 'LocalWpfUatPassed': False,
              'InstalledOnAmeen': False, 'ProductionActivated': False}
    save_report(project, report)
    print('PASS exact site cohort inputs:', len(prepared), '; excluded extra CI compilation inputs:', len(removed))


def verify_materialized(project, plan):
    for item in plan['Inputs']:
        raw = contained(project, item['Path']).read_bytes()
        if digest(normalized(raw, item['Path'])) != item['Sha256']:
            raise RuntimeError('Materialized cohort drift: ' + item['Path'])
        if item.get('ExactSha256') and digest(raw) != item['ExactSha256']:
            raise RuntimeError('Exact database content drift: ' + item['Path'])
    for name in ['AssemblyInfo.cs', 'PharmaLIMS.csproj']:
        if plan['Version'] not in contained(project, name).read_text(encoding='utf-8'):
            raise RuntimeError('Site version306 was not preserved.')
    manifest = json.loads((project / 'Database/MigrationManifest.json').read_text(encoding='utf-8'))
    migrations = [manifest['freshInstallBaseline']] + manifest['migrations']
    if len(migrations) != plan['MigrationCount']:
        raise RuntimeError('Migration manifest entry count changed.')
    for entry in migrations:
        raw = contained(project, 'Database/' + entry['file']).read_bytes()
        if digest(raw) != entry['sha256'].lower():
            raise RuntimeError('Exact migration checksum differs: ' + entry['file'])
    print('PASS preserved version', plan['Version'], 'and', len(migrations), 'migration checksums.')


def verify_graph(project, plan):
    require_hosted_checkout(project)
    verify_materialized(project, plan)
    inputs = {x['Path'] for x in plan['Inputs']}
    graph = {}
    for prefix, filename in [('', 'PharmaLIMS.csproj'), ('tools/PharmaLIMS.TrustedSigning/', 'PharmaLIMS.TrustedSigning.csproj'),
                             ('tools/PharmaLIMS.TrustedSigning.Host/', 'PharmaLIMS.TrustedSigning.Host.csproj')]:
        result = subprocess.run(['dotnet', 'msbuild', str(contained(project, prefix + filename)), '-nologo',
                                 '-getItem:Compile,Page,ApplicationDefinition'], check=True, capture_output=True, text=True, encoding='utf-8')
        data = json.loads(result.stdout)
        actual = set()
        for category in ['Compile', 'Page', 'ApplicationDefinition']:
            for item in data['Items'].get(category, []):
                path = Path(item['FullPath']).resolve()
                if not path.is_relative_to(project.resolve()):
                    raise RuntimeError('MSBuild included a source outside the cohort.')
                actual.add(path.relative_to(project.resolve()).as_posix())
        expected = set(plan['BuildGraphs'][prefix + filename])
        if not expected.issubset(inputs) or any(not p.endswith(('.cs', '.xaml')) for p in expected):
            raise RuntimeError('The reviewed build graph contains an unqualified input.')
        if actual != expected:
            raise RuntimeError('Evaluated MSBuild source graph differs: missing=' + str(sorted(expected-actual)) + ' extra=' + str(sorted(actual-expected)))
        graph[prefix + filename] = sorted(actual)
    report_path = project / 'artifacts/site-cohort/verification.json'
    report = json.loads(report_path.read_text(encoding='utf-8'))
    report['EvaluatedSourceGraph'] = graph
    report['EvaluatedSourceGraphVerified'] = True
    save_report(project, report)
    print('PASS evaluated MSBuild Compile/Page/ApplicationDefinition graphs:', {k: len(v) for k, v in graph.items()})


def finish(project, plan):
    require_hosted_checkout(project)
    verify_materialized(project, plan)
    report = json.loads((project / 'artifacts/site-cohort/verification.json').read_text(encoding='utf-8'))
    if not report.get('EvaluatedSourceGraphVerified'):
        raise RuntimeError('Build input graph evidence missing.')
    outputs = ['bin/Release/net8.0-windows7.0/PharmaLIMS.dll',
               'tools/PharmaLIMS.TrustedSigning.Host/bin/Release/net8.0-windows/PharmaLIMS.TrustedSigning.Host.dll']
    # The host target framework is verified from the pinned project, not assumed.
    host_project = (project / 'tools/PharmaLIMS.TrustedSigning.Host/PharmaLIMS.TrustedSigning.Host.csproj').read_text(encoding='utf-8')
    framework = re.search(r'<TargetFramework>([^<]+)</TargetFramework>', host_project).group(1)
    outputs[1] = outputs[1].replace('net8.0-windows/', framework + '/')
    # Output paths may contain bin; they are never accepted as source-plan targets.
    report['BuildOutputs'] = [{'Path': name, 'Sha256': digest((project / name).read_bytes())} for name in outputs]
    report['BuildPassed'] = True
    save_report(project, report)
    print('PASS exact site-cohort Release compilation. Local WPF, authentication, installation and QA acceptance remain unverified.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['check', 'prepare', 'graph', 'finish'])
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    plan = json.loads((project / 'scripts/site-cohort-inputs/AMEEN-cohort.json').read_text(encoding='utf-8'))
    if args.operation == 'check':
        validate_inputs(project, plan['Inputs'])
        print('PASS all immutable cohort inputs validated; no source changed.')
    elif args.operation == 'prepare':
        prepare(project, plan)
    elif args.operation == 'graph':
        verify_graph(project, plan)
    else:
        finish(project, plan)


if __name__ == '__main__':
    main()
