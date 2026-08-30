import test from 'node:test';
import assert from 'node:assert/strict';

import {
  SITE_CATALOG,
  normalizeOptionsSelection,
  permissionOrigins,
} from '../../src/NightGate.Chrome.Extension/lib/options-model.mjs';

test('options accept only catalogued exact domains and remove duplicates', () => {
  const selected = normalizeOptionsSelection(['youtube.com', 'bilibili.com', 'youtube.com']);
  assert.deepEqual(selected, ['bilibili.com', 'youtube.com']);
  assert.throws(() => normalizeOptionsSelection(['evil.test']));
  assert.throws(() => normalizeOptionsSelection(['*.youtube.com']));
  assert.ok(SITE_CATALOG.every(site => site.label.length > 0 && site.domain.length > 0));
});

test('permission requests expand each selected domain to exact http/https roots only', () => {
  assert.deepEqual(permissionOrigins(['youtube.com']), [
    'http://youtube.com/*', 'https://youtube.com/*',
    'http://*.youtube.com/*', 'https://*.youtube.com/*',
  ]);
  assert.equal(permissionOrigins(['youtube.com']).includes('<all_urls>'), false);
  assert.equal(permissionOrigins(['youtube.com']).includes('*://*/*'), false);
});
