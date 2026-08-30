import test from 'node:test';
import assert from 'node:assert/strict';

import {
  domainMatchesHost,
  normalizeDomain,
  normalizeUrlHost,
} from '../../src/NightGate.Chrome.Extension/lib/domain.mjs';

test('normalizeDomain lowercases, removes one trailing dot, and converts IDN', () => {
  assert.equal(normalizeDomain('EXAMPLE.COM.'), 'example.com');
  assert.equal(normalizeDomain('例子.测试'), 'xn--fsqu00a.xn--0zwm56d');
});

test('normalizeDomain matches the service IDN contract', () => {
  assert.equal(normalizeDomain('fa\u00df.de'), 'xn--fa-hia.de');
  assert.equal(normalizeDomain('\u03bf\u03b4\u03cc\u03c2.gr'), 'xn--pxavk3b.gr');
  assert.equal(normalizeDomain('\u03bf\u03b4\u03cc\u03c3.gr'), 'xn--pxavn9a.gr');
});

test('normalizeDomain rejects non-domain rules and invalid labels', () => {
  const invalid = [
    '', ' example.com', 'example.com ', 'https://example.com', 'example.com/path',
    'user@example.com', 'example.com:443', '*.example.com', '.example.com',
    'example..com', 'example.com..', '-example.com', 'example-.com', 'a'.repeat(64) + '.com',
    '127.0.0.1', '[::1]', 'exa_mple.com', 'xn--a.com', 'example.123', '999.999',
    'example.0x10', '999.0X10',
    'example.0x', '999.0X',
  ];

  for (const value of invalid) {
    assert.throws(() => normalizeDomain(value), { name: 'TypeError' }, value);
  }
});

test('domainMatchesHost matches exact hosts and subdomains only', () => {
  assert.equal(domainMatchesHost('example.com', 'example.com'), true);
  assert.equal(domainMatchesHost('watch.example.com', 'example.com'), true);
  assert.equal(domainMatchesHost('example.com.evil.test', 'example.com'), false);
  assert.equal(domainMatchesHost('notexample.com', 'example.com'), false);
  assert.equal(domainMatchesHost('example.co', 'example.com'), false);
});

test('normalizeUrlHost extracts only an http or https hostname', () => {
  assert.equal(normalizeUrlHost('https://Watch.Example.com/video?q=secret#part'), 'watch.example.com');
  assert.throws(() => normalizeUrlHost('file:///C:/secret'));
  assert.throws(() => normalizeUrlHost('javascript:alert(1)'));
  assert.throws(() => normalizeUrlHost('https://127.0.0.1/private'));
});
