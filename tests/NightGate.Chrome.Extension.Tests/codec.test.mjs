import test from 'node:test';
import assert from 'node:assert/strict';

import {
  MAX_NATIVE_MESSAGE_BYTES,
  decodeNativeResponse,
  encodeNativeRequest,
  parsePolicy,
} from '../../src/NightGate.Chrome.Extension/lib/codec.mjs';

const token = 'A'.repeat(43);

function policy(overrides = {}) {
  return {
    revision: 7,
    gateId: 'gate-2026-07-12',
    evaluatedAtUtc: '2026-07-12T15:00:00.000Z',
    lastStartAtUtc: '2026-07-12T15:35:00.000Z',
    ttlMs: 45_000,
    mode: 'grandfatherOneMedia',
    lockAtUtc: '2026-07-12T16:10:00.000Z',
    wakeAtUtc: '2026-07-13T00:15:00.000Z',
    overrideKind: null,
    siteRules: [
      { ruleId: 'video-1', category: 'video', domain: 'example.com' },
    ],
    ...overrides,
  };
}

test('encodeNativeRequest emits only the fixed version-1 envelope', () => {
  const encoded = encodeNativeRequest({
    type: 'getPolicy',
    requestId: 'request-1',
    profileToken: token,
    payload: {},
  });

  assert.deepEqual(JSON.parse(encoded), {
    version: 1,
    type: 'getPolicy',
    requestId: 'request-1',
    profileToken: token,
    payload: {},
  });
});

test('encodeNativeRequest accepts only whitelisted exact request types and identifiers', () => {
  const payloads = {
    getPolicy: {},
    heartbeat: {
      revision: 7, extensionVersion: '0.1.0', incognitoAllowed: true, protectionReady: true,
    },
    mediaState: { timestamp: '2026-07-12T15:00:00.000Z', eventType: 'mediaPlaying', ruleId: 'r', category: 'video' },
    navigationAttempt: { timestamp: '2026-07-12T15:00:00.000Z', eventType: 'navigationBlocked', ruleId: 'r', category: 'video' },
  };
  for (const type of ['getPolicy', 'heartbeat', 'mediaState', 'navigationAttempt']) {
    assert.doesNotThrow(() => encodeNativeRequest({ type, requestId: 'x', profileToken: token, payload: payloads[type] }));
  }
  for (const type of ['GetPolicy', 'override', 'history', 1]) {
    assert.throws(() => encodeNativeRequest({ type, requestId: 'x', profileToken: token, payload: {} }));
  }
  for (const requestId of ['', 'x'.repeat(65), '\n', '中文']) {
    assert.throws(() => encodeNativeRequest({
      type: 'heartbeat', requestId, profileToken: token, payload: payloads.heartbeat,
    }));
  }
  for (const profileToken of ['A'.repeat(42), 'A'.repeat(44), 'A'.repeat(42) + '=', '*'.repeat(43)]) {
    assert.throws(() => encodeNativeRequest({
      type: 'heartbeat', requestId: 'x', profileToken, payload: payloads.heartbeat,
    }));
  }
});

test('native decoder enforces the 65,536-byte UTF-8 boundary', () => {
  const response = JSON.stringify({
    version: 1, type: 'heartbeatResult', requestId: 'x', profileToken: token, payload: { accepted: true },
  });
  const atLimit = `${' '.repeat(MAX_NATIVE_MESSAGE_BYTES - Buffer.byteLength(response))}${response}`;
  assert.doesNotThrow(() => decodeNativeResponse(atLimit, {
    requestType: 'heartbeat', requestId: 'x', profileToken: token,
  }));
  assert.throws(() => decodeNativeResponse(` ${atLimit}`, {
    requestType: 'heartbeat', requestId: 'x', profileToken: token,
  }));
});

test('outbound payload schemas reject unknown fields, numeric enums, and excessive strings', () => {
  const encode = (type, payload) => encodeNativeRequest({ type, requestId: 'x', profileToken: token, payload });
  assert.throws(() => encode('getPolicy', { extra: true }));
  assert.throws(() => encode('heartbeat', {}));
  assert.throws(() => encode('heartbeat', { revision: 1 }));
  const heartbeat = {
    revision: 1, extensionVersion: '0.1.0', incognitoAllowed: false, protectionReady: true,
  };
  assert.doesNotThrow(() => encode('heartbeat', heartbeat));
  for (const invalid of [
    { ...heartbeat, extra: true },
    { ...heartbeat, revision: -2 },
    { ...heartbeat, extensionVersion: '' },
    { ...heartbeat, extensionVersion: '1..2' },
    { ...heartbeat, extensionVersion: '1.2.3.4.5' },
    { ...heartbeat, extensionVersion: '65536' },
    { ...heartbeat, extensionVersion: '1a' },
    { ...heartbeat, incognitoAllowed: 'false' },
    { ...heartbeat, protectionReady: 'true' },
  ]) assert.throws(() => encode('heartbeat', invalid));
  assert.throws(() => encode('mediaState', {
    timestamp: '2026-07-12T15:00:00.000Z', eventType: 1, ruleId: 'r', category: 'video',
  }));
  assert.throws(() => encode('mediaState', {
    timestamp: '2026-07-12T15:00:00.000Z', eventType: 'mediaPlaying', ruleId: 'r'.repeat(65), category: 'video',
  }));
  assert.throws(() => encode('navigationAttempt', {
    timestamp: '2026-07-12T15:00:00.000Z', eventType: 'mediaPlaying', ruleId: 'r', category: 'video',
  }));
});

test('decodeNativeResponse rejects wrong correlation, type, version, fields, and duplicates', () => {
  const valid = {
    version: 1,
    type: 'getPolicyResult',
    requestId: 'request-1',
    profileToken: token,
    payload: policy(),
  };
  const decode = value => decodeNativeResponse(JSON.stringify(value), {
    requestType: 'getPolicy', requestId: 'request-1', profileToken: token,
  });
  assert.deepEqual(decode(valid).payload, policy());

  assert.throws(() => decode({ ...valid, version: '1' }));
  assert.throws(() => decode({ ...valid, version: 2 }));
  assert.throws(() => decode({ ...valid, type: 'heartbeatResult' }));
  assert.throws(() => decode({ ...valid, requestId: 'other' }));
  assert.throws(() => decode({ ...valid, profileToken: 'B'.repeat(43) }));
  assert.throws(() => decode({ ...valid, extra: true }));
  assert.throws(() => decodeNativeResponse(
    `{"version":1,"version":1,"type":"getPolicyResult","requestId":"request-1","profileToken":"${token}","payload":{}}`,
    { requestType: 'getPolicy', requestId: 'request-1', profileToken: token },
  ));
});

test('parsePolicy accepts all exact modes and normalizes site domains', () => {
  for (const mode of ['unrestricted', 'grandfatherOneMedia', 'blocked', 'fullOverride', 'failOpen']) {
    const parsed = parsePolicy(policy({ mode, siteRules: [{ ruleId: 'r', category: 'video', domain: 'EXAMPLE.COM.' }] }));
    assert.equal(parsed.mode, mode);
    assert.equal(parsed.siteRules[0].domain, 'example.com');
    assert.ok(Object.isFrozen(parsed));
  }
});

test('parsePolicy rejects unknown fields, numeric enums, malformed dates, domains, and bounds', () => {
  const invalid = [
    policy({ extra: 1 }),
    policy({ revision: -1 }), policy({ revision: 1.2 }),
    policy({ mode: 1 }), policy({ mode: 'Blocked' }),
    policy({ evaluatedAtUtc: '2026-07-12 15:00:00Z' }),
    policy({ evaluatedAtUtc: '2026-02-30T00:00:00.000Z' }),
    policy({ lastStartAtUtc: '2026-02-30T00:00:00.000Z' }),
    policy({ lockAtUtc: 'soon' }), policy({ wakeAtUtc: null }),
    policy({ ttlMs: 0 }), policy({ ttlMs: 120_001 }),
    policy({ overrideKind: 1 }), policy({ overrideKind: 'TeamRescue' }),
    policy({ siteRules: [{ ruleId: 'r', category: 'video', domain: '*.example.com' }] }),
    policy({ siteRules: [{ ruleId: 'r', category: 'secret-title', domain: 'example.com' }] }),
    policy({ siteRules: Array.from({ length: 101 }, (_, i) => ({ ruleId: `r${i}`, category: 'video', domain: `${i}.example.com` })) }),
  ];
  for (const value of invalid) assert.throws(() => parsePolicy(value));
});

test('parsePolicy rejects duplicate rule IDs and stale revisions', () => {
  assert.throws(() => parsePolicy(policy({ siteRules: [
    { ruleId: 'same', category: 'video', domain: 'a.example' },
    { ruleId: 'same', category: 'social', domain: 'b.example' },
  ] })));
  assert.throws(() => parsePolicy(policy({ revision: 6 }), { minimumRevision: 7 }));
});

test('parsePolicy rejects impossible schedule order and duplicate normalized domains', () => {
  assert.throws(() => parsePolicy(policy({
    lastStartAtUtc: '2026-07-12T16:10:00.000Z',
  })));
  assert.throws(() => parsePolicy(policy({
    wakeAtUtc: '2026-07-12T16:10:00.000Z',
  })));
  assert.throws(() => parsePolicy(policy({ siteRules: [
    { ruleId: 'one', category: 'video', domain: 'EXAMPLE.COM.' },
    { ruleId: 'two', category: 'video', domain: 'example.com' },
  ] })));
});
