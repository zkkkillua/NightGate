import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(
  new URL('../../src/NightGate.Chrome.Extension/options.js', import.meta.url),
  'utf8',
);

const permissionOrigins = domains => domains.flatMap(domain => [
  `http://${domain}/*`, `https://${domain}/*`,
  `http://*.${domain}/*`, `https://*.${domain}/*`,
]);

function element() {
  const listeners = new Map();
  return {
    textContent: '', children: [],
    append(...values) { this.children.push(...values); },
    replaceChildren(...values) { this.children = values; },
    addEventListener(type, listener) { listeners.set(type, listener); },
    listener(type) { return listeners.get(type); },
  };
}

function optionsPage({ approvedDomains, selectedDomains }) {
  const list = element();
  const form = element();
  const result = element();
  const status = element();
  const incognito = element();
  const calls = { requests: [], removals: [], saved: [], messages: [] };
  const stored = { approvedDomains: [...approvedDomains], protectionStatus: '等待授权' };
  form.querySelectorAll = () => selectedDomains.map(value => ({ value, checked: true }));
  const document = {
    querySelector(selector) {
      return {
        '#site-list': list, '#site-form': form, '#save-result': result,
        '#status': status, '#incognito': incognito,
      }[selector];
    },
    createElement: element,
  };
  const chrome = {
    storage: { local: {
      async get() { return structuredClone(stored); },
      async set(value) {
        Object.assign(stored, structuredClone(value));
        calls.saved.push(structuredClone(value));
      },
    } },
    permissions: {
      async request(value) { calls.requests.push(structuredClone(value)); return true; },
      async remove(value) { calls.removals.push(structuredClone(value)); return true; },
    },
    runtime: {
      async sendMessage(value) {
        calls.messages.push(structuredClone(value));
        stored.protectionStatus = '网页保护正常';
        return { ok: true };
      },
    },
    extension: { async isAllowedIncognitoAccess() { return true; } },
  };
  const catalog = [
    { domain: 'bilibili.com', label: '哔哩哔哩' },
    { domain: 'youtube.com', label: 'YouTube' },
  ];
  const transformed = source.replace(
    /^import .*?;\r?\n/u,
    `const SITE_CATALOG = ${JSON.stringify(catalog)};\n`
      + `const normalizeOptionsSelection = values => [...new Set(values)].sort();\n`
      + `const permissionOrigins = ${permissionOrigins.toString()};\n`,
  );
  vm.runInNewContext(transformed, { chrome, document, console });
  return { form, result, status, calls };
}

test('options save drops deselected site origins, retries the policy immediately, and refreshes status', async () => {
  const page = optionsPage({
    approvedDomains: ['bilibili.com', 'youtube.com'], selectedDomains: ['youtube.com'],
  });
  await new Promise(resolve => setImmediate(resolve));

  await page.form.listener('submit')({ preventDefault() {} });

  assert.deepEqual(page.calls.requests, [{ origins: permissionOrigins(['youtube.com']) }]);
  assert.deepEqual(page.calls.removals, [{ origins: permissionOrigins(['bilibili.com']) }]);
  assert.deepEqual(page.calls.saved, [{ approvedDomains: ['youtube.com'] }]);
  assert.deepEqual(page.calls.messages, [{ type: 'nightGateSitePermissionsChanged' }]);
  assert.equal(page.status.textContent, '网页保护正常');
  assert.equal(page.result.textContent, '已保存并授权。网页保护已立即重新检查。');
});
