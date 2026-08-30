import test from 'node:test';
import assert from 'node:assert/strict';

import {
  assertPrivacySafe,
  buildPrivacyEvent,
  localRedirect,
} from '../../src/NightGate.Chrome.Extension/lib/privacy.mjs';

test('privacy events contain exactly timestamp, event type, stable rule ID, and category', () => {
  const value = buildPrivacyEvent({
    timestamp: '2026-07-12T15:00:00.000Z',
    eventType: 'mediaPlaying',
    ruleId: 'video-1',
    category: 'video',
    url: 'https://example.com/private',
    title: 'secret title',
  });
  assert.deepEqual(value, {
    timestamp: '2026-07-12T15:00:00.000Z',
    eventType: 'mediaPlaying',
    ruleId: 'video-1',
    category: 'video',
  });
});

test('privacy event schema rejects unknown enum values and invalid identifiers', () => {
  const base = {
    timestamp: '2026-07-12T15:00:00.000Z', eventType: 'mediaPaused', ruleId: 'r', category: 'video',
  };
  assert.throws(() => buildPrivacyEvent({ ...base, eventType: 1 }));
  assert.throws(() => buildPrivacyEvent({ ...base, eventType: 'pageTitle' }));
  assert.throws(() => buildPrivacyEvent({ ...base, ruleId: '' }));
  assert.throws(() => buildPrivacyEvent({ ...base, category: 'medical' }));
});

test('recursive privacy assertion rejects forbidden fields at every depth', () => {
  const forbidden = ['url', 'host', 'path', 'query', 'fragment', 'referrer', 'title', 'source', 'currentSrc', 'pageText'];
  for (const key of forbidden) {
    assert.throws(() => assertPrivacySafe({ safe: [{ nested: { [key]: 'secret' } }] }), undefined, key);
    assert.throws(() => assertPrivacySafe({ [`original${key[0].toUpperCase()}${key.slice(1)}`]: 'secret' }), undefined, key);
  }
  assert.doesNotThrow(() => assertPrivacySafe({ timestamp: 'x', ruleId: 'r', category: 'video', sourceGeneration: 2 }));
});

test('blocked and finished redirects are local and never carry the original page', () => {
  for (const page of ['blocked', 'finished']) {
    const redirect = localRedirect(page);
    assert.equal(redirect, `/${page}.html`);
    assert.equal(redirect.includes('?'), false);
    assert.equal(redirect.includes('#'), false);
  }
  assert.throws(() => localRedirect('https://example.com'));
});
