import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

const repo = path.resolve(import.meta.dirname, '..', '..');
const read = relative => readFile(path.join(repo, relative), 'utf8');

const buildScripts = [
  'scripts/Common.ps1',
  'scripts/Restore.ps1',
  'scripts/Test.ps1',
  'scripts/Build.ps1',
  'scripts/Publish.ps1',
  'scripts/Import-OfficialRuntimePacks.ps1',
  'scripts/Package.ps1',
  'scripts/Verify.ps1',
  'scripts/New-NativeHostManifest.ps1',
  'scripts/Invoke-DemoSmoke.ps1',
  'scripts/New-NightGateMsi.ps1',
];

function chromeExtensionId(publicKey) {
  const digest = createHash('sha256')
    .update(Buffer.from(publicKey, 'base64'))
    .digest()
    .subarray(0, 16);
  return [...digest]
    .flatMap(byte => [byte >> 4, byte & 15])
    .map(nibble => String.fromCharCode(97 + nibble))
    .join('');
}

function runPowerShell(command) {
  const encoded = Buffer.from(command, 'utf16le').toString('base64');
  const systemRoot = process.env.SystemRoot ?? process.env.WINDIR ?? 'C:\\Windows';
  const windowsPowerShell = path.join(
    systemRoot,
    'System32',
    'WindowsPowerShell',
    'v1.0',
    'powershell.exe',
  );
  const modulePaths = [
    process.env.USERPROFILE
      ? path.join(process.env.USERPROFILE, 'Documents', 'WindowsPowerShell', 'Modules')
      : null,
    process.env.ProgramFiles
      ? path.join(process.env.ProgramFiles, 'WindowsPowerShell', 'Modules')
      : null,
    path.join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'Modules'),
  ].filter(Boolean);
  const windowsPowerShellEnvironment = {
    ...process.env,
    PSModulePath: [...new Map(
      modulePaths.map(modulePath => [modulePath.toLowerCase(), modulePath]),
    ).values()].join(';'),
  };
  return spawnSync(windowsPowerShell, [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-EncodedCommand', encoded,
  ], {
    cwd: repo,
    encoding: 'utf8',
    env: windowsPowerShellEnvironment,
  });
}

test('release scripts exist, fail fast, and keep machine mutation out of default verification', async () => {
  const texts = await Promise.all(buildScripts.map(read));
  for (const [index, text] of texts.entries()) {
    assert.match(text, /Set-StrictMode\s+-Version\s+Latest/i, buildScripts[index]);
    assert.match(text, /\$ErrorActionPreference\s*=\s*['"]Stop['"]/i, buildScripts[index]);
  }

  const defaultTooling = texts.join('\n');
  for (const forbidden of [
    /\bsc(?:\.exe)?\s+(?:create|delete|start|stop)\b/i,
    /\b(?:New|Start|Stop|Remove)-Service\b/i,
    /\b(?:Register|Unregister)-ScheduledTask\b/i,
    /\bschtasks(?:\.exe)?\b/i,
    /\bLockWorkStation\b/i,
    /\bTerminateProcess\b/i,
    /\bpowercfg(?:\.exe)?\b/i,
    /\bnetsh(?:\.exe)?\b/i,
    /\bshutdown(?:\.exe)?\b/i,
  ]) {
    assert.doesNotMatch(defaultTooling, forbidden);
  }
});

test('common tooling pins the project SDK and supports an explicit offline local feed', async () => {
  const common = await read('scripts/Common.ps1');
  assert.match(common, /work[\\/]\.dotnet[\\/]dotnet\.exe/i);
  assert.match(common, /10\.0\.301/);
  assert.match(common, /work[\\/]nuget-feed/i);
  assert.match(common, /NuGetAudit=false/);
  assert.match(common, /ContinuousIntegrationBuild=true/);
  assert.match(common, /TreatWarningsAsErrors=true/);
});

test('desktop probe compilation source is included without a local work directory', async () => {
  const project = await read('tests/NightGate.Desktop.Tests/NightGate.Desktop.Tests.csproj');
  assert.match(project, /Compile\s+Include="\.\.\\Shared\\InstalledStateProbe\.cs"/i);
  assert.doesNotMatch(project, /Compile\s+Include="[^"\r\n]*\bwork[\\/]/i);
  const probe = await read('tests/Shared/InstalledStateProbe.cs');
  assert.match(probe, /internal static class InstalledStateProbe/);
  assert.doesNotMatch(probe, /[A-Z]:\\Users\\/i);
});

test('restore keeps configured online sources unless offline mode is explicitly selected', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-restore-source-'));
  const feed = path.join(directory, 'work', 'nuget-feed');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const commonPath = path.join(repo, 'scripts', 'Common.ps1');
  const run = command => runPowerShell(
    `$ErrorActionPreference='Stop'; . ${quote(commonPath)}; `
    + `$script:NightGateRepoRoot=${quote(directory)}; `
    + `$env:NIGHTGATE_OFFLINE_RESTORE=$null; ${command}`,
  );
  try {
    const absent = run(`@(Get-NightGateRestoreArguments)|ConvertTo-Json -Compress`);
    assert.equal(absent.status, 0, absent.stderr || absent.stdout);
    assert.ok(!JSON.parse(absent.stdout.trim()).includes('--source'));

    await mkdir(feed, { recursive: true });
    const result = run(
      `$online=@(Get-NightGateRestoreArguments); `
      + `$explicit=@(Get-NightGateRestoreArguments -Offline); `
      + `$env:NIGHTGATE_OFFLINE_RESTORE='1'; $environment=@(Get-NightGateRestoreArguments); `
      + `$env:NIGHTGATE_OFFLINE_RESTORE='0'; $disabled=@(Get-NightGateRestoreArguments); `
      + `[ordered]@{online=$online;explicit=$explicit;environment=$environment;disabled=$disabled}`
      + `|ConvertTo-Json -Compress`,
    );
    assert.equal(result.status, 0, result.stderr || result.stdout);
    const actual = JSON.parse(result.stdout.trim());
    assert.ok(!actual.online.includes('--source'), 'an existing local feed must not disable nuget.org');
    assert.ok(!actual.disabled.includes('--source'), 'only an explicit enabled value selects offline mode');
    assert.deepEqual(actual.explicit.slice(0, 2), ['--source', feed]);
    assert.deepEqual(actual.environment, actual.explicit);
    assert.ok(actual.online.includes('-p:TreatWarningsAsErrors=true'));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('explicit offline restore reports a missing feed before invoking NuGet', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-missing-offline-feed-'));
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  try {
    const result = runPowerShell(
      `$ErrorActionPreference='Stop'; . ${quote(path.join(repo, 'scripts', 'Common.ps1'))}; `
      + `$script:NightGateRepoRoot=${quote(directory)}; Get-NightGateRestoreArguments -Offline`,
    );
    assert.notEqual(result.status, 0);
    assert.match(`${result.stderr}\n${result.stdout}`, /offline.*feed.*missing/i);
    await assert.rejects(stat(path.join(directory, 'work')), { code: 'ENOENT' });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('source fingerprint ignores WPF temporary projects but covers shared probe source', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-source-fingerprint-'));
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const fingerprint = () => {
    const result = runPowerShell(
      `$ErrorActionPreference='Stop'; . ${quote(path.join(repo, 'scripts', 'Common.ps1'))}; `
      + `$script:NightGateRepoRoot=${quote(directory)}; Get-NightGateTestSourceFingerprint`,
    );
    assert.equal(result.status, 0, result.stderr || result.stdout);
    assert.match(result.stdout.trim(), /^[A-F0-9]{64}$/);
    return result.stdout.trim();
  };
  try {
    for (const name of [
      'Directory.Build.props', 'NightGate.slnx', 'NuGet.Config', 'global.json',
      'README.md', 'USER-GUIDE.zh-CN.md',
    ]) await writeFile(path.join(directory, name), 'fixture\n');
    for (const name of ['assets', 'docs', 'installer', 'scripts', 'src', 'tests']) {
      await mkdir(path.join(directory, name));
    }
    await mkdir(path.join(directory, 'tests', 'Shared'));
    const probe = path.join(directory, 'tests', 'Shared', 'InstalledStateProbe.cs');
    await writeFile(probe, 'shared source v1\n');
    const baseline = fingerprint();
    const temporaryProject = path.join(directory, 'src', 'NightGate.Desktop_random_wpftmp.csproj');
    await writeFile(temporaryProject, 'generated machine-specific project\n');
    assert.equal(fingerprint(), baseline, 'generated WPF projects are not source inputs');
    await writeFile(temporaryProject, 'regenerated project with a different local path\n');
    assert.equal(fingerprint(), baseline);
    await writeFile(probe, 'shared source v2\n');
    assert.notEqual(fingerprint(), baseline, 'the relocated probe must remain covered by test evidence');
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('runtime-pack importer accepts only exact signed downloads before writing the feed', async () => {
  const importer = await read('scripts/Import-OfficialRuntimePacks.ps1');
  assert.match(importer, /Get-NightGateRuntimePackRequirements/i);
  assert.match(importer, /Get-NightGateNuGetPackageIdentity/i);
  assert.match(importer, /nuget['"]?\s*,?\s*['"]verify/i);
  assert.match(importer, /--all/i);
  assert.match(importer, /Get-NightGateSha512/i);
  assert.match(importer, /Assert-NightGateOfficialRuntimePacks/i);
  assert.match(importer, /\$HOME\s*,?\s*['"]Downloads['"]/i);
  assert.match(importer, /WhatIfPreference/i);
  assert.match(importer, /Copy-Item[\s\S]{0,180}-Destination/i);
  assert.ok(
    importer.indexOf('NuGet signature verification failed')
      < importer.indexOf('Copy-Item'),
    'signature verification must occur before any package is copied',
  );

  const emptyDownloads = await mkdtemp(path.join(tmpdir(), 'nightgate-runtime-downloads-'));
  try {
    const missing = spawnSync('powershell.exe', [
      '-NoLogo',
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'scripts', 'Import-OfficialRuntimePacks.ps1'),
      '-SourceDirectory', emptyDownloads,
      '-WhatIf',
    ], { cwd: repo, encoding: 'utf8' });
    assert.notEqual(missing.status, 0, 'an empty download directory must be rejected');
    const output = `${missing.stdout}\n${missing.stderr}`;
    assert.match(output, /Microsoft\.NETCore\.App\.Runtime\.win-x64\.10\.0\.9\.nupkg/i);
    assert.match(output, /Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64\.10\.0\.9\.nupkg/i);
    assert.match(output, /Microsoft\.AspNetCore\.App\.Runtime\.win-x64\.10\.0\.9\.nupkg/i);
    assert.doesNotMatch(output, /Join-Path[\s\S]*empty string/i);
  } finally {
    await rm(emptyDownloads, { recursive: true, force: true });
  }
});

test('formal publish accepts only exact signed official runtime packs and records a SHA-512 lock', async () => {
  const publish = await read('scripts/Publish.ps1');
  const common = await read('scripts/Common.ps1');
  const verify = await read('scripts/Verify.ps1');
  const offlinePackEntry = await read('scripts/New-OfflineRuntimePacks.ps1');
  assert.match(publish, /['"]--self-contained['"]\s*,\s*['"]true['"]/i);
  assert.match(publish, /win-x64/i);
  assert.match(publish, /Assert-NightGateOfficialRuntimePacks/i);
  assert.match(publish, /official signed win-x64 runtime packs/i);
  assert.match(publish, /\.publish-mode\.json/i);

  for (const id of [
    'Microsoft.NETCore.App.Runtime.win-x64',
    'Microsoft.WindowsDesktop.App.Runtime.win-x64',
    'Microsoft.AspNetCore.App.Runtime.win-x64',
  ]) {
    assert.ok(common.includes(id), `missing official runtime-pack identity: ${id}`);
  }
  assert.match(common, /10\.0\.9/);
  assert.match(common, /\.nuspec/i);
  assert.match(common, /dotnet[^\r\n]+nuget[^\r\n]+verify[^\r\n]+--all/i);
  assert.match(publish, /runtime-packs\.sha512\.json/i);
  assert.match(publish, /WriteAllText/i);
  assert.match(publish, /runtimePackLock[\s\S]{0,180}schemaVersion\s*=\s*1/i);
  assert.match(verify, /runtime-packs\.sha512\.json/i);
  assert.match(verify, /Runtime-pack SHA-512 lock disagrees/i);
  assert.match(common, /Get-FileHash[^\r\n]+SHA512/i);
  assert.match(common, /signatureVerified/i);
  assert.match(common, /sha512/i);

  assert.match(offlinePackEntry, /refus(?:e|es|ing)|拒绝/i);
  assert.match(offlinePackEntry, /official signed/i);
  assert.match(offlinePackEntry, /throw/i);
  assert.doesNotMatch(offlinePackEntry, /ZipArchive|CreateEntry|SharedFrameworkDirectory/i);
});

async function snapshotRuntimePackFeed(feed) {
  const entries = await readdir(feed).catch(error => {
    if (error.code === 'ENOENT') return [];
    throw error;
  });
  return entries
    .filter(name => /Microsoft\.(?:NETCore|WindowsDesktop|AspNetCore)\.App\.Runtime\.win-x64/i.test(name))
    .sort();
}

test('runtime-pack snapshot accepts a missing clean-clone feed without creating it', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-missing-pack-snapshot-'));
  const feed = path.join(directory, 'work', 'nuget-feed');
  try {
    assert.deepEqual(await snapshotRuntimePackFeed(feed), []);
    await assert.rejects(stat(path.join(directory, 'work')), { code: 'ENOENT' });
    await mkdir(feed, { recursive: true });
    await writeFile(path.join(feed, 'unrelated.txt'), 'not a runtime package');
    const packageName = 'Microsoft.NETCore.App.Runtime.win-x64.10.0.9.nupkg';
    await writeFile(path.join(feed, packageName), 'snapshot-only fixture');
    assert.deepEqual(await snapshotRuntimePackFeed(feed), [packageName]);
    const fileInsteadOfFeed = path.join(directory, 'not-a-directory');
    await writeFile(fileInsteadOfFeed, 'fixture');
    await assert.rejects(snapshotRuntimePackFeed(fileInsteadOfFeed));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('obsolete offline runtime-pack entry refuses reconstruction without changing the local feed', async () => {
  const feed = path.join(repo, 'work', 'nuget-feed');
  const snapshot = () => snapshotRuntimePackFeed(feed);
  const before = await snapshot();
  const result = spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', path.join(repo, 'scripts', 'New-OfflineRuntimePacks.ps1'),
  ], { cwd: repo, encoding: 'utf8' });
  assert.notEqual(result.status, 0);
  assert.match(`${result.stderr}\n${result.stdout}`, /refuses to reconstruct or forge/i);
  assert.deepEqual(await snapshot(), before);
});

test('runtime-pack restore classification rejects unrelated failures', async () => {
  const commonPath = path.join(repo, 'scripts', 'Common.ps1');

  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const classification = runPowerShell(
    `. ${quote(commonPath)}; `
    + `$runtime = 'project : error NU1101: missing Microsoft.NETCore.App.Runtime.win-x64'; `
    + `$unrelated = 'project : error NU1101: missing Example.Unrelated.Package'; `
    + `$mixed = $runtime + [Environment]::NewLine + 'project : error NU1605: downgrade'; `
    + `if (-not (Test-NightGateMissingRuntimePackFailure -Output $runtime)) { exit 11 }; `
    + `if (Test-NightGateMissingRuntimePackFailure -Output $unrelated) { exit 12 }; `
    + `if (Test-NightGateMissingRuntimePackFailure -Output $mixed) { exit 13 }`,
  );
  assert.equal(classification.status, 0, classification.stderr || classification.stdout);
});

test('runtime-pack trust manifest rejects an inexact package identity before reading package bytes', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-runtime-pack-trust-'));
  const manifestPath = path.join(directory, 'runtime-packs.sha512.json');
  try {
    await writeFile(manifestPath, JSON.stringify({
      schemaVersion: 1,
      packages: [
        {
          id: 'Microsoft.NETCore.App.Runtime.win-arm64',
          version: '10.0.9',
          sha512: 'A'.repeat(128),
        },
        {
          id: 'Microsoft.WindowsDesktop.App.Runtime.win-x64',
          version: '10.0.9',
          sha512: 'B'.repeat(128),
        },
        {
          id: 'Microsoft.AspNetCore.App.Runtime.win-x64',
          version: '10.0.9',
          sha512: 'C'.repeat(128),
        },
      ],
    }));
    const commonPath = path.join(repo, 'scripts', 'Common.ps1');
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const result = runPowerShell(
      `. ${quote(commonPath)}; Initialize-NightGateBuildEnvironment; `
      + `$dotnet=Resolve-NightGateDotNet; `
      + `Assert-NightGateOfficialRuntimePacks -DotNetPath $dotnet `
      + `-ManifestPath ${quote(manifestPath)}`,
    );
    assert.notEqual(result.status, 0);
    const output = `${result.stderr}\n${result.stdout}`;
    assert.match(output, /missing the exact identity/i);
    assert.doesNotMatch(output, /runtime pack is missing/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('runtime-pack signature gate does not require an external SHA-512 manifest', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-runtime-pack-no-manifest-'));
  try {
    const commonPath = path.join(repo, 'scripts', 'Common.ps1');
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const result = runPowerShell(
      `. ${quote(commonPath)}; Initialize-NightGateBuildEnvironment; `
      + `$dotnet=Resolve-NightGateDotNet; `
      + `Assert-NightGateOfficialRuntimePacks -DotNetPath $dotnet `
      + `-FeedPath ${quote(directory)}`,
    );
    assert.notEqual(result.status, 0);
    const output = `${result.stderr}\n${result.stdout}`;
    assert.match(output, /official signed win-x64 runtime pack is missing/i);
    assert.doesNotMatch(output, /require.+trusted SHA-512 manifest/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('runtime-pack trust gate reads the nuspec and rejects a renamed package with the wrong identity', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-runtime-pack-nuspec-'));
  const feed = path.join(directory, 'feed');
  const payload = path.join(directory, 'payload');
  const archivePath = path.join(directory, 'fake.zip');
  const packageName = 'Microsoft.NETCore.App.Runtime.win-x64.10.0.9.nupkg';
  const packagePath = path.join(feed, packageName);
  const manifestPath = path.join(directory, 'runtime-packs.sha512.json');
  try {
    await mkdir(feed, { recursive: true });
    await mkdir(payload, { recursive: true });
    await writeFile(path.join(payload, 'fake.nuspec'), `<?xml version="1.0"?>
<package><metadata><id>Attacker.Renamed.Runtime</id><version>10.0.9</version></metadata></package>`);
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const compressed = runPowerShell(
      `Compress-Archive -LiteralPath ${quote(path.join(payload, 'fake.nuspec'))} `
      + `-DestinationPath ${quote(archivePath)} -Force`,
    );
    assert.equal(compressed.status, 0, compressed.stderr || compressed.stdout);
    await copyFile(archivePath, packagePath);
    const sha512 = createHash('sha512').update(await readFile(packagePath)).digest('hex').toUpperCase();
    await writeFile(manifestPath, JSON.stringify({
      schemaVersion: 1,
      packages: [
        { id: 'Microsoft.NETCore.App.Runtime.win-x64', version: '10.0.9', sha512 },
        { id: 'Microsoft.WindowsDesktop.App.Runtime.win-x64', version: '10.0.9', sha512: 'B'.repeat(128) },
        { id: 'Microsoft.AspNetCore.App.Runtime.win-x64', version: '10.0.9', sha512: 'C'.repeat(128) },
      ],
    }));
    const result = runPowerShell(
      `. ${quote(path.join(repo, 'scripts', 'Common.ps1'))}; `
      + `Initialize-NightGateBuildEnvironment; $dotnet=Resolve-NightGateDotNet; `
      + `Assert-NightGateOfficialRuntimePacks -DotNetPath $dotnet `
      + `-ManifestPath ${quote(manifestPath)} -FeedPath ${quote(feed)}`,
    );
    assert.notEqual(result.status, 0);
    const output = `${result.stderr}\n${result.stdout}`;
    assert.match(output, /nuspec identity mismatch/i);
    assert.doesNotMatch(output, /signature verification failed/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('runtime-pack trust gate rejects an unsigned package even when identity and SHA-512 match', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-runtime-pack-signature-'));
  const feed = path.join(directory, 'feed');
  const payload = path.join(directory, 'payload');
  const archivePath = path.join(directory, 'fake.zip');
  const packageName = 'Microsoft.NETCore.App.Runtime.win-x64.10.0.9.nupkg';
  const packagePath = path.join(feed, packageName);
  const manifestPath = path.join(directory, 'runtime-packs.sha512.json');
  try {
    await mkdir(feed, { recursive: true });
    await mkdir(payload, { recursive: true });
    await writeFile(path.join(payload, 'runtime.nuspec'), `<?xml version="1.0"?>
<package><metadata><id>Microsoft.NETCore.App.Runtime.win-x64</id><version>10.0.9</version></metadata></package>`);
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const compressed = runPowerShell(
      `Compress-Archive -LiteralPath ${quote(path.join(payload, 'runtime.nuspec'))} `
      + `-DestinationPath ${quote(archivePath)} -Force`,
    );
    assert.equal(compressed.status, 0, compressed.stderr || compressed.stdout);
    await copyFile(archivePath, packagePath);
    const sha512 = createHash('sha512').update(await readFile(packagePath)).digest('hex').toUpperCase();
    await writeFile(manifestPath, JSON.stringify({
      schemaVersion: 1,
      packages: [
        { id: 'Microsoft.NETCore.App.Runtime.win-x64', version: '10.0.9', sha512 },
        { id: 'Microsoft.WindowsDesktop.App.Runtime.win-x64', version: '10.0.9', sha512: 'B'.repeat(128) },
        { id: 'Microsoft.AspNetCore.App.Runtime.win-x64', version: '10.0.9', sha512: 'C'.repeat(128) },
      ],
    }));
    const result = runPowerShell(
      `. ${quote(path.join(repo, 'scripts', 'Common.ps1'))}; `
      + `Initialize-NightGateBuildEnvironment; $dotnet=Resolve-NightGateDotNet; `
      + `Assert-NightGateOfficialRuntimePacks -DotNetPath $dotnet `
      + `-ManifestPath ${quote(manifestPath)} -FeedPath ${quote(feed)}`,
    );
    assert.notEqual(result.status, 0);
    assert.match(`${result.stderr}\n${result.stdout}`, /signature verification failed or is unavailable/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('private-runtime output is explicit diagnostics only and cannot pass formal verification by default', async () => {
  const publish = await read('scripts/Publish.ps1');
  const verify = await read('scripts/Verify.ps1');

  assert.match(publish, /ForcePrivateRuntimeFallback/i);
  assert.match(publish, /private-runtime-fallback/i);
  assert.match(publish, /releaseEligible\s*=\s*\$false/i);
  assert.match(publish, /AppHostDotNetSearch/i);
  assert.match(publish, /app-relative-only/i);
  assert.doesNotMatch(
    publish,
    /Test-NightGateMissingRuntimePackFailure[\s\S]{0,500}\$selfContained\s*=\s*\$false/i,
  );

  assert.match(verify, /AllowPrivateRuntimeFallback/i);
  assert.match(
    verify,
    /private-runtime-fallback[\s\S]{0,500}formal release verification[\s\S]{0,200}throw/i,
  );
  assert.match(verify, /DIAGNOSTIC PASS \(NOT RELEASE\)/i);
});

test('publish isolates every restore graph from shared project obj directories', async () => {
  const publish = await read('scripts/Publish.ps1');
  assert.match(publish, /--artifacts-path/i);
  assert.match(publish, /BaseIntermediateOutputPath/i);
  assert.match(publish, /MSBuildProjectExtensionsPath/i);
  assert.match(publish, /BaseOutputPath/i);
  assert.doesNotMatch(publish, /\$\(MSBuildProjectName\)/);
  assert.match(publish, /artifacts[\\/]isolated/i);
});

test('publish, package, and verification support fresh repo-scoped candidate directories', async () => {
  const [common, publish, packageScript, verify] = await Promise.all([
    read('scripts/Common.ps1'),
    read('scripts/Publish.ps1'),
    read('scripts/Package.ps1'),
    read('scripts/Verify.ps1'),
  ]);
  assert.match(common, /function\s+Resolve-NightGateRepoScopedDirectory/i);
  assert.match(common, /function\s+Resolve-NightGateRepoScopedFile/i);
  assert.match(common, /must stay below the repository root/i);
  for (const script of [publish, packageScript, verify]) {
    assert.match(script, /\$PublishDirectory/i);
    assert.match(script, /Resolve-NightGateRepoScopedDirectory/i);
  }
  assert.match(publish, /\$IsolatedArtifactsDirectory/i);
  assert.match(packageScript, /\$OutputDirectory/i);
  assert.match(verify, /\$OutputDirectory/i);
  assert.match(verify, /\$TestSummaryPath/i);
  assert.match(packageScript, /sourceEvidence\s*=\s*\([\s\S]{0,160}Get-NightGateRelativePath/i);
  assert.match(packageScript, /artifact\s*=\s*\([\s\S]{0,160}Get-NightGateRelativePath/i);
});

test('package tooling creates a deterministic ZIP, checksums, inventory, and placeholder-free staged manifest', async () => {
  const packageScript = await read('scripts/Package.ps1');
  assert.match(packageScript, /NightGate-win-x64\.zip/i);
  assert.match(packageScript, /SHA256/i);
  assert.match(packageScript, /file-inventory\.sha256/i);
  assert.match(packageScript, /ZipArchive/i);
  assert.match(packageScript, /2000-01-01/);
  assert.match(packageScript, /__NIGHTGATE_NATIVE_HOST_PATH__/);
  assert.match(packageScript, /Program Files[\\/]NightGate/i);
  assert.match(packageScript, /NightGate\.Chrome\.Extension/i);
  assert.match(packageScript, /New-NightGateRenderedWixSource/i);
  assert.match(packageScript, /Get-NightGateMsiArtifactIdentity/i);
  assert.match(packageScript, /wixSourceStatus\s*=\s*['"]authored-only['"]/i);
  assert.match(packageScript, /wixSourceCompiled\s*=\s*\$false/i);
});

test('verification orchestrates all safe evidence and never invokes installation', async () => {
  const verify = await read('scripts/Verify.ps1');
  for (const required of [
    /Restore\.ps1/i,
    /Test\.ps1/i,
    /Build\.ps1/i,
    /Package\.ps1/i,
    /Invoke-DemoSmoke\.ps1/i,
    /git[^\r\n]+diff[^\r\n]+--check/i,
    /verification-report\.md/i,
    /installer availability/i,
    /NativeHost/i,
    /Get-Command\s+['"]git['"]\s+-ErrorAction\s+SilentlyContinue/i,
    /Git unavailable; release-source trailing-whitespace scan PASS/i,
  ]) {
    assert.match(verify, required);
  }
  assert.doesNotMatch(verify, /Install-NightGate\.ps1[^'"\r\n]*&/i);
  assert.doesNotMatch(verify, /Uninstall-NightGate\.ps1[^'"\r\n]*&/i);
});

test('Windows PowerShell native-host smoke disables system dotnet for every release mode', async () => {
  const verify = await read('scripts/Verify.ps1');
  assert.match(verify, /DOTNET_ROOT_X64/i);
  assert.match(verify, /DOTNET_MULTILEVEL_LOOKUP/i);
  assert.match(verify, /nonexistent-system-dotnet/i);
  assert.match(verify, /system dotnet disabled/i);
  assert.match(verify, /['"]Process['"]\s*\)/i);
  assert.match(verify, /finally\s*\{/i);
  assert.match(verify, /StandardInput\.BaseStream\.Close\(\)/i);
  assert.doesNotMatch(verify, /StandardInput\.Close\(\)/i);
  assert.match(verify, /\$previousConsoleInputEncoding\s*=\s*\[Console\]::InputEncoding/i);
  assert.match(verify, /\[Console\]::InputEncoding\s*=\s*\[Text\.UTF8Encoding\]::new\(\$false\)/i);
  assert.match(verify, /\[Console\]::InputEncoding\s*=\s*\$previousConsoleInputEncoding/i);
  assert.doesNotMatch(verify, /EnvironmentVariables\[['"]DOTNET_ROOT['"]\]/i);
  assert.doesNotMatch(
    verify,
    /if\s*\(\$publishMode\.mode\s+-eq\s+['"]private-runtime-fallback['"]\)\s*\{[\s\S]{0,900}nonexistent-system-dotnet/i,
  );
});

test('genuine self-contained verification requires native runtime and WindowsDesktop assets with no fallback wrappers', async () => {
  const verify = await read('scripts/Verify.ps1');
  for (const required of [
    /hostfxr\.dll/i,
    /hostpolicy\.dll/i,
    /coreclr\.dll/i,
    /PresentationFramework\.dll/i,
    /PresentationCore\.dll/i,
    /WindowsBase\.dll/i,
    /wpfgfx_cor3\.dll/i,
    /Get-ChildItem[^\r\n]+-Filter\s+['"]\*\.cmd['"]/i,
    /top-level runtime/i,
  ]) {
    assert.match(verify, required);
  }
});

test('test orchestration records machine-readable .NET and Node pass counts', async () => {
  const testScript = await read('scripts/Test.ps1');
  const verify = await read('scripts/Verify.ps1');
  assert.match(testScript, /test-summary\.json/i);
  assert.match(testScript, /ResultSummary/i);
  assert.match(testScript, /--test-reporter[=']+tap/i);
  assert.match(testScript, /dotnetPassed/i);
  assert.match(testScript, /nodePassed/i);
  assert.match(testScript, /-Status\s+['"]running['"]/i);
  assert.match(testScript, /-Status\s+['"]failed['"]/i);
  assert.match(testScript, /-Status\s+['"]completed['"]/i);
  assert.match(testScript, /Write-NightGateJsonAtomically/i);
  assert.match(testScript, /Get-NightGateTestSourceFingerprint/i);
  assert.match(verify, /Assert-NightGateCompletedTestSummary/i);
  assert.match(verify, /Get-NightGateTestSourceFingerprint/i);
});

test('test-summary validation rejects stale, failed, and unsuccessful evidence', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-test-summary-'));
  const resultsRoot = path.join(directory, 'test-results');
  const runId = '20260726-003900862-c97a63a1';
  const runDirectory = path.join(resultsRoot, runId);
  const canonicalPath = path.join(resultsRoot, 'test-summary.json');
  const runPath = path.join(runDirectory, 'test-summary.json');
  const expectedFingerprint = 'A'.repeat(64);
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const commonPath = path.join(repo, 'scripts', 'Common.ps1');
  const validate = () => runPowerShell(
    `. ${quote(commonPath)}; `
    + `Assert-NightGateCompletedTestSummary `
    + `-SummaryPath ${quote(canonicalPath)} `
    + `-ResultsRoot ${quote(resultsRoot)} `
    + `-ExpectedSourceFingerprint ${quote(expectedFingerprint)} | Out-Null`,
  );
  const summary = ({
    status = 'completed',
    sourceFingerprint = expectedFingerprint,
    dotnetPassed = 1,
    dotnetFailed = 0,
    nodePassed = 1,
    nodeFailed = 0,
  } = {}) => ({
    schemaVersion: 1,
    status,
    runId,
    startedAtUtc: '2026-07-25T16:39:00.8620000+00:00',
    completedAtUtc: '2026-07-25T16:40:00.8620000+00:00',
    sourceFingerprintAlgorithm: 'nightgate-test-source-v1-sha256',
    sourceFingerprint,
    dotnetPassed,
    dotnetFailed,
    dotnetSkipped: 0,
    nodePassed,
    nodeFailed,
    nodeSkipped: 0,
    failure: null,
  });
  const writeSummary = async value => {
    const json = `${JSON.stringify(value)}\n`;
    await writeFile(canonicalPath, json, 'utf8');
    await writeFile(runPath, json, 'utf8');
  };

  try {
    await mkdir(runDirectory, { recursive: true });

    await writeSummary(summary());
    const valid = validate();
    assert.equal(valid.status, 0, valid.stderr || valid.stdout);

    await writeSummary(summary({ sourceFingerprint: 'B'.repeat(64) }));
    const stale = validate();
    assert.notEqual(stale.status, 0);
    assert.match(`${stale.stderr}\n${stale.stdout}`, /source fingerprint/i);

    await writeSummary(summary({ status: 'failed' }));
    const failed = validate();
    assert.notEqual(failed.status, 0);
    assert.match(`${failed.stderr}\n${failed.stdout}`, /not completed/i);

    await writeSummary(summary({ dotnetFailed: 1 }));
    const failingCount = validate();
    assert.notEqual(failingCount.status, 0);
    assert.match(`${failingCount.stderr}\n${failingCount.stdout}`, /failed test count/i);

    await writeSummary(summary({ nodePassed: 0 }));
    const emptyCount = validate();
    assert.notEqual(emptyCount.status, 0);
    assert.match(`${emptyCount.stderr}\n${emptyCount.stdout}`, /passed test count/i);

    const canonical = summary();
    await writeSummary(canonical);
    await writeFile(runPath, `${JSON.stringify({ ...canonical, nodePassed: 2 })}\n`, 'utf8');
    const mismatchedRun = validate();
    assert.notEqual(mismatchedRun.status, 0);
    assert.match(`${mismatchedRun.stderr}\n${mismatchedRun.stdout}`, /per-run summary/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('native host template origin exactly matches the extension public key', async () => {
  const extension = JSON.parse(await read('src/NightGate.Chrome.Extension/manifest.json'));
  const nativeHost = JSON.parse(await read('src/NightGate.NativeHost/com.nightgate.host.json'));
  const id = chromeExtensionId(extension.key);
  assert.equal(id, 'eefgemhlhbdodhlgjmicnoifhclhdgmm');
  assert.deepEqual(nativeHost.allowed_origins, [`chrome-extension://${id}/`]);
  assert.equal(nativeHost.path, '__NIGHTGATE_NATIVE_HOST_PATH__');
});

test('manifest generator replaces only the absolute host path and leaves no placeholder', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-manifest-'));
  const output = path.join(directory, 'com.nightgate.host.json');
  const host = 'C:\\Program Files\\NightGate\\apps\\NativeHost\\NightGate.NativeHost.exe';
  try {
    const result = spawnSync('powershell.exe', [
      '-NoLogo',
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'scripts', 'New-NativeHostManifest.ps1'),
      '-OutputPath', output,
      '-HostExecutablePath', host,
    ], { cwd: repo, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr || result.stdout);
    const generatedText = await readFile(output, 'utf8');
    const generated = JSON.parse(generatedText);
    assert.equal(generated.path, host);
    assert.deepEqual(generated.allowed_origins, [
      'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/',
    ]);
    assert.doesNotMatch(generatedText, /__[A-Z0-9_]+__/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('WiX audit source remains well-formed XML before schema compilation', () => {
  const wixPath = path.join(repo, 'installer', 'NightGate.wxs');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const parsed = runPowerShell(
    `$ErrorActionPreference='Stop'; [xml]$null=Get-Content -LiteralPath ${quote(wixPath)} -Raw -Encoding UTF8`,
  );
  assert.equal(parsed.status, 0, parsed.stderr || parsed.stdout);
});

test('one stable MSI identity renders a literal authored-only WiX release source', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-wix-identity-'));
  const output = path.join(directory, 'NightGate.wxs');
  const commonPath = path.join(repo, 'scripts', 'Common.ps1');
  const templatePath = path.join(repo, 'installer', 'NightGate.wxs');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  try {
    const rendered = runPowerShell(
      `$ErrorActionPreference='Stop'; . ${quote(commonPath)}; `
      + `$identity=Get-NightGateMsiIdentity -ProductVersion '0.3.17'; `
      + `$prior=Get-NightGateMsiIdentity -ProductVersion '0.3.16'; `
      + `New-NightGateRenderedWixSource -TemplatePath ${quote(templatePath)} `
      + `-OutputPath ${quote(output)} -Identity $identity; `
      + `[ordered]@{ProductVersion=$identity.ProductVersion;ProductCode=$identity.ProductCode;`
      + `UpgradeCode=$identity.UpgradeCode;PriorProductCode=$prior.ProductCode}`
      + `|ConvertTo-Json -Compress`,
    );
    assert.equal(rendered.status, 0, rendered.stderr || rendered.stdout);
    const identity = JSON.parse(rendered.stdout.trim());
    assert.deepEqual(identity, {
      ProductVersion: '0.3.17',
      ProductCode: '{B3242043-11C7-5151-96F7-3998961E0F6E}',
      UpgradeCode: '{B2D91E43-3320-4F82-AE8B-6D4A8769E066}',
      PriorProductCode: '{64C142B7-728C-2051-BCBF-4F97E97C162A}',
    });

    const wix = await readFile(output, 'utf8');
    assert.match(wix, /ProductCode="\{B3242043-11C7-5151-96F7-3998961E0F6E\}"/i);
    assert.match(wix, /Version="0\.3\.17"/i);
    assert.match(wix, /UpgradeCode="\{B2D91E43-3320-4F82-AE8B-6D4A8769E066\}"/i);
    assert.doesNotMatch(wix, /\$\(var\.(?:ProductCode|ProductVersion)\)/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('packaging and verification cross-check authored WiX identity without claiming compilation', async () => {
  const packageScript = await read('scripts/Package.ps1');
  const verify = await read('scripts/Verify.ps1');
  const readme = await read('README.md');
  for (const field of ['ProductVersion', 'ProductCode', 'UpgradeCode']) {
    assert.match(packageScript, new RegExp(`wixIdentity\\.${field}`, 'i'));
    assert.match(packageScript, new RegExp(`msiIdentity\\.${field}`, 'i'));
    assert.match(verify, new RegExp(`wixIdentity\\.${field}`, 'i'));
  }
  assert.match(packageScript, /Get-NightGateMsiArtifactIdentity/i);
  assert.match(verify, /wixSourceStatus/i);
  assert.match(verify, /wixSourceCompiled/i);
  assert.match(verify, /authored-only; not compiled/i);
  assert.match(readme, /authored-only/i);
  assert.match(readme, /未编译|没有编译/i);
});

test('WiX source declares x64 per-machine upgrade, LocalService, logon, ACL, and native host registration', async () => {
  const wix = await read('installer/NightGate.wxs');
  for (const required of [
    /Scope="perMachine"/i,
    /Platform="x64"/i,
    /MajorUpgrade/i,
    /MajorUpgrade Schedule="afterInstallExecute"/i,
    /ServiceInstall/i,
    /LocalService/i,
    /ServiceControl/i,
    /ProgramData/i,
    /PermissionEx/i,
    /CurrentVersion\\Run/i,
    /NativeMessagingHosts\\com\.nightgate\.host/i,
    /Permanent="yes"/i,
    /Component Id="NightGateServiceConfiguration"/i,
    /appsettings\.json/i,
  ]) {
    assert.match(wix, required);
  }
  assert.doesNotMatch(wix, /schtasks|shutdown\.exe/i);
  const cleanup = wix.match(
    /<Component Id="RollbackSnapshotCleanup"[\s\S]*?<\/Component>/i,
  )?.[0];
  assert.ok(cleanup, 'missing rollback snapshot cleanup component');
  assert.match(cleanup, /Guid="B19C091A-1794-EA51-A267-874C0EC6B21E"/i);
  assert.match(cleanup, /KeyPath="yes"/i);
  assert.match(cleanup, /Permanent="no"/i);
  assert.match(cleanup, /Name="rollback-snapshot\.json"[\s\S]{0,80}On="uninstall"/i);
  assert.match(cleanup, /Name="msi-install-state\.json"[\s\S]{0,120}On="uninstall"/i);
  assert.doesNotMatch(cleanup, /Name="[^"]*[?*][^"]*"/i);
});

test('MSI publication is SID-bound and has a native Windows Installer authoring fallback', async () => {
  const packageScript = await read('scripts/Package.ps1');
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  const msiFinalize = await read('installer/Finalize-NightGateMsi.ps1');
  const verify = await read('scripts/Verify.ps1');
  const readme = await read('README.md');
  assert.match(packageScript, /msiTargetSidContractImplemented\s*=\s*\$true/i);
  assert.match(packageScript, /New-NightGateMsi\.ps1/i);
  assert.match(packageScript, /targetInteractiveSidContractImplemented/i);
  assert.match(msiBuilder, /WindowsInstaller\.Installer/i);
  assert.match(msiBuilder, /UserSID/i);
  assert.match(msiBuilder, /ServiceInstall/i);
  assert.match(msiBuilder, /ServiceControl/i);
  assert.match(msiBuilder, /InstallExecuteSequence/i);
  assert.match(msiBuilder, /\[PowerShellV1Folder\]powershell\.exe/i);
  assert.match(msiBuilder, /-NonInteractive -WindowStyle Hidden/i);
  assert.doesNotMatch(msiBuilder, /powershell\.exe\s+\[CustomActionData\]/i);
  assert.match(msiBuilder, /ProductVersion/i);
  assert.match(msiBuilder, /Guid\]::NewGuid/i);
  assert.match(msiBuilder, /NEWERPRODUCTFOUND/i);
  assert.match(msiBuilder, /NIGHTGATEWINDOWSBUILD\s*>=\s*22000/i);
  assert.match(msiFinalize, /ConfiguredWindowsUserSid/i);
  assert.match(msiFinalize, /RegistryHive\]::Users/i);
  assert.match(msiFinalize, /RegistryKey\]::OpenBaseKey/i);
  assert.match(msiFinalize, /RegistryView\]::Registry32/i);
  assert.match(msiFinalize, /RegistryView\]::Registry64/i);
  assert.match(msiFinalize, /LocalService/i);
  assert.match(verify, /targetInteractiveSidContractImplemented/i);
  assert.match(verify, /MSI[^\r\n]+target[^\r\n]+SID/i);
  assert.match(readme, /MSI[\s\S]{0,220}UserSID/i);
});

test('MSI PowerShell transaction actions carry complete hidden commands directly', async () => {
  const wix = await read('installer/NightGate.wxs');
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  const verify = await read('scripts/Verify.ps1');
  for (const authoring of [wix, msiBuilder]) {
    assert.match(authoring, /\[PowerShellV1Folder\]powershell\.exe/i);
    assert.match(
      authoring,
      /-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass/i,
    );
    assert.match(authoring, /-File[\s\S]{0,120}\[#/i);
    assert.match(authoring, /Finalize-NightGateMsi\.ps1/i);
    for (const mode of ['Prepare', 'Rollback', 'Install', 'Uninstall', 'Commit']) {
      assert.match(authoring, new RegExp(`-Mode ${mode}`, 'i'));
    }
    assert.doesNotMatch(authoring, /powershell\.exe\s+\[CustomActionData\]/i);
  }
  assert.doesNotMatch(
    wix,
    /<SetProperty\s+Id="Set(?:PrepareInstall|PrepareUninstall|RollbackInstall|RollbackUninstall|Finalize|Uninstall|Commit)NightGateData"/i,
  );
  assert.match(wix, /\[INSTALLFOLDER\]\\&quot;/i);
  assert.match(wix, /\[PROGRAMDATAFOLDER\]\\&quot;/i);
  assert.doesNotMatch(
    msiBuilder,
    /,@\('Set(?:PrepareInstall|PrepareUninstall|RollbackInstall|RollbackUninstall|Finalize|Uninstall|Commit)NightGateData'/i,
  );
  assert.match(verify, /FormatRecord/i);
  assert.match(verify, /DoAction\('CostFinalize'\)/i);
  assert.match(verify, /FinalizeNightGate formats to an incomplete command/i);
});

test('MSI Win11 gate discovers build and workstation type inside each installer sequence', async () => {
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  const wix = await read('installer/NightGate.wxs');
  const verify = await read('scripts/Verify.ps1');
  for (const required of [
    /CREATE TABLE `AppSearch`/i,
    /CREATE TABLE `RegLocator`/i,
    /CREATE TABLE `Signature`/i,
    /CREATE TABLE `InstallUISequence`/i,
    /NIGHTGATEWINDOWSBUILD/i,
    /CurrentBuildNumber/i,
    /NIGHTGATEPRODUCTTYPE/i,
    /Control\\ProductOptions/i,
    /ProductType/i,
    /Installed\s+OR\s+\(NIGHTGATEWINDOWSBUILD\s*>=\s*22000[\s\S]*NIGHTGATEPRODUCTTYPE\s*=\s*"WinNT"\)/i,
  ]) {
    assert.match(msiBuilder, required);
  }
  assert.doesNotMatch(
    msiBuilder,
    /VersionNT64\s*>=\s*1000\s+AND\s+WindowsBuild\s*>=\s*22000/i,
  );
  assert.match(wix, /Property Id="NIGHTGATEWINDOWSBUILD"[\s\S]*RegistrySearch/i);
  assert.match(wix, /Property Id="NIGHTGATEPRODUCTTYPE"[\s\S]*RegistrySearch/i);
  assert.match(verify, /NIGHTGATEWINDOWSBUILD/i);
  assert.match(verify, /NIGHTGATEPRODUCTTYPE/i);
  assert.match(verify, /\$summaryTemplate\s*=\s*\[string\]\$summary\.Property\(7\)/i);
  assert.match(verify, /\$uiExecuteAction\s*=\s*Get-NightGateMsiScalar/i);
  assert.match(verify, /\$executeAppSearchCondition\s*=\s*Get-NightGateMsiScalar/i);
  assert.match(msiBuilder, /\$storedSummaryTemplate\s*=\s*\[string\]\$storedSummary\.Property\(7\)/i);
  assert.match(msiBuilder, /\$storedUiExecuteAction\s*=\s*Get-MsiScalar/i);
});

test('MSI authoring uses standard shell tables, a product icon, and 0.3.17 metadata', async () => {
  const buildProps = await read('Directory.Build.props');
  const wix = await read('installer/NightGate.wxs');
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  const packageScript = await read('scripts/Package.ps1');
  const verify = await read('scripts/Verify.ps1');
  const lifecycle = await read('installer/Test-NightGateMsiLifecycle.ps1');
  const chromeHealth = await read('src/NightGate.Core/ChromeProtectionHealth.cs');
  const extensionManifest = JSON.parse(
    await read('src/NightGate.Chrome.Extension/manifest.json'),
  );

  for (const table of ['Icon', 'Registry', 'Shortcut']) {
    assert.match(msiBuilder, new RegExp('CREATE TABLE `' + table + '`', 'i'));
  }
  assert.match(msiBuilder, /ARPPRODUCTICON[\s\S]*NightGateProductIcon/i);
  assert.match(msiBuilder, /ProgramMenuFolder/);
  assert.match(msiBuilder, /DesktopFolder/);
  assert.match(msiBuilder, /INSTALLDESKTOPSHORTCUT[\s\S]*['"]1['"]/i);
  assert.match(msiBuilder, /RemoveShortcuts['"\s,$null]*3200/i);
  assert.match(msiBuilder, /CreateShortcuts['"\s,$null]*4500/i);
  assert.match(msiBuilder, /WriteRegistryValues['"\s,$null]*5000/i);
  assert.doesNotMatch(msiBuilder, /WScript\.Shell|\.CreateShortcut\s*\(/i);
  assert.match(wix, /Package Name="收尾 NightGate"/);
  assert.match(wix, /Guid="1CAAAF77-83FA-4356-A9DC-F11A68457702"/i);
  assert.match(wix, /Guid="10D5411C-2D2D-A459-A7E7-3F0C48333AED"/i);
  assert.match(buildProps, /<Version>0\.3\.17<\/Version>/i);
  assert.match(buildProps, /<AssemblyVersion>1\.0\.0\.0<\/AssemblyVersion>/i);
  assert.match(buildProps, /<FileVersion>1\.3\.17\.0<\/FileVersion>/i);
  assert.match(packageScript, /\$ProductVersion\s*=\s*['"]0\.3\.17['"]/i);
  assert.match(packageScript, /-ProductVersion\s+\$ProductVersion/i);
  assert.match(packageScript, /productVersion\s*=\s*\$msiIdentity\.ProductVersion/i);
  assert.match(verify, /productVersion\s+-ne\s+['"]0\.3\.17['"]/i);
  assert.equal(extensionManifest.version, '0.1.5');
  assert.match(
    chromeHealth,
    /MinimumCompatibleExtensionVersion\s*=\s*"0\.1\.4"/,
  );
  assert.match(verify, /ARPSYSTEMCOMPONENT/i);
  assert.match(lifecycle, /PreviousMsiPath/i);
  assert.match(lifecycle, /lifecycle-upgrade-sentinel/i);
  assert.match(lifecycle, /Assert-NightGateProtocolPayloadVersions/i);
  assert.match(lifecycle, /Assert-NightGateExtensionVersion/i);
  assert.match(lifecycle, /Test-NightGateUserStatePipe/g);
  assert.match(lifecycle, /Test-NightGateInstalledNativeHostPolicy/g);
});

test('release payload file versions outrank the legacy 1.0.0.0 binaries', async () => {
  const buildProps = await read('Directory.Build.props');
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  const productVersion = buildProps.match(/<Version>([^<]+)<\/Version>/i)?.[1];
  const fileVersion = buildProps.match(/<FileVersion>([^<]+)<\/FileVersion>/i)?.[1];
  const asParts = value => value.split('.').map(part => Number.parseInt(part, 10));
  const compareVersions = (left, right) => {
    const leftParts = asParts(left);
    const rightParts = asParts(right);
    for (let index = 0; index < 4; index += 1) {
      const difference = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
      if (difference !== 0) return difference;
    }
    return 0;
  };

  assert.equal(productVersion, '0.3.17');
  assert.ok(
    compareVersions(fileVersion, '1.0.0.0') > 0,
    `payload FileVersion ${fileVersion} must replace legacy 1.0.0.0 files`,
  );
  assert.match(msiBuilder, /FileVersionInfo.*GetVersionInfo/i);
  assert.doesNotMatch(msiBuilder, /expectedFileVersion\s*=\s*"\$ProductVersion\.0"/i);
});

test('VM upgrade lifecycle covers both supported immediate predecessors of 0.3.17', async () => {
  const lifecycle = await read('installer/Test-NightGateMsiLifecycle.ps1');
  assert.match(lifecycle, /NightGate\.Desktop\.exe/i);
  assert.match(lifecycle, /NightGate\.Desktop\.dll/i);
  assert.match(lifecycle, /NightGate\.Service\.exe/i);
  assert.match(lifecycle, /NightGate\.NativeHost\.exe/i);
  assert.match(lifecycle, /chrome-extension\\manifest\.json/i);
  assert.match(lifecycle, /ValidateSet\('0\.3\.15',\s*'0\.3\.16'\)/i);
  assert.match(lifecycle, /\$PreviousProductVersion\s*=\s*'0\.3\.16'/i);
  assert.match(
    lifecycle,
    /'0\.3\.15'\s*\{\s*'1\.3\.15\.0'\s*\}[\s\S]*'0\.3\.16'\s*\{\s*'1\.3\.16\.0'\s*\}/i,
  );
  assert.match(
    lifecycle,
    /'0\.3\.15'\s*\{\s*'0\.1\.5'\s*\}[\s\S]*'0\.3\.16'\s*\{\s*'0\.1\.5'\s*\}/i,
  );
  assert.match(
    lifecycle,
    /Get-NightGateMsiProperty[\s\S]*-Name\s+ProductVersion/i,
  );
  assert.match(lifecycle, /install previous \$PreviousProductVersion/i);
  assert.match(lifecycle, /major upgrade to \$currentProductVersion/i);
  assert.match(
    lifecycle,
    /Assert-NightGateExtensionVersion\s+-ExpectedVersion\s+\$previousExtensionVersion/i,
  );
  assert.match(
    lifecycle,
    /Assert-NightGateExtensionVersion\s+-ExpectedVersion\s+'0\.1\.5'/i,
  );
  assert.match(lifecycle, /FileVersion/i);
  assert.match(lifecycle, /1\.3\.15\.0/);
  assert.match(lifecycle, /1\.3\.16\.0/);
  assert.match(lifecycle, /1\.3\.17\.0/);
});

test('VM lifecycle validates the installed Chrome native-host discovery and health protocol', async () => {
  const lifecycle = await read('installer/Test-NightGateMsiLifecycle.ps1');
  const bridge = await read('scripts/Test-InstalledChromeBridge.ps1');

  assert.match(lifecycle, /function\s+Assert-NightGateInstalledNativeHostRegistration/i);
  assert.match(lifecycle, /RegistryView\]::Registry32/i);
  assert.match(lifecycle, /RegistryView\]::Registry64/i);
  assert.match(lifecycle, /NativeMessagingHosts\\com\.nightgate\.host/i);
  assert.match(lifecycle, /allowed_origins/i);
  assert.match(lifecycle, /NightGate\.NativeHost\.exe/i);
  assert.match(lifecycle, /type\s*=\s*'heartbeat'/i);
  assert.match(
    lifecycle,
    /-ExtensionVersion\s+'0\.1\.3'[\s\S]*-ExpectedAccepted\s+\$false/i,
  );
  assert.match(
    lifecycle,
    /-ExtensionVersion\s+'0\.1\.4'[\s\S]*-ExpectedAccepted\s+\$true/i,
  );
  assert.match(lifecycle, /heartbeatResult/i);
  assert.match(lifecycle, /accepted/i);
  assert.match(bridge, /health\.extensionVersion\s+-ne\s*['"]0\.1\.5['"]/i);
});

test('installed Chrome bridge probe rejects an isolated shell identity before reading HKCU', async () => {
  const bridgePath = path.join(repo, 'scripts', 'Test-InstalledChromeBridge.ps1');
  const bridge = await read('scripts/Test-InstalledChromeBridge.ps1');
  const identityGuard = bridge.indexOf('$identityMismatch = Get-NightGateProbeIdentityMismatch');
  const registryProbe = bridge.indexOf('$viewResults = [ordered]@{}');

  assert.match(bridge, /WTSQuerySessionInformation/i);
  assert.match(bridge, /function\s+Get-NightGateInteractiveSessionIdentity/i);
  assert.match(bridge, /function\s+Get-NightGateProbeIdentityMismatch/i);
  assert.ok(identityGuard >= 0, 'the probe must evaluate the identity mismatch');
  assert.ok(
    registryProbe > identityGuard,
    'the identity mismatch must be rejected before either HKCU registry view is read',
  );

  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const behavior = runPowerShell(
    `$ErrorActionPreference='Stop'; `
    + `$tokens=$null;$errors=$null;`
    + `$ast=[Management.Automation.Language.Parser]::ParseFile(`
    + `${quote(bridgePath)},[ref]$tokens,[ref]$errors); `
    + `if($errors.Count -ne 0){throw $errors[0]}; `
    + `$definition=$ast.Find({param($node) `
    + `$node -is [Management.Automation.Language.FunctionDefinitionAst] -and `
    + `$node.Name -eq 'Get-NightGateProbeIdentityMismatch'},$true); `
    + `if($null -eq $definition){throw 'identity mismatch function is missing'}; `
    + `Invoke-Expression $definition.Extent.Text; `
    + `$mismatch=Get-NightGateProbeIdentityMismatch `
    + `-CurrentIdentityName 'PC\\CodexSandboxOffline' `
    + `-CurrentSid 'S-1-5-21-1-2-3-1005' `
    + `-InteractiveIdentityName 'PC\\DesktopUser' `
    + `-InteractiveSid 'S-1-5-21-1-2-3-1001'; `
    + `$fallback=Get-NightGateProbeIdentityMismatch `
    + `-CurrentIdentityName 'PC\\CodexSandboxOnline' `
    + `-CurrentSid 'S-1-5-21-1-2-3-1006' `
    + `-InteractiveIdentityName '' -InteractiveSid ''; `
    + `$matching=Get-NightGateProbeIdentityMismatch `
    + `-CurrentIdentityName 'PC\\DesktopUser' `
    + `-CurrentSid 'S-1-5-21-1-2-3-1001' `
    + `-InteractiveIdentityName 'PC\\DesktopUser' `
    + `-InteractiveSid 'S-1-5-21-1-2-3-1001'; `
    + `if([string]::IsNullOrWhiteSpace($mismatch)){exit 81}; `
    + `if([string]::IsNullOrWhiteSpace($fallback)){exit 82}; `
    + `if($null -ne $matching){exit 83}; `
    + `"Mismatch=$mismatch";"Fallback=$fallback"`,
  );
  assert.equal(behavior.status, 0, behavior.stderr || behavior.stdout);
  assert.match(behavior.stdout, /wrong Windows account/i);
  assert.match(behavior.stdout, /Codex sandbox identity/i);
  assert.match(behavior.stdout, /actual interactive desktop account/i);
});

test('native MSI fallback authors a versioned vital service package with safe upgrade metadata', async () => {
  const stage = await mkdtemp(path.join(repo, 'outputs', 'msi-test-'));
  const output = path.join(stage, 'NightGate-x64.msi');
  const fixture = path.join(repo, 'README.md');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const versionedFixture = path.join(stage, 'versioned-fixture.exe');
  const required = [
    'apps/Desktop/NightGate.Desktop.exe',
    'apps/Desktop/NightGate.ico',
    'apps/Service/NightGate.Service.exe',
    'apps/Service/appsettings.json',
    'apps/NativeHost/NightGate.NativeHost.exe',
  ];
  try {
    const compiledFixture = runPowerShell(
      `$ErrorActionPreference='Stop'\n`
      + `$source=@'\n`
      + `using System.Reflection;\n`
      + `[assembly: AssemblyVersion("1.0.0.0")]\n`
      + `[assembly: AssemblyFileVersion("1.3.17.0")]\n`
      + `public static class Program { public static void Main() {} }\n`
      + `'@\n`
      + `Add-Type -TypeDefinition $source -OutputAssembly ${quote(versionedFixture)} -OutputType ConsoleApplication`,
    );
    assert.equal(compiledFixture.status, 0, compiledFixture.stderr || compiledFixture.stdout);
    for (const relative of required) {
      const target = path.join(stage, relative);
      await mkdir(path.dirname(target), { recursive: true });
      if (relative === 'apps/Service/appsettings.json') {
        await writeFile(target, '{"NightGate":{"ConfiguredWindowsUserSid":"__CONFIGURED_WINDOWS_USER_SID__"}}\n');
      } else if (relative === 'apps/Desktop/NightGate.ico') {
        await copyFile(path.join(repo, 'assets', 'NightGate.ico'), target);
      } else if (relative.endsWith('.exe')) {
        // CostFinalize reads version resources for authored versioned files.
        // Use a real PE fixture while the MSI table version remains the
        // exact payload FileVersion under test.
        await copyFile(versionedFixture, target);
      } else {
        await copyFile(fixture, target);
      }
    }
    await mkdir(path.join(stage, 'installer'), { recursive: true });
    await mkdir(path.join(stage, 'native-host'), { recursive: true });
    await copyFile(
      path.join(repo, 'installer', 'Finalize-NightGateMsi.ps1'),
      path.join(stage, 'installer', 'Finalize-NightGateMsi.ps1'),
    );
    await copyFile(
      path.join(repo, 'installer', 'NightGate.Installation.Common.ps1'),
      path.join(stage, 'installer', 'NightGate.Installation.Common.ps1'),
    );
    await copyFile(
      path.join(repo, 'src', 'NightGate.NativeHost', 'com.nightgate.host.json'),
      path.join(stage, 'native-host', 'com.nightgate.host.json'),
    );

    const authored = spawnSync('powershell.exe', [
      '-NoLogo',
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'scripts', 'New-NightGateMsi.ps1'),
      '-StageDirectory', stage,
      '-OutputPath', output,
      '-ProductVersion', '0.3.17',
    ], { cwd: repo, encoding: 'utf8' });
    assert.equal(authored.status, 0, authored.stderr || authored.stdout);
    assert.ok((await stat(output)).size > 0);

    const inspected = runPowerShell(
      `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$queries=@(`
      + `"SELECT \`\`Name\`\` FROM \`\`ServiceInstall\`\` WHERE \`\`ServiceInstall\`\`='NightGateServiceInstall'",`
      + `"SELECT \`\`ErrorControl\`\` FROM \`\`ServiceInstall\`\` WHERE \`\`ServiceInstall\`\`='NightGateServiceInstall'",`
      + `"SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductVersion'",`
      + `"SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='SecureCustomProperties'",`
      + `"SELECT \`\`Condition\`\` FROM \`\`LaunchCondition\`\` WHERE \`\`Description\`\`='NightGate requires Windows 11 build 22000 or newer.'",`
      + `"SELECT \`\`Target\`\` FROM \`\`CustomAction\`\` WHERE \`\`Action\`\`='FinalizeNightGate'",`
      + `"SELECT \`\`Target\`\` FROM \`\`CustomAction\`\` WHERE \`\`Action\`\`='UninstallNightGate'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RemoveExistingProducts'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackInstallNightGate'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackUninstallNightGate'",`
      + `"SELECT \`\`Event\`\` FROM \`\`ServiceControl\`\` WHERE \`\`ServiceControl\`\`='NightGateServiceControl'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='StopServices'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='InstallExecute'",`
      + `"SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RemoveExistingProducts'",`
      + `"SELECT \`\`Version\`\` FROM \`\`File\`\` WHERE \`\`FileName\`\`='NightGate.Desktop.exe'",`
      + `"SELECT \`\`Version\`\` FROM \`\`File\`\` WHERE \`\`FileName\`\`='NightGate.Service.exe'",`
      + `"SELECT \`\`Version\`\` FROM \`\`File\`\` WHERE \`\`FileName\`\`='NightGate.NativeHost.exe'"); `
      + `foreach($q in $queries){$v=$d.OpenView($q);$v.Execute();$r=$v.Fetch();$value=$r.StringData(1);if([string]::IsNullOrWhiteSpace($value)){'<blank>'}else{$value};$v.Close()}`,
    );
    assert.equal(inspected.status, 0, inspected.stderr || inspected.stdout);
    assert.match(inspected.stdout, /NightGate\.LocalService/i);
    assert.match(inspected.stdout, /32769/);
    assert.match(inspected.stdout, /0\.3\.17/);
    assert.match(
      inspected.stdout,
      /OLDPRODUCTS;NEWERPRODUCTFOUND;NIGHTGATEWINDOWSBUILD;NIGHTGATEPRODUCTTYPE;INSTALLDESKTOPSHORTCUT/,
    );
    assert.match(inspected.stdout, /Installed\s+OR\s+\(NIGHTGATEWINDOWSBUILD\s*>=\s*22000[\s\S]*NIGHTGATEPRODUCTTYPE\s*=\s*"WinNT"\)/i);
    assert.match(inspected.stdout, /\[UserSID\]/i);
    assert.match(inspected.stdout, /-Mode Uninstall/i);
    assert.doesNotMatch(inspected.stdout, /-Mode Uninstall[^\r\n]*\[UserSID\]/i);
    assert.match(inspected.stdout, /6550/);
    assert.match(inspected.stdout, /4005/);
    assert.match(inspected.stdout, /3380/);
    const inspectionLines = inspected.stdout.trim().split(/\r?\n/);
    assert.equal(inspectionLines.at(-7), '163', 'service must stop on install and uninstall');
    assert.equal(inspectionLines.at(-6), '1900', 'service stop must keep its standard ordering');
    assert.equal(inspectionLines.at(-5), '6500', 'the queued service stop must execute before upgrade removal');
    assert.equal(inspectionLines.at(-4), '6550', 'old-product removal must remain inside the transaction');
    assert.deepEqual(
      inspectionLines.slice(-3),
      ['1.3.17.0', '1.3.17.0', '1.3.17.0'],
      'late major upgrades require versioned NightGate application binaries',
    );

    const shellEntries = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$queries=[ordered]@{`
      + `ProductName="SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductName'";`
      + `ProductVersion="SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductVersion'";`
      + `UpgradeCode="SELECT \`\`UpgradeCode\`\` FROM \`\`Upgrade\`\` WHERE \`\`ActionProperty\`\`='OLDPRODUCTS'";`
      + `ProgramMenuParent="SELECT \`\`Directory_Parent\`\` FROM \`\`Directory\`\` WHERE \`\`Directory\`\`='ProgramMenuFolder'";`
      + `StartGuid="SELECT \`\`ComponentId\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_NightGateStartMenuShortcut'";`
      + `DesktopCondition="SELECT \`\`Condition\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_NightGateDesktopShortcut'";`
      + `StartRegistry="SELECT \`\`Name\`\` FROM \`\`Registry\`\` WHERE \`\`Registry\`\`='R_NightGateStartMenuShortcut'";`
      + `StartShortcut="SELECT \`\`Name\`\` FROM \`\`Shortcut\`\` WHERE \`\`Shortcut\`\`='NightGateStartMenuShortcut'";`
      + `DesktopShortcut="SELECT \`\`Name\`\` FROM \`\`Shortcut\`\` WHERE \`\`Shortcut\`\`='NightGateDesktopShortcut'";`
      + `StartFeature="SELECT \`\`Feature_\`\` FROM \`\`FeatureComponents\`\` WHERE \`\`Component_\`\`='C_NightGateStartMenuShortcut'";`
      + `RemoveShortcuts="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RemoveShortcuts'";`
      + `CreateShortcuts="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='CreateShortcuts'";`
      + `WriteRegistryValues="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='WriteRegistryValues'"}; `
      + `foreach($name in $queries.Keys){$v=$d.OpenView($queries[$name]);$v.Execute();$r=$v.Fetch();if($null -eq $r){throw "Missing $name"};"$name=$($r.StringData(1))";$v.Close()}; `
      + `$v=$d.OpenView("SELECT \`\`Data\`\` FROM \`\`Icon\`\` WHERE \`\`Name\`\`='NightGateProductIcon'");$v.Execute();$r=$v.Fetch();"IconSize=$($r.DataSize(1))";$v.Close(); `
      + `$v=$d.OpenView("SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ARPSYSTEMCOMPONENT'");$v.Execute();"ArpSystemComponentPresent=$($null -ne $v.Fetch())";$v.Close()`,
    );
    assert.equal(shellEntries.status, 0, shellEntries.stderr || shellEntries.stdout);
    assert.match(shellEntries.stdout, /ProductName=收尾 NightGate/);
    assert.match(shellEntries.stdout, /ProductVersion=0\.3\.17/);
    assert.match(shellEntries.stdout, /UpgradeCode=\{B2D91E43-3320-4F82-AE8B-6D4A8769E066\}/i);
    assert.match(shellEntries.stdout, /ProgramMenuParent=TARGETDIR/i);
    assert.match(shellEntries.stdout, /StartGuid=\{1CAAAF77-83FA-4356-A9DC-F11A68457702\}/i);
    assert.match(shellEntries.stdout, /DesktopCondition=INSTALLDESKTOPSHORTCUT=1/i);
    assert.match(shellEntries.stdout, /StartRegistry=StartMenuShortcut/i);
    assert.match(shellEntries.stdout, /StartShortcut=收尾 NightGate/);
    assert.match(shellEntries.stdout, /DesktopShortcut=收尾/);
    assert.match(shellEntries.stdout, /StartFeature=Complete/i);
    assert.match(shellEntries.stdout, /RemoveShortcuts=3200/i);
    assert.match(shellEntries.stdout, /CreateShortcuts=4500/i);
    assert.match(shellEntries.stdout, /WriteRegistryValues=5000/i);
    assert.match(shellEntries.stdout, /IconSize=[1-9]\d*/i);
    assert.match(shellEntries.stdout, /ArpSystemComponentPresent=False/i);

    const transactionActions = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$actions=@('PrepareInstallNightGate','PrepareUninstallNightGate','RollbackInstallNightGate','RollbackUninstallNightGate','FinalizeNightGate','UninstallNightGate','CommitNightGate'); `
      + `foreach($action in $actions){`
      + `$v=$d.OpenView("SELECT \`\`Type\`\`,\`\`Target\`\` FROM \`\`CustomAction\`\` WHERE \`\`Action\`\`='$action'");`
      + `$v.Execute();$row=$v.Fetch();if($null -eq $row){throw "Missing $action"};`
      + `"$action|$($row.IntegerData(1))|$($row.StringData(2))";$v.Close()}; `
      + `$legacy=@('SetPrepareInstallNightGateData','SetPrepareUninstallNightGateData','SetRollbackInstallNightGateData','SetRollbackUninstallNightGateData','SetFinalizeNightGateData','SetUninstallNightGateData','SetCommitNightGateData'); `
      + `foreach($action in $legacy){$v=$d.OpenView("SELECT \`\`Action\`\` FROM \`\`CustomAction\`\` WHERE \`\`Action\`\`='$action'");$v.Execute();if($null -ne $v.Fetch()){throw "Legacy setter $action remains"};$v.Close()}`,
    );
    assert.equal(
      transactionActions.status,
      0,
      transactionActions.stderr || transactionActions.stdout,
    );
    for (const [action, type, mode] of [
      ['PrepareInstallNightGate', '3106', 'Prepare'],
      ['PrepareUninstallNightGate', '3106', 'Prepare'],
      ['RollbackInstallNightGate', '3362', 'Rollback'],
      ['RollbackUninstallNightGate', '3362', 'Rollback'],
      ['FinalizeNightGate', '3106', 'Install'],
      ['UninstallNightGate', '3106', 'Uninstall'],
      ['CommitNightGate', '3618', 'Commit'],
    ]) {
      assert.match(
        transactionActions.stdout,
        new RegExp(`${action}\\|${type}\\|"\\[PowerShellV1Folder\\]powershell\\.exe"[^\\r\\n]*-WindowStyle Hidden[^\\r\\n]*-Mode ${mode}`, 'i'),
      );
    }
    assert.doesNotMatch(transactionActions.stdout, /CustomActionData/i);

    const snapshotCleanup = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$queries=[ordered]@{`
      + `CleanupFile="SELECT \`\`FileName\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateRollbackSnapshot'";`
      + `CleanupMode="SELECT \`\`InstallMode\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateRollbackSnapshot'";`
      + `CleanupComponent="SELECT \`\`Component_\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateRollbackSnapshot'";`
      + `CleanupDirectory="SELECT \`\`Directory_\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_RollbackSnapshotCleanup'";`
      + `CleanupGuid="SELECT \`\`ComponentId\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_RollbackSnapshotCleanup'";`
      + `CleanupKeyPath="SELECT \`\`KeyPath\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_RollbackSnapshotCleanup'";`
      + `CleanupAttributes="SELECT \`\`Attributes\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_RollbackSnapshotCleanup'";`
      + `DataAttributes="SELECT \`\`Attributes\`\` FROM \`\`Component\`\` WHERE \`\`Component\`\`='C_NightGateData'";`
      + `CleanupParent="SELECT \`\`Directory_Parent\`\` FROM \`\`Directory\`\` WHERE \`\`Directory\`\`='NIGHTGATEINSTALLERSTATE'";`
      + `LegacyFile="SELECT \`\`FileName\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateLegacyMsiState'";`
      + `LegacyMode="SELECT \`\`InstallMode\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateLegacyMsiState'";`
      + `LegacyDirectory="SELECT \`\`DirProperty\`\` FROM \`\`RemoveFile\`\` WHERE \`\`FileKey\`\`='RemoveNightGateLegacyMsiState'";`
      + `PrepareUninstallSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='PrepareUninstallNightGate'";`
      + `PrepareUninstallCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='PrepareUninstallNightGate'";`
      + `RollbackUninstallSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackUninstallNightGate'";`
      + `RollbackUninstallCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackUninstallNightGate'";`
      + `UninstallSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='UninstallNightGate'";`
      + `UninstallCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='UninstallNightGate'";`
      + `RemoveFilesSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RemoveFiles'";`
      + `PrepareInstallSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='PrepareInstallNightGate'";`
      + `PrepareInstallCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='PrepareInstallNightGate'";`
      + `RollbackInstallSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackInstallNightGate'";`
      + `RollbackInstallCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='RollbackInstallNightGate'";`
      + `FinalizeSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='FinalizeNightGate'";`
      + `FinalizeCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='FinalizeNightGate'";`
      + `CommitSequence="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='CommitNightGate'";`
      + `CommitCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='CommitNightGate'"}; `
      + `foreach($name in $queries.Keys){$v=$d.OpenView($queries[$name]);$v.Execute();$r=$v.Fetch();`
      + `if($null -eq $r){throw "Missing $name"};"$name=$($r.StringData(1))";$v.Close()}`,
    );
    assert.equal(
      snapshotCleanup.status,
      0,
      snapshotCleanup.stderr || snapshotCleanup.stdout,
    );
    assert.match(snapshotCleanup.stdout, /CleanupFile=rollback-snapshot\.json/i);
    assert.match(snapshotCleanup.stdout, /CleanupMode=2/i);
    assert.match(snapshotCleanup.stdout, /CleanupComponent=C_RollbackSnapshotCleanup/i);
    assert.match(snapshotCleanup.stdout, /CleanupDirectory=NIGHTGATEINSTALLERSTATE/i);
    assert.match(snapshotCleanup.stdout, /CleanupGuid=\{B19C091A-1794-EA51-A267-874C0EC6B21E\}/i);
    assert.match(snapshotCleanup.stdout, /^CleanupKeyPath=$/im);
    assert.match(snapshotCleanup.stdout, /CleanupAttributes=256/i);
    assert.match(snapshotCleanup.stdout, /DataAttributes=272/i);
    assert.match(snapshotCleanup.stdout, /CleanupParent=NIGHTGATEDATA/i);
    assert.match(snapshotCleanup.stdout, /LegacyFile=msi-install-state\.json/i);
    assert.match(snapshotCleanup.stdout, /LegacyMode=2/i);
    assert.match(snapshotCleanup.stdout, /LegacyDirectory=NIGHTGATEDATA/i);
    assert.match(snapshotCleanup.stdout, /PrepareUninstallSequence=3370/i);
    assert.match(snapshotCleanup.stdout, /PrepareUninstallCondition=REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE/i);
    assert.match(snapshotCleanup.stdout, /RollbackUninstallSequence=3380/i);
    assert.match(snapshotCleanup.stdout, /RollbackUninstallCondition=REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE/i);
    assert.match(snapshotCleanup.stdout, /UninstallSequence=3400/i);
    assert.match(snapshotCleanup.stdout, /UninstallCondition=REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE/i);
    assert.match(snapshotCleanup.stdout, /RemoveFilesSequence=3500/i);
    assert.match(snapshotCleanup.stdout, /PrepareInstallSequence=4002/i);
    assert.match(snapshotCleanup.stdout, /PrepareInstallCondition=NOT REMOVE~="ALL"/i);
    assert.match(snapshotCleanup.stdout, /RollbackInstallSequence=4005/i);
    assert.match(snapshotCleanup.stdout, /RollbackInstallCondition=NOT REMOVE~="ALL"/i);
    assert.match(snapshotCleanup.stdout, /FinalizeSequence=4020/i);
    assert.match(snapshotCleanup.stdout, /FinalizeCondition=NOT REMOVE~="ALL"/i);
    assert.match(snapshotCleanup.stdout, /CommitSequence=6490/i);
    assert.match(snapshotCleanup.stdout, /CommitCondition=NOT REMOVE~="ALL"/i);
    assert.match(
      transactionActions.stdout,
      /RollbackInstallNightGate\|3362\|[^\r\n]*-ExpectedOperation Install[^\r\n]*-Mode Rollback/i,
    );
    assert.match(
      transactionActions.stdout,
      /RollbackUninstallNightGate\|3362\|[^\r\n]*-ExpectedOperation Uninstall[^\r\n]*-Mode Rollback/i,
    );

    const formattedFinalize = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$v=$d.OpenView("SELECT \`\`Target\`\` FROM \`\`CustomAction\`\` WHERE \`\`Action\`\`='FinalizeNightGate'"); `
      + `$v.Execute();$row=$v.Fetch();$target=$row.StringData(1);$v.Close(); `
      // This fixture intentionally uses the current release identity. Ignore
      // machine registration so the read-only formatting check also works
      // after that same NightGate version has been installed locally.
      + `$s=$i.OpenPackage(${quote(output)},1); `
      + `$null=$s.DoAction('CostInitialize');$null=$s.DoAction('FileCost');$null=$s.DoAction('CostFinalize'); `
      + `$record=$i.CreateRecord(1);$record.StringData(0)=$target; `
      + `"Target=$target";"Formatted=$($s.FormatRecord($record))"`,
    );
    assert.equal(
      formattedFinalize.status,
      0,
      formattedFinalize.stderr || formattedFinalize.stdout,
    );
    assert.match(
      formattedFinalize.stdout,
      /Formatted="?[A-Z]:\\[^\r\n]*\\powershell\.exe"?[\s\S]*-NonInteractive[\s\S]*-WindowStyle Hidden[\s\S]*-File\s+"[^"\r\n]*Finalize-NightGateMsi\.ps1"[\s\S]*-UserSid\s+"S-1-5-[^"]+"[\s\S]*-Mode Install/i,
      `deferred PowerShell command did not format to a complete noninteractive command:\n${formattedFinalize.stdout}`,
    );
    assert.doesNotMatch(formattedFinalize.stdout, /CustomActionData/i);

    const formattedCommand = formattedFinalize.stdout.match(/^Formatted=(.+)\r?$/m)?.[1];
    assert.ok(formattedCommand, formattedFinalize.stdout);
    const probePath = path.join(
      repo,
      'tests',
      'NightGate.Release.Tests',
      'fixtures',
      'PowerShellArgumentProbe.ps1',
    );
    const probeCommand = formattedCommand.replace(
      /-File\s+"[^"]+"/i,
      `-File "${probePath}"`,
    );
    const commandParts = probeCommand.match(/^"([^"]+)"\s+(.+)$/);
    assert.ok(commandParts, probeCommand);
    const rawBinding = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$psi=[Diagnostics.ProcessStartInfo]::new(); `
      + `$psi.FileName=${quote(commandParts[1])}; `
      + `$psi.Arguments=${quote(commandParts[2])}; `
      + `$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true; `
      + `$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true; `
      + `$p=[Diagnostics.Process]::Start($psi); `
      + `$stdout=$p.StandardOutput.ReadToEnd();$stderr=$p.StandardError.ReadToEnd(); `
      + `$p.WaitForExit();if($p.ExitCode -ne 0){[Console]::Error.Write($stderr);exit $p.ExitCode}; `
      + `[Console]::Out.Write($stdout)`,
    );
    assert.equal(rawBinding.status, 0, rawBinding.stderr || rawBinding.stdout);
    const boundArguments = JSON.parse(rawBinding.stdout.trim());
    assert.equal(boundArguments.InstallPath, 'C:\\Program Files\\NightGate\\');
    assert.equal(boundArguments.DataPath, 'C:\\ProgramData\\NightGate\\');
    assert.equal(boundArguments.ProductVersion, '0.3.17');
    assert.match(boundArguments.UserSid, /^S-1-5-/);
    assert.equal(boundArguments.Mode, 'Install');

    const osGate = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$queries=[ordered]@{`
      + `BuildSignature="SELECT \`\`Signature_\`\` FROM \`\`AppSearch\`\` WHERE \`\`Property\`\`='NIGHTGATEWINDOWSBUILD'";`
      + `BuildRoot="SELECT \`\`Root\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateWindowsBuild'";`
      + `BuildKey="SELECT \`\`Key\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateWindowsBuild'";`
      + `BuildName="SELECT \`\`Name\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateWindowsBuild'";`
      + `BuildType="SELECT \`\`Type\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateWindowsBuild'";`
      + `ProductSignature="SELECT \`\`Signature_\`\` FROM \`\`AppSearch\`\` WHERE \`\`Property\`\`='NIGHTGATEPRODUCTTYPE'";`
      + `ProductKey="SELECT \`\`Key\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateProductType'";`
      + `ProductName="SELECT \`\`Name\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateProductType'";`
      + `ProductType="SELECT \`\`Type\`\` FROM \`\`RegLocator\`\` WHERE \`\`Signature_\`\`='NightGateProductType'";`
      + `ExecuteAppSearch="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='AppSearch'";`
      + `ExecuteAppSearchCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='AppSearch'";`
      + `ExecuteLaunch="SELECT \`\`Sequence\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='LaunchConditions'";`
      + `ExecuteLaunchCondition="SELECT \`\`Condition\`\` FROM \`\`InstallExecuteSequence\`\` WHERE \`\`Action\`\`='LaunchConditions'";`
      + `UiAppSearch="SELECT \`\`Sequence\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='AppSearch'";`
      + `UiAppSearchCondition="SELECT \`\`Condition\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='AppSearch'";`
      + `UiLaunch="SELECT \`\`Sequence\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='LaunchConditions'";`
      + `UiLaunchCondition="SELECT \`\`Condition\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='LaunchConditions'";`
      + `UiExecuteAction="SELECT \`\`Sequence\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='ExecuteAction'";`
      + `UiExecuteActionCondition="SELECT \`\`Condition\`\` FROM \`\`InstallUISequence\`\` WHERE \`\`Action\`\`='ExecuteAction'";`
      + `RollbackRequired="SELECT \`\`Condition\`\` FROM \`\`LaunchCondition\`\` WHERE \`\`Description\`\`='NightGate requires Windows Installer rollback to be enabled.'"}; `
      + `foreach($name in $queries.Keys){$v=$d.OpenView($queries[$name]);$v.Execute();$r=$v.Fetch();"$name=$($r.StringData(1))";$v.Close()};`
      + `$s=$d.SummaryInformation(0);"Template=$($s.Property(7))"`,
    );
    assert.equal(osGate.status, 0, osGate.stderr || osGate.stdout);
    assert.match(osGate.stdout, /BuildSignature=NightGateWindowsBuild/i);
    assert.match(osGate.stdout, /BuildRoot=2/i);
    assert.match(osGate.stdout, /BuildKey=SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion/i);
    assert.match(osGate.stdout, /BuildName=CurrentBuildNumber/i);
    assert.match(osGate.stdout, /BuildType=18/i);
    assert.match(osGate.stdout, /ProductSignature=NightGateProductType/i);
    assert.match(osGate.stdout, /ProductKey=SYSTEM\\CurrentControlSet\\Control\\ProductOptions/i);
    assert.match(osGate.stdout, /ProductName=ProductType/i);
    assert.match(osGate.stdout, /ProductType=18/i);
    assert.match(osGate.stdout, /ExecuteAppSearch=400/i);
    assert.match(osGate.stdout, /ExecuteAppSearchCondition=\r?$/im);
    assert.match(osGate.stdout, /ExecuteLaunch=500/i);
    assert.match(osGate.stdout, /ExecuteLaunchCondition=\r?$/im);
    assert.match(osGate.stdout, /UiAppSearch=400/i);
    assert.match(osGate.stdout, /UiAppSearchCondition=\r?$/im);
    assert.match(osGate.stdout, /UiLaunch=500/i);
    assert.match(osGate.stdout, /UiLaunchCondition=\r?$/im);
    assert.match(osGate.stdout, /UiExecuteAction=1300/i);
    assert.match(osGate.stdout, /UiExecuteActionCondition=\r?$/im);
    assert.match(osGate.stdout, /RollbackRequired=NOT RollbackDisabled/i);
    assert.match(osGate.stdout, /Template=x64;2052/i);

    const evaluatedGate = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$i.UILevel=2; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$v=$d.OpenView("SELECT \`\`Condition\`\` FROM \`\`LaunchCondition\`\` WHERE \`\`Description\`\`='NightGate requires Windows 11 build 22000 or newer.'"); `
      + `$v.Execute();$r=$v.Fetch();$condition=$r.StringData(1);$v.Close(); `
      + `$s=$i.OpenPackage(${quote(output)},1); `
      + `$appSearch=$s.DoAction('AppSearch'); `
      + `$currentLaunch=$s.DoAction('LaunchConditions'); `
      + `"AppSearch=$appSearch"; `
      + `"Build=$($s.Property('NIGHTGATEWINDOWSBUILD'))"; `
      + `"Product=$($s.Property('NIGHTGATEPRODUCTTYPE'))"; `
      + `"CurrentLaunch=$currentLaunch"; `
      + `"Current=$($s.EvaluateCondition($condition))"; `
      + `$s.Property('NIGHTGATEWINDOWSBUILD')='19045'; `
      + `$oldLaunch=$s.DoAction('LaunchConditions'); `
      + `"OldLaunch=$oldLaunch"; `
      + `"OldBuild=$($s.EvaluateCondition($condition))"; `
      + `$s.Property('NIGHTGATEWINDOWSBUILD')='22000'; `
      + `$s.Property('NIGHTGATEPRODUCTTYPE')='ServerNT'; `
      + `$serverLaunch=$s.DoAction('LaunchConditions'); `
      + `"ServerLaunch=$serverLaunch"; `
      + `"Server=$($s.EvaluateCondition($condition))"; `
      + `$s.Property('NIGHTGATEWINDOWSBUILD')='19045'; `
      + `$s.Property('Installed')='1'; `
      + `$maintenanceLaunch=$s.DoAction('LaunchConditions'); `
      + `"MaintenanceLaunch=$maintenanceLaunch"; `
      + `"Maintenance=$($s.EvaluateCondition($condition))"`,
    );
    assert.equal(evaluatedGate.status, 0, evaluatedGate.stderr || evaluatedGate.stdout);
    assert.match(evaluatedGate.stdout, /Build=\d+/i);
    assert.match(evaluatedGate.stdout, /Product=WinNT/i);
    assert.match(evaluatedGate.stdout, /AppSearch=1/i);
    assert.match(evaluatedGate.stdout, /CurrentLaunch=1/i);
    assert.match(evaluatedGate.stdout, /Current=1/i);
    assert.match(evaluatedGate.stdout, /OldLaunch=3/i);
    assert.match(evaluatedGate.stdout, /OldBuild=0/i);
    assert.match(evaluatedGate.stdout, /ServerLaunch=3/i);
    assert.match(evaluatedGate.stdout, /Server=0/i);
    assert.match(evaluatedGate.stdout, /MaintenanceLaunch=1/i);
    assert.match(evaluatedGate.stdout, /Maintenance=1/i);

    const firstIdentity = runPowerShell(
      `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$v=$d.OpenView("SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductCode'");`
      + `$v.Execute();$r=$v.Fetch();$product=$r.StringData(1);$v.Close();`
      + `$s=$d.SummaryInformation(0);$package=$s.Property(9);"$product|$package"`,
    );
    assert.equal(firstIdentity.status, 0, firstIdentity.stderr || firstIdentity.stdout);

    const rebuilt = spawnSync('powershell.exe', [
      '-NoLogo',
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'scripts', 'New-NightGateMsi.ps1'),
      '-StageDirectory', stage,
      '-OutputPath', output,
      '-ProductVersion', '0.3.17',
    ], { cwd: repo, encoding: 'utf8' });
    assert.equal(rebuilt.status, 0, rebuilt.stderr || rebuilt.stdout);
    const secondIdentity = runPowerShell(
      `$i=New-Object -ComObject WindowsInstaller.Installer; `
      + `$d=$i.OpenDatabase(${quote(output)},0); `
      + `$v=$d.OpenView("SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductCode'");`
      + `$v.Execute();$r=$v.Fetch();$product=$r.StringData(1);$v.Close();`
      + `$s=$d.SummaryInformation(0);$package=$s.Property(9);"$product|$package"`,
    );
    assert.equal(secondIdentity.status, 0, secondIdentity.stderr || secondIdentity.stdout);
    const [firstProduct, firstPackage] = firstIdentity.stdout.trim().split('|');
    const [secondProduct, secondPackage] = secondIdentity.stdout.trim().split('|');
    assert.equal(secondProduct, firstProduct, 'same product version must keep ProductCode');
    assert.equal(firstProduct, '{B3242043-11C7-5151-96F7-3998961E0F6E}');
    assert.notEqual(firstProduct, '{41FC6A56-3186-5D57-A160-448177D02ACC}');
    assert.notEqual(secondPackage, firstPackage, 'every rebuilt MSI must receive a new PackageCode');
  } finally {
    await rm(stage, { recursive: true, force: true });
  }
});

test('installer service name matches the Windows service host while the IPC pipe remains stable', async () => {
  const host = await read('src/NightGate.Service/NightGateHost.cs');
  const wix = await read('installer/NightGate.wxs');
  const msiBuilder = await read('scripts/New-NightGateMsi.ps1');
  assert.match(host, /WindowsServiceName\s*=\s*"NightGate\.LocalService"/);
  assert.match(host, /PipeName\s*=\s*"NightGateService"/);
  assert.match(wix, /ServiceInstall[\s\S]{0,180}Name="NightGate\.LocalService"/i);
  assert.match(msiBuilder, /NightGateServiceInstall['"\s,]+NightGate\.LocalService/i);
});

test('MSI finalizer snapshots old registry values and persists the original target SID in protected machine state', async () => {
  const finalize = await read('installer/Finalize-NightGateMsi.ps1');
  const common = await read('installer/NightGate.Installation.Common.ps1');
  for (const required of [
    /Get-NightGateRegistryValueSnapshot/i,
    /Restore-NightGateRegistryValueSnapshot/i,
    /RegistryValueKind/i,
    /RegistryValueKind\]::None/i,
    /rollback-snapshot/i,
    /configuredWindowsUserSid/i,
    /installer-state/i,
    /legacyMachineStatePath/i,
    /msi-install-state\.json/i,
    /SetAccessRuleProtection\(\$true,\s*\$false\)/i,
    /S-1-5-18/i,
    /S-1-5-32-544/i,
    /Mode\s+-eq\s+['"]Commit['"]/i,
    /Mode\s+-eq\s+['"]Prepare['"]/i,
    /ExpectedOperation/i,
    /directoryAcls/i,
    /Get-NightGateFileSystemAclSnapshot/i,
    /Restore-NightGateFileSystemAclSnapshot/i,
  ]) {
    assert.match(`${finalize}\n${common}`, required);
  }
  assert.doesNotMatch(finalize, /Remove-ExactRegistryValue/i);
  assert.doesNotMatch(finalize, /\$null\s+-eq\s+\$key\s+-and\s+\(\$Writable\s+-or\s+\$Create\)/i);
  assert.match(finalize, /existingState[\s\S]{0,500}configuredWindowsUserSid/i);
  assert.match(finalize, /Mode\s+-eq\s+['"]Uninstall['"][\s\S]{0,600}configuredWindowsUserSid/i);
  assert.match(finalize, /operation[\s\S]{0,240}ExpectedOperation/i);
  assert.match(
    finalize,
    /directoryAcls\s*=\s*\[ordered\]@\{[\s\S]{0,360}\binstall\s*=\s*Get-NightGateFileSystemAclSnapshot\s+-Path\s+\$install/i,
  );
  assert.match(
    finalize,
    /Restore-NightGateFileSystemAclSnapshot\s+-Path\s+\$install[\s\S]{0,160}directoryAclsProperty\.Value\.install/i,
  );
});

test('MSI finalizer owns Chrome native-host registration in both registry views transactionally', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'nightgate-registry-views-'));
  const installPath = path.join(root, 'install', 'NightGate');
  const dataPath = path.join(root, 'data', 'NightGate');
  const finalizePath = path.join(repo, 'installer', 'Finalize-NightGateMsi.ps1');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  try {
    await mkdir(installPath, { recursive: true });
    await mkdir(dataPath, { recursive: true });

    const hiveProbe = runPowerShell(
      `$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; `
      + `$missing=$false; `
      + `foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,`
      + `[Microsoft.Win32.RegistryView]::Registry64)){`
      + `$users=[Microsoft.Win32.RegistryKey]::OpenBaseKey(`
      + `[Microsoft.Win32.RegistryHive]::Users,$view); `
      + `try{$key=$users.OpenSubKey("$sid\\Software",$false); `
      + `if($null -eq $key){$missing=$true}else{$key.Dispose()}}`
      + `finally{$users.Dispose()}}; `
      + `if($missing){exit 42}`,
    );
    if (hiveProbe.status === 42) {
      t.skip('the current SID hive is unavailable through HKEY_USERS in this sandbox');
      return;
    }
    assert.equal(hiveProbe.status, 0, hiveProbe.stderr || hiveProbe.stdout);

    // Dot-sourcing Prepare defines the real finalizer registry functions without
    // running an install or mutating application registration.
    const viewProbe = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `. ${quote(finalizePath)} -Mode Prepare -InstallPath ${quote(installPath)} `
      + `-DataPath ${quote(dataPath)} `
      + `-ProductCode '{A9E84CF4-B5E5-4BC2-95BB-0E1C8A0A07AE}' `
      + `-ProductVersion '0.3.12'; `
      + `$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; `
      + `$key32=Open-NightGateTargetUserKey -TargetSid $sid -SubKey 'Software' `
      + `-RegistryView ([Microsoft.Win32.RegistryView]::Registry32); `
      + `$key64=Open-NightGateTargetUserKey -TargetSid $sid -SubKey 'Software' `
      + `-RegistryView ([Microsoft.Win32.RegistryView]::Registry64); `
      + `try { "Open32=$($key32.View)"; "Open64=$($key64.View)" } `
      + `finally { $key32.Dispose(); $key64.Dispose() }; `
      + `$absent32=New-NightGateAbsentRegistrySnapshot -SubKey 'Software\\NightGate\\Tests' `
      + `-Name '' -RegistryView ([Microsoft.Win32.RegistryView]::Registry32); `
      + `$absent64=New-NightGateAbsentRegistrySnapshot -SubKey 'Software\\NightGate\\Tests' `
      + `-Name '' -RegistryView ([Microsoft.Win32.RegistryView]::Registry64); `
      + `"Snapshot32=$($absent32.registryView)"; `
      + `"Snapshot64=$($absent64.registryView)"`,
    );
    assert.equal(viewProbe.status, 0, viewProbe.stderr || viewProbe.stdout);
    assert.match(viewProbe.stdout, /Open32=Registry32/i);
    assert.match(viewProbe.stdout, /Open64=Registry64/i);
    assert.match(viewProbe.stdout, /Snapshot32=Registry32/i);
    assert.match(viewProbe.stdout, /Snapshot64=Registry64/i);

    const finalize = await read('installer/Finalize-NightGateMsi.ps1');
    assert.match(
      finalize,
      /function\s+Get-NightGateNativeHostRegistrySnapshots[\s\S]{0,420}registry32\s*=\s*Get-NightGateRegistryValueSnapshot[\s\S]{0,220}-RegistryView\s+\(?\[Microsoft\.Win32\.RegistryView\]::Registry32\)?[\s\S]{0,420}registry64\s*=\s*Get-NightGateRegistryValueSnapshot[\s\S]{0,220}-RegistryView\s+\(?\[Microsoft\.Win32\.RegistryView\]::Registry64\)?/i,
    );
    assert.match(
      finalize,
      /Set-NightGateTargetUserStringValue[\s\S]{0,260}\$nativeSubKey[\s\S]{0,220}Registry32[\s\S]{0,420}Set-NightGateTargetUserStringValue[\s\S]{0,260}\$nativeSubKey[\s\S]{0,220}Registry64/i,
    );
    assert.ok(
      (finalize.match(/Restore-NightGateNativeHostRegistrySnapshots/g) ?? []).length >= 3,
      'install rollback, uninstall, and compatibility paths must share dual-view restoration',
    );
    assert.match(finalize, /schemaVersion\s*=\s*3/i);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('MSI v0.3.6 state migration preserves the pre-upgrade Registry32 native-host value', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'nightgate-registry-migration-'));
  const installPath = path.join(root, 'install', 'NightGate');
  const dataPath = path.join(root, 'data', 'NightGate');
  const finalizePath = path.join(repo, 'installer', 'Finalize-NightGateMsi.ps1');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  try {
    await mkdir(installPath, { recursive: true });
    await mkdir(dataPath, { recursive: true });

    const migrationProbe = runPowerShell(
      `$ErrorActionPreference='Stop'; `
      + `. ${quote(finalizePath)} -Mode Prepare -InstallPath ${quote(installPath)} `
      + `-DataPath ${quote(dataPath)} `
      + `-ProductCode '{A9E84CF4-B5E5-4BC2-95BB-0E1C8A0A07AE}' `
      + `-ProductVersion '0.3.12'; `
      + `$legacy64=[pscustomobject]@{subKey='native';name='';wasPresent=$true;`
      + `kind='String';encodedValue='original-64'}; `
      + `$fresh32=[pscustomobject]@{subKey='native';name='';registryView='Registry32';`
      + `wasPresent=$true;kind='String';encodedValue='pre-upgrade-32'}; `
      + `$normalized=ConvertTo-NightGateNativeHostRegistrySnapshots `
      + `-Snapshots $legacy64 -LegacyRegistry32Snapshot $fresh32; `
      + `"Registry32=$($normalized.registry32.encodedValue)"; `
      + `"Registry32View=$($normalized.registry32.registryView)"; `
      + `"Registry64=$($normalized.registry64.encodedValue)"`,
    );
    assert.equal(
      migrationProbe.status,
      0,
      migrationProbe.stderr || migrationProbe.stdout,
    );
    assert.match(migrationProbe.stdout, /Registry32=pre-upgrade-32/i);
    assert.match(migrationProbe.stdout, /Registry32View=Registry32/i);
    assert.match(migrationProbe.stdout, /Registry64=original-64/i);

    const finalize = await read('installer/Finalize-NightGateMsi.ps1');
    assert.match(
      finalize,
      /-Snapshots\s+\$originalRegistry\.nativeHost[\s\S]{0,180}-LegacyRegistry32Snapshot[\s\S]{0,80}\$rollbackSnapshot\.registry\.nativeHost\.registry32/i,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('MSI prepare mode removes an abandoned rollback snapshot before any new transaction state', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'nightgate-prepare-'));
  const installPath = path.join(root, 'install', 'NightGate');
  const dataPath = path.join(root, 'data', 'NightGate');
  const snapshotPath = path.join(dataPath, 'installer-state', 'rollback-snapshot.json');
  try {
    await mkdir(installPath, { recursive: true });
    await mkdir(path.dirname(snapshotPath), { recursive: true });
    await writeFile(snapshotPath, '{"operation":"Install"}\n');

    const prepared = spawnSync('powershell.exe', [
      '-NoLogo',
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'installer', 'Finalize-NightGateMsi.ps1'),
      '-Mode', 'Prepare',
      '-InstallPath', installPath,
      '-DataPath', dataPath,
      '-ProductCode', '{A9E84CF4-B5E5-4BC2-95BB-0E1C8A0A07AE}',
      '-ProductVersion', '0.1.0',
    ], { cwd: repo, encoding: 'utf8' });

    assert.equal(prepared.status, 0, prepared.stderr || prepared.stdout);
    await assert.rejects(stat(snapshotPath), error => error?.code === 'ENOENT');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('MSI finalizer payload validation allows Windows Installer to own executable delivery', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'nightgate-finalize-payload-'));
  const installPath = path.join(root, 'install', 'NightGate');
  const dataPath = path.join(root, 'data', 'NightGate');
  const configPath = path.join(installPath, 'apps', 'Service', 'appsettings.json');
  const finalizePath = path.join(repo, 'installer', 'Finalize-NightGateMsi.ps1');
  const invoke = () => spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', finalizePath,
    '-Mode', 'Install',
    '-InstallPath', installPath,
    '-DataPath', dataPath,
    '-UserSid', 'S-1-5-21-100-200-300-400',
    '-ProductCode', '{A9E84CF4-B5E5-4BC2-95BB-0E1C8A0A07AE}',
    '-ProductVersion', '0.2.0',
    '-ValidatePayloadOnly',
  ], { cwd: repo, encoding: 'utf8' });

  try {
    await mkdir(path.dirname(configPath), { recursive: true });
    await mkdir(dataPath, { recursive: true });
    await writeFile(
      configPath,
      '{"NightGate":{"ConfiguredWindowsUserSid":"__CONFIGURED_WINDOWS_USER_SID__"}}\n',
    );

    const executableFilesMayStillBePending = invoke();
    assert.equal(
      executableFilesMayStillBePending.status,
      0,
      executableFilesMayStillBePending.stderr || executableFilesMayStillBePending.stdout,
    );

    await rm(configPath);
    const requiredConfigurationIsMissing = invoke();
    assert.notEqual(requiredConfigurationIsMissing.status, 0);
    assert.match(
      `${requiredConfigurationIsMissing.stderr}\n${requiredConfigurationIsMissing.stdout}`,
      /appsettings\.json/i,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('installer ACL rules bind SID identities without localized account-name translation', async () => {
  const commonPath = path.join(repo, 'installer', 'NightGate.Installation.Common.ps1');
  const finalize = await read('installer/Finalize-NightGateMsi.ps1');
  const fallback = await read('installer/Install-NightGate.ps1');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const probe = runPowerShell(
    `. ${quote(commonPath)}; `
    + `$inheritance=[Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'; `
    + `$propagation=[Security.AccessControl.PropagationFlags]::None; `
    + `$allow=[Security.AccessControl.AccessControlType]::Allow; `
    + `$acl=[Security.AccessControl.DirectorySecurity]::new(); `
    + `foreach($sid in @('S-1-5-18','S-1-5-32-544','S-1-5-19','S-1-5-21-100-200-300-400')){`
    + `$rule=New-NightGateSidFileSystemAccessRule -SidValue $sid -Rights FullControl `
    + `-InheritanceFlags $inheritance -PropagationFlags $propagation -AccessControlType $allow; `
    + `$acl.AddAccessRule($rule); `
    + `"$($rule.IdentityReference.GetType().FullName)|$($rule.IdentityReference.Value)"}`,
  );
  assert.equal(probe.status, 0, probe.stderr || probe.stdout);
  for (const sid of [
    'S-1-5-18',
    'S-1-5-32-544',
    'S-1-5-19',
    'S-1-5-21-100-200-300-400',
  ]) {
    assert.match(
      probe.stdout,
      new RegExp(`System\\.Security\\.Principal\\.SecurityIdentifier\\|${sid.replaceAll('-', '\\-')}`),
    );
  }
  assert.match(finalize, /New-NightGateSidFileSystemAccessRule/i);
  assert.match(fallback, /New-NightGateSidFileSystemAccessRule/i);
  assert.doesNotMatch(
    `${finalize}\n${fallback}`,
    /FileSystemAccessRule\]::new\(\s*(?:['"]S-1-5-|\$DesktopSid|\$sid\s*,)/i,
  );
});

test('installer ACL snapshots restore an exact pre-transaction directory descriptor', async () => {
  const commonPath = path.join(repo, 'installer', 'NightGate.Installation.Common.ps1');
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-acl-rollback-'));
  const filePath = path.join(directory, 'install-state.json');
  try {
    await writeFile(filePath, '{}\n');
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const probe = runPowerShell(
      `$ErrorActionPreference='Stop'; . ${quote(commonPath)}; `
      + `$before=Get-NightGateFileSystemAclSnapshot -Path ${quote(directory)}; `
      + `$acl=Get-Acl -LiteralPath ${quote(directory)}; `
      + `$acl.SetAccessRuleProtection($true,$false); `
      + `$inheritance=[Security.AccessControl.InheritanceFlags]::None; `
      + `$propagation=[Security.AccessControl.PropagationFlags]::None; `
      + `$allow=[Security.AccessControl.AccessControlType]::Allow; `
      + `$current=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; `
      + `$acl.AddAccessRule((New-NightGateSidFileSystemAccessRule -SidValue $current -Rights FullControl `
      + `-InheritanceFlags $inheritance -PropagationFlags $propagation -AccessControlType $allow)); `
      + `$acl.AddAccessRule((New-NightGateSidFileSystemAccessRule -SidValue 'S-1-5-21-100-200-300-400' -Rights Read `
      + `-InheritanceFlags $inheritance -PropagationFlags $propagation -AccessControlType $allow)); `
      + `Set-Acl -LiteralPath ${quote(directory)} -AclObject $acl; `
      + `$changed=Get-NightGateFileSystemAclSnapshot -Path ${quote(directory)}; `
      + `Restore-NightGateFileSystemAclSnapshot -Path ${quote(directory)} -Snapshot $before; `
      + `$after=Get-NightGateFileSystemAclSnapshot -Path ${quote(directory)}; `
      + `$beforeFile=Get-NightGateFileSystemAclSnapshot -Path ${quote(filePath)} -File; `
      + `$fileAcl=Get-Acl -LiteralPath ${quote(filePath)}; `
      + `$fileAcl.SetAccessRuleProtection($true,$false); `
      + `$fileAcl.AddAccessRule((New-NightGateSidFileSystemAccessRule -SidValue $current -Rights FullControl `
      + `-InheritanceFlags $inheritance -PropagationFlags $propagation -AccessControlType $allow)); `
      + `Set-Acl -LiteralPath ${quote(filePath)} -AclObject $fileAcl; `
      + `$changedFile=Get-NightGateFileSystemAclSnapshot -Path ${quote(filePath)} -File; `
      + `Restore-NightGateFileSystemAclSnapshot -Path ${quote(filePath)} -Snapshot $beforeFile; `
      + `$afterFile=Get-NightGateFileSystemAclSnapshot -Path ${quote(filePath)} -File; `
      + `"Changed=$($changed.sddl -ne $before.sddl)";"Restored=$($after.sddl -eq $before.sddl)"; `
      + `"FileChanged=$($changedFile.sddl -ne $beforeFile.sddl)";"FileRestored=$($afterFile.sddl -eq $beforeFile.sddl)"`,
    );
    assert.equal(probe.status, 0, probe.stderr || probe.stdout);
    assert.match(probe.stdout, /Changed=True/i);
    assert.match(probe.stdout, /Restored=True/i);
    assert.match(probe.stdout, /FileChanged=True/i);
    assert.match(probe.stdout, /FileRestored=True/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('installed service configuration replaces the staged SID placeholder in place', async () => {
  const commonPath = path.join(repo, 'installer', 'NightGate.Installation.Common.ps1');
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-installed-config-'));
  const configPath = path.join(directory, 'appsettings.json');
  const sid = 'S-1-5-21-100-200-300-400';
  try {
    await copyFile(
      path.join(repo, 'src', 'NightGate.Service', 'appsettings.sample.json'),
      configPath,
    );
    const quote = value => `'${value.replaceAll("'", "''")}'`;
    const result = runPowerShell(
      `. ${quote(commonPath)}; Set-NightGateServiceConfigurationSid `
      + `-InputPath ${quote(configPath)} -OutputPath ${quote(configPath)} `
      + `-DesktopSid ${quote(sid)}`,
    );
    assert.equal(result.status, 0, result.stderr || result.stdout);
    const text = await readFile(configPath, 'utf8');
    const config = JSON.parse(text);
    assert.equal(config.NightGate.ConfiguredWindowsUserSid, sid);
    assert.doesNotMatch(text, /__CONFIGURED_WINDOWS_USER_SID__/);

    const repairSid = 'S-1-5-21-100-200-300-401';
    const repair = runPowerShell(
      `. ${quote(commonPath)}; Set-NightGateServiceConfigurationSid `
      + `-InputPath ${quote(configPath)} -OutputPath ${quote(configPath)} `
      + `-DesktopSid ${quote(repairSid)}`,
    );
    assert.equal(repair.status, 0, repair.stderr || repair.stdout);
    const repaired = JSON.parse(await readFile(configPath, 'utf8'));
    assert.equal(repaired.NightGate.ConfiguredWindowsUserSid, repairSid);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('publish artifacts retain only the fixed target-install SID placeholder', async () => {
  const publish = await read('scripts/Publish.ps1');
  assert.match(publish, /__CONFIGURED_WINDOWS_USER_SID__/i);
  assert.doesNotMatch(publish, /Get-NightGateInteractiveDesktopSid/i);
  assert.doesNotMatch(publish, /ConfiguredWindowsUserSid/i);
});

test('optional MSI lifecycle harness is explicit VM-only evidence and default verification stays read-only', async () => {
  const lifecycle = await read('installer/Test-NightGateMsiLifecycle.ps1');
  const verify = await read('scripts/Verify.ps1');
  for (const required of [
    /RunLifecycle/i,
    /WindowsBuild/i,
    /22000/,
    /msiexec(?:\.exe)?/i,
    /\/i/i,
    /\/fa/i,
    /\/x/i,
    /msival2/i,
    /finally\s*\{/i,
  ]) {
    assert.match(lifecycle, required);
  }
  assert.match(verify, /read-only structural validation/i);
  assert.doesNotMatch(verify, /Test-NightGateMsiLifecycle\.ps1[^'"\r\n]*&/i);
});

test('read-only MSI verification inspects both upgrade directions and transaction action types', async () => {
  const verify = await read('scripts/Verify.ps1');
  for (const required of [
    /FROM\s+``Upgrade``[\s\S]{0,160}OLDPRODUCTS/i,
    /FROM\s+``Upgrade``[\s\S]{0,160}NEWERPRODUCTFOUND/i,
    /RollbackInstallNightGate[\s\S]{0,160}3362/i,
    /RollbackUninstallNightGate[\s\S]{0,160}3362/i,
    /CommitNightGate[\s\S]{0,160}3618/i,
    /RemoveNightGateRollbackSnapshot/i,
    /RemoveNightGateLegacyMsiState/i,
    /RollbackDisabled/i,
    /ExpectedOperation/i,
    /PackageCode/i,
    /UpgradeCode/i,
  ]) {
    assert.match(verify, required);
  }
});

test('MSI build and verification pin the complete transaction safety matrix', async () => {
  const sources = [
    await read('scripts/New-NightGateMsi.ps1'),
    await read('scripts/Verify.ps1'),
  ];
  const sequences = [
    ['PrepareUninstall', '3370'],
    ['RollbackUninstall', '3380'],
    ['Uninstall', '3400'],
    ['PrepareInstall', '4002'],
    ['RollbackInstall', '4005'],
    ['Finalize', '4020'],
    ['Commit', '6490'],
  ];
  const types = [
    ['PrepareInstall', '3106'],
    ['PrepareUninstall', '3106'],
    ['Finalize', '3106'],
    ['Uninstall', '3106'],
    ['RollbackInstall', '3362'],
    ['RollbackUninstall', '3362'],
    ['Commit', '3618'],
  ];
  for (const source of sources) {
    for (const [action, sequence] of sequences) {
      assert.match(
        source,
        new RegExp(`\\$[A-Za-z]*${action}[A-Za-z]*Sequence\\s+-ne\\s+['"]${sequence}['"]`, 'i'),
      );
    }
    for (const [action, type] of types) {
      assert.match(
        source,
        new RegExp(`\\$[A-Za-z]*${action}[A-Za-z]*Type\\s+-ne\\s+['"]${type}['"]`, 'i'),
      );
    }
    for (const action of ['PrepareUninstall', 'RollbackUninstall', 'Uninstall']) {
      assert.match(
        source,
        new RegExp(`\\$[A-Za-z]*${action}[A-Za-z]*Condition\\s+-ne[\\s\\S]{0,100}REMOVE~=\\"ALL\\" AND NOT UPGRADINGPRODUCTCODE`, 'i'),
      );
    }
    for (const action of ['PrepareInstall', 'RollbackInstall', 'Finalize', 'Commit']) {
      assert.match(
        source,
        new RegExp(`\\$[A-Za-z]*${action}[A-Za-z]*Condition\\s+-ne\\s+['"]NOT REMOVE~=\\"ALL\\"['"]`, 'i'),
      );
    }
  }
});

test('fallback installer is explicit, WhatIf-aware, SID-bound, recorded, and uninstall preserves data by default', async () => {
  const install = await read('installer/Install-NightGate.ps1');
  const uninstall = await read('installer/Uninstall-NightGate.ps1');
  const installationCommon = await read('installer/NightGate.Installation.Common.ps1');
  assert.match(install, /SupportsShouldProcess\s*=\s*\$true/i);
  assert.match(uninstall, /SupportsShouldProcess\s*=\s*\$true/i);
  assert.match(install, /WindowsPrincipal/i);
  assert.match(`${install}\n${installationCommon}`, /SecurityIdentifier/i);
  assert.match(install, /Get-NightGateInteractiveDesktopSid/i);
  assert.match(install, /install-state\.json/i);
  assert.match(uninstall, /install-state\.json/i);
  assert.match(install, /NT AUTHORITY\\LocalService/i);
  assert.match(install, /NativeMessagingHosts/i);
  assert.match(install, /CurrentVersion\\Run/i);
  assert.match(uninstall, /RemoveApplicationData/i);
  assert.match(uninstall, /if\s*\(\$RemoveApplicationData/i);
  assert.doesNotMatch(`${install}\n${uninstall}`, /shutdown\.exe|schtasks(?:\.exe)?/i);
});

test('ZIP fallback registers and restores the Chrome native host independently in both registry views', async () => {
  const install = await read('installer/Install-NightGate.ps1');
  const uninstall = await read('installer/Uninstall-NightGate.ps1');

  assert.match(
    install,
    /function\s+Get-NightGateNativeHostRegistrySnapshots[\s\S]{0,480}registry32\s*=\s*Get-NightGateRegistryValueSnapshot[\s\S]{0,240}-RegistryView\s+\(?\[Microsoft\.Win32\.RegistryView\]::Registry32\)?[\s\S]{0,480}registry64\s*=\s*Get-NightGateRegistryValueSnapshot[\s\S]{0,240}-RegistryView\s+\(?\[Microsoft\.Win32\.RegistryView\]::Registry64\)?/i,
  );
  assert.match(
    install,
    /Set-NightGateTargetUserStringValue[\s\S]{0,260}\$nativeSubKey[\s\S]{0,220}Registry32[\s\S]{0,480}Set-NightGateTargetUserStringValue[\s\S]{0,260}\$nativeSubKey[\s\S]{0,220}Registry64/i,
  );
  assert.match(install, /\$registryRollback\s*=\s*Get-NightGateNativeHostRegistrySnapshots/i);
  assert.match(install, /catch\s*\{[\s\S]{0,300}Restore-NightGateNativeHostRegistrySnapshots/i);
  assert.match(install, /originalRegistry[\s\S]{0,180}nativeHost/i);
  assert.match(install, /schemaVersion\s*=\s*2/i);
  assert.match(
    install,
    /\$nativeSubKey\s*=\s*['"]Software\\Google\\Chrome\\NativeMessagingHosts\\com\.nightgate\.host['"]/i,
  );
  assert.match(
    install,
    /Get-NightGateNativeHostRegistrySnapshots[\s\S]{0,140}-SubKey\s+\$nativeSubKey/i,
  );
  assert.doesNotMatch(
    install,
    /\$nativeSubKey\s*=\s*["']\$desktopSid\\Software\\Google/i,
    'the explicit registry-view helpers must receive a SID-relative subkey',
  );

  assert.match(uninstall, /schemaVersion\s+-notin\s+@\(1\s*,\s*2\)/i);
  assert.match(uninstall, /Restore-NightGateNativeHostRegistrySnapshots/i);
  assert.match(uninstall, /registry32[\s\S]{0,220}registry64/i);
  assert.match(
    uninstall,
    /\$nativeSubKey\s*=\s*['"]Software\\Google\\Chrome\\NativeMessagingHosts\\com\.nightgate\.host['"][\s\S]{0,180}\$legacyNativeSubKey\s*=\s*"\$\(\$state\.desktopUserSid\)\\\$nativeSubKey"/i,
  );
  assert.match(
    uninstall,
    /legacy[\s\S]{0,360}native[\s\S]{0,300}not touch/i,
    'legacy ZIP state must not guess that a pre-existing native-host value belonged to NightGate',
  );
});

test('target-install SID contract rejects missing or LocalService identity and writes an exact service config', async () => {
  const commonPath = path.join(repo, 'installer', 'NightGate.Installation.Common.ps1');
  const common = await read('installer/NightGate.Installation.Common.ps1');
  const publish = await read('scripts/Publish.ps1');
  const install = await read('installer/Install-NightGate.ps1');
  assert.match(common, /SecurityIdentifier/i);
  assert.match(common, /WTSQuerySessionInformation/i);
  assert.match(common, /WTSDomainName/i);
  assert.doesNotMatch(common, /WindowsIdentity[^\r\n]+GetCurrent[^\r\n]+User/i);
  assert.match(common, /S-1-5-19/);
  assert.match(common, /__CONFIGURED_WINDOWS_USER_SID__/);
  assert.match(publish, /appsettings\.sample\.json/i);
  assert.match(publish, /__CONFIGURED_WINDOWS_USER_SID__/i);
  assert.doesNotMatch(publish, /Get-NightGateInteractiveDesktopSid/i);
  assert.match(install, /Get-NightGateInteractiveDesktopSid/i);
  assert.match(install, /Set-NightGateServiceConfigurationSid/i);
  assert.doesNotMatch(
    install,
    /Get-NightGateServiceConfigurationSid\s+-Path\s+\$sourceServiceConfig/i,
  );
  assert.match(install, /S-1-5-19/);
  assert.match(install, /ReadAndExecute/i);
  assert.match(install, /-SidValue\s+\$DesktopSid\s+-Rights\s+ReadAndExecute/i);
  assert.doesNotMatch(install, /\$DesktopSid\s*,\s*\$\(if\s*\(\$Writable\)/i);

  const quote = value => `'${value.replaceAll("'", "''")}'`;
  for (const invalid of ['', 'S-1-5-19']) {
    const result = runPowerShell(
      `. ${quote(commonPath)}; ConvertTo-NightGateCanonicalDesktopSid -SidValue ${quote(invalid)}`,
    );
    assert.notEqual(result.status, 0, `SID should be rejected: ${invalid || '<missing>'}`);
  }

  const valid = runPowerShell(
    `. ${quote(commonPath)}; ConvertTo-NightGateCanonicalDesktopSid -SidValue 'S-1-5-21-100-200-300-400'`,
  );
  assert.equal(valid.status, 0, valid.stderr || valid.stdout);
  assert.match(valid.stdout, /S-1-5-21-100-200-300-400/);

  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-service-config-'));
  const output = path.join(directory, 'appsettings.json');
  try {
    const generate = runPowerShell(
      `. ${quote(commonPath)}; Write-NightGateServiceConfiguration `
      + `-TemplatePath ${quote(path.join(repo, 'src', 'NightGate.Service', 'appsettings.sample.json'))} `
      + `-OutputPath ${quote(output)} -DesktopSid 'S-1-5-21-100-200-300-400'`,
    );
    assert.equal(generate.status, 0, generate.stderr || generate.stdout);
    const configText = await readFile(output, 'utf8');
    const config = JSON.parse(configText);
    assert.equal(config.NightGate.ConfiguredWindowsUserSid, 'S-1-5-21-100-200-300-400');
    assert.doesNotMatch(configText, /__CONFIGURED_WINDOWS_USER_SID__/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('Chinese README states purpose, recovery, privacy, device guidance, limitations, and threat model', async () => {
  const readme = await read('README.md');
  for (const required of [
    '防冲动，不防蓄意拆除',
    '不诊断或治疗失眠',
    '构建与测试',
    '安装',
    '卸载',
    '恢复',
    '隐私',
    'Chrome',
    'iPhone',
    '旧关机任务',
    '限制',
    '手工验收',
  ]) {
    assert.ok(readme.includes(required), `README missing: ${required}`);
  }
});

test('a standalone Chinese user guide is shipped in every release package', async () => {
  const guide = await read('USER-GUIDE.zh-CN.md');
  const packageScript = await read('scripts/Package.ps1');
  for (const required of [
    '安装前确认',
    '安装与第一次打开',
    '完成首次设置',
    'Chrome 网页保护',
    '三种例外',
    '安装后自检',
    '故障排查',
    '卸载与恢复',
    '未签名',
  ]) {
    assert.ok(guide.includes(required), `user guide missing: ${required}`);
  }
  assert.match(packageScript, /USER-GUIDE\.zh-CN\.md/i);
  assert.match(
    packageScript,
    /Copy-Item[\s\S]{0,180}USER-GUIDE\.zh-CN\.md[\s\S]{0,120}-Destination/i,
  );
});

test('legacy shutdown regression never turns an unreadable System log into PASS', async () => {
  const regression = await read('scripts/Test-LegacyShutdownRegression.ps1');
  assert.match(regression, /Set-StrictMode\s+-Version\s+Latest/i);
  assert.match(regression, /Get-WinEvent[\s\S]{0,500}LogName\s*=\s*['"]System['"]/i);
  assert.match(regression, /Get-WinEvent[\s\S]{0,500}-ErrorAction\s+Stop/i);
  assert.doesNotMatch(regression, /Get-WinEvent[\s\S]{0,500}-ErrorAction\s+SilentlyContinue/i);
  assert.match(regression, /NoMatchingEventsFound/i);
  assert.match(regression, /INCONCLUSIVE:[\s\S]{0,300}exit\s+2/i);
  assert.match(regression, /Microsoft-Windows-TaskScheduler\/Operational/i);
  assert.match(regression, /1074[\s\S]{0,40}1075/i);
  assert.match(regression, /100[\s\S]{0,120}200[\s\S]{0,80}201[\s\S]{0,80}202/i);
});

test('scheduled-task probe verifier distinguishes PASS, FAIL, and stale evidence', async () => {
  const verifier = path.join(repo, 'scripts', 'Test-ScheduledShutdownTaskProbeResult.ps1');
  const source = await read('scripts/Test-ScheduledShutdownTaskProbeResult.ps1');
  assert.match(source, /Set-StrictMode\s+-Version\s+Latest/i);
  assert.match(source, /\$ErrorActionPreference\s*=\s*['"]Stop['"]/i);
  assert.doesNotMatch(source, /\b(?:Register|Unregister)-ScheduledTask\b/i);
  assert.doesNotMatch(source, /\bschtasks(?:\.exe)?\b/i);
  assert.doesNotMatch(source, /\b(?:Start-Process|Invoke-Item)\b/i);
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-task-probe-'));
  const probe = path.join(directory, 'probe.txt');
  const run = () => spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', verifier,
    '-ProbePath', probe,
    '-MinimumCheckedAtLocal', '2026-07-20T00:11:00+08:00',
    '-MaximumCheckedAtLocal', '2026-07-20T00:15:00+08:00',
    '-ForbiddenRunOnOrAfterLocal', '2026-07-20T00:00:00',
  ], { cwd: repo, encoding: 'utf8' });
  const fixture = ({ checkedAt, enabled = 'False', lastRun }) => [
    'PASS',
    'matchingTaskCount=1',
    'identity=TEST-PC\\Test User',
    `checkedAtLocal=${checkedAt}`,
    '---',
    'path=\\\u5b9a\u65f6\u5173\u673a',
    `enabled=${enabled}`,
    `lastRunTime=${lastRun}`,
    'command=C:\\Windows\\System32\\shutdown.exe',
    '',
  ].join('\n');
  try {
    await writeFile(probe, fixture({
      checkedAt: '2026-07-20T00:11:30+08:00',
      lastRun: '2026-07-19T00:10:01',
    }), 'utf8');
    const pass = run();
    assert.equal(pass.status, 0, pass.stderr || pass.stdout);
    assert.match(pass.stdout, /PASS:/i);

    await writeFile(probe, fixture({
      checkedAt: '2026-07-20T00:11:30+08:00',
      lastRun: '2026-07-20T00:10:01',
    }), 'utf8');
    const fail = run();
    assert.equal(fail.status, 1, fail.stderr || fail.stdout);
    assert.match(fail.stdout, /FAIL:/i);

    await writeFile(probe, fixture({
      checkedAt: '2026-07-19T19:43:54+08:00',
      lastRun: '2026-07-19T00:10:01',
    }), 'utf8');
    const stale = run();
    assert.equal(stale.status, 2, stale.stderr || stale.stdout);
    assert.match(stale.stdout, /INCONCLUSIVE:/i);

    await writeFile(probe, fixture({
      checkedAt: '2026-07-21T00:11:30+08:00',
      lastRun: '2026-07-19T00:10:01',
    }), 'utf8');
    const future = run();
    assert.equal(future.status, 2, future.stderr || future.stdout);
    assert.match(future.stdout, /INCONCLUSIVE:/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('scheduled-task probe verifier strictly evaluates Desktop JSON evidence', async () => {
  const verifier = path.join(repo, 'scripts', 'Test-ScheduledShutdownTaskProbeResult.ps1');
  const source = await read('scripts/Test-ScheduledShutdownTaskProbeResult.ps1');
  assert.match(source, /LocalApplicationData/i);
  assert.match(source, /legacy-shutdown-task-evidence\.json/i);
  assert.match(source, /MaximumCheckedAtLocal/i);
  const directory = await mkdtemp(path.join(tmpdir(), 'nightgate-task-evidence-'));
  const probe = path.join(directory, 'probe.json');
  const run = () => spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', verifier,
    '-ProbePath', probe,
    '-MinimumCheckedAtLocal', '2026-07-20T00:11:00+08:00',
    '-MaximumCheckedAtLocal', '2026-07-20T00:15:00+08:00',
    '-ForbiddenRunOnOrAfterLocal', '2026-07-20T00:00:00',
  ], { cwd: repo, encoding: 'utf8' });
  const fixture = ({
    checkedAtLocal = '2026-07-20T00:11:30+08:00',
    checkedAtUtc = '2026-07-19T16:11:30+00:00',
    probeDateLocal = '2026-07-20',
    status = 'complete',
    error = null,
    enabled = false,
    identityStatus = 'matchingDisabled',
    lastRunTimeLocal = '2026-07-19T00:10:01+08:00',
    lastRunTimeUtc = '2026-07-18T16:10:01+00:00',
    fingerprint = 'a'.repeat(64),
  } = {}) => ({
    schemaVersion: 1,
    probeDateLocal,
    checkedAtLocal,
    checkedAtUtc,
    status,
    error,
    tasks: [{
      migrationId: 'migration-001',
      taskPath: '\\定时关机',
      actionFingerprint: fingerprint,
      migrationStatus: 'disabled',
      identityStatus,
      enabled,
      lastRunTimeLocal,
      lastRunTimeUtc,
      lastTaskResult: 0,
    }],
  });
  try {
    await writeFile(probe, JSON.stringify(fixture()), 'utf8');
    const pass = run();
    assert.equal(pass.status, 0, pass.stderr || pass.stdout);
    assert.match(pass.stdout, /PASS:/i);

    await writeFile(probe, JSON.stringify(fixture({
      enabled: true,
      identityStatus: 'matchingEnabled',
    })), 'utf8');
    const fail = run();
    assert.equal(fail.status, 1, fail.stderr || fail.stdout);
    assert.match(fail.stdout, /FAIL:/i);

    await writeFile(probe, JSON.stringify(fixture({
      checkedAtLocal: '2026-07-19T23:59:59+08:00',
      checkedAtUtc: '2026-07-19T15:59:59+00:00',
      probeDateLocal: '2026-07-19',
    })), 'utf8');
    const stale = run();
    assert.equal(stale.status, 2, stale.stderr || stale.stdout);
    assert.match(stale.stdout, /INCONCLUSIVE:/i);

    await writeFile(probe, JSON.stringify(fixture({
      checkedAtLocal: '2026-07-21T00:11:30+08:00',
      checkedAtUtc: '2026-07-20T16:11:30+00:00',
      probeDateLocal: '2026-07-21',
    })), 'utf8');
    const futureNight = run();
    assert.equal(futureNight.status, 2, futureNight.stderr || futureNight.stdout);
    assert.match(futureNight.stdout, /INCONCLUSIVE:/i);

    await writeFile(probe, JSON.stringify(fixture({
      checkedAtLocal: '2026-07-20T23:59:00+08:00',
      checkedAtUtc: '2026-07-20T15:59:00+00:00',
    })), 'utf8');
    const futureSameDay = run();
    assert.equal(futureSameDay.status, 2, futureSameDay.stderr || futureSameDay.stdout);
    assert.match(futureSameDay.stdout, /INCONCLUSIVE:/i);

    await writeFile(probe, JSON.stringify(fixture({
      status: 'inconclusive',
      error: 'scheduler-read-unavailable',
    })), 'utf8');
    const inconclusive = run();
    assert.equal(inconclusive.status, 2, inconclusive.stderr || inconclusive.stdout);
    assert.match(inconclusive.stdout, /INCONCLUSIVE:/i);

    await writeFile(probe, JSON.stringify(fixture({ fingerprint: 'not-trusted' })), 'utf8');
    const malformed = run();
    assert.equal(malformed.status, 2, malformed.stderr || malformed.stdout);
    assert.match(malformed.stdout, /INCONCLUSIVE:/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('demo smoke is deterministic and explicitly non-mutating', async () => {
  const result = spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', path.join(repo, 'scripts', 'Invoke-DemoSmoke.ps1'),
    '-AsJson',
  ], { cwd: repo, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  const timeline = JSON.parse(result.stdout);
  assert.deepEqual(timeline.map(item => item.phase), [
    'Free', 'LastStart', 'Grace', 'LandingLocked', 'Morning',
  ]);
  assert.ok(timeline.every(item => item.mutatedMachine === false));
});
