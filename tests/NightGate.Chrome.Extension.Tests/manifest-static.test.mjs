import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../src/NightGate.Chrome.Extension/', import.meta.url));
const manifestPath = path.join(root, 'manifest.json');

function filesUnder(directory) {
  return readdirSync(directory).flatMap(name => {
    const full = path.join(directory, name);
    return statSync(full).isDirectory() ? filesUnder(full) : [full];
  });
}

test('manifest is MV3 with fixed public key, module worker, minimal permissions, and no broad host access', () => {
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.background.type, 'module');
  assert.equal(existsSync(path.join(root, manifest.background.service_worker)), true);
  assert.match(manifest.key, /^[A-Za-z0-9+/]+={0,2}$/);
  assert.ok(manifest.key.length > 100);
  assert.deepEqual([...manifest.permissions].sort(), [
    'alarms', 'declarativeNetRequest', 'nativeMessaging',
    'scripting', 'storage', 'webNavigation',
  ].sort());
  assert.equal(manifest.permissions.includes('tabs'), false);
  assert.equal(manifest.permissions.includes('history'), false);
  assert.equal(JSON.stringify(manifest).includes('<all_urls>'), false);
  assert.equal(manifest.host_permissions, undefined);
  assert.ok(manifest.optional_host_permissions.every(pattern => !['*://*/*', 'http://*/*', 'https://*/*'].includes(pattern)));
});

test('manifest CSP and unpacked resources load only local checked-in files', () => {
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  assert.equal(manifest.content_security_policy.extension_pages, "script-src 'self'; object-src 'self'");
  const required = [
    manifest.background.service_worker, manifest.options_page,
    'content-script.js', 'lib/content-observer.js', 'blocked.html', 'blocked-page.js', 'finished.html',
  ];
  for (const relative of required) assert.equal(existsSync(path.join(root, relative)), true, relative);
  const worker = readFileSync(path.join(root, manifest.background.service_worker), 'utf8');
  assert.equal(worker.includes("./lib/worker-controller.mjs"), true);
  assert.equal(worker.includes("./lib/chrome-adapter.mjs"), true);
});

test('options page explains the required site permission handoff and immediate retry', () => {
  const page = readFileSync(path.join(root, 'options.html'), 'utf8');
  const script = readFileSync(path.join(root, 'options.js'), 'utf8');
  assert.match(page, /与“收尾”桌面端相同/u);
  assert.match(page, /保存并授权/u);
  assert.match(page, /请选择“允许”/u);
  assert.match(page, /无需等待 30 秒/u);
  assert.match(script, /nightGateSitePermissionsChanged/u);
});

test('module worker attaches its MV3 listeners synchronously at top level', () => {
  const worker = readFileSync(path.join(root, 'service-worker.js'), 'utf8');
  assert.match(worker, /^const listenerAdapter = attachChromeListeners\(/mu);
  assert.match(worker, /^void listenerAdapter\.start\(\)\.catch\(\(\) => \{\}\);/mu);
  assert.match(worker, /const monotonicEpochClock = \(\) => performance\.timeOrigin \+ performance\.now\(\);/u);
  assert.match(worker, /createChromeEffects\(chrome, \{ monotonicEpochClock \}\)/u);
  assert.match(worker, /monotonicEpochClock,/u);
});

test('legacy DNR planning is isolated from the production controller', () => {
  for (const relative of [
    'lib/media-reducer.mjs',
    'lib/dnr-planner.mjs',
  ]) {
    const source = readFileSync(path.join(root, relative), 'utf8');
    assert.equal(source.includes("from './effective-mode.mjs'"), true, relative);
    assert.equal(source.includes('function effectiveMode('), false, relative);
  }
  const controller = readFileSync(path.join(root, 'lib/worker-controller.mjs'), 'utf8');
  assert.equal(controller.includes('planSessionRules'), false);
  assert.equal(controller.includes('effects.replaceDnr([])'), true);
});

test('production code never requests a persistent restrictive DNR rule', () => {
  const controller = readFileSync(path.join(root, 'lib/worker-controller.mjs'), 'utf8');
  const adapter = readFileSync(path.join(root, 'lib/chrome-adapter.mjs'), 'utf8');
  assert.equal(controller.includes("from './dnr-planner.mjs'"), false);
  assert.match(adapter, /persistent DNR restrictions are not supported/u);
  const additions = [...adapter.matchAll(/addRules:\s*([^,}\r\n]+)/gu)]
    .map(match => match[1].trim());
  assert.ok(additions.length > 0);
  assert.deepEqual([...new Set(additions)], ['[]']);
});

test('static privacy and remote-code scan finds no dangerous permission, runtime, or remote script pattern', () => {
  const textFiles = filesUnder(root).filter(file => /\.(?:js|mjs|json|html|css)$/u.test(file));
  for (const file of textFiles) {
    const source = readFileSync(file, 'utf8');
    assert.equal(/<script[^>]+src=["']https?:/iu.test(source), false, file);
    assert.equal(/\b(?:eval|Function)\s*\(/u.test(source), false, file);
    assert.equal(/\bBuffer\b/u.test(source), false, file);
    assert.equal(/analytics|telemetry/iu.test(source), false, file);
    assert.equal(/chrome\.history/u.test(source), false, file);
  }
});

test('redirect pages never accept or reflect an original URL', () => {
  for (const name of ['blocked.html', 'blocked-page.js', 'finished.html']) {
    const source = readFileSync(path.join(root, name), 'utf8');
    for (const forbidden of ['location.search', 'location.hash', 'document.referrer', 'originalUrl']) {
      assert.equal(source.includes(forbidden), false, `${name}: ${forbidden}`);
    }
  }
});
