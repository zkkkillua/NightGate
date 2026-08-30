import test from 'node:test';
import assert from 'node:assert/strict';

import {
  DEGRADATION_TEXT,
  createHeartbeatState,
  heartbeatReducer,
  nextBackoffMs,
  projectHeartbeatStorage,
  restoreHeartbeatStorage,
} from '../../src/NightGate.Chrome.Extension/lib/heartbeat.mjs';

const start = Date.parse('2026-07-12T15:00:00.000Z');

test('valid policy establishes TTL and keeps normal visible status', () => {
  const state = heartbeatReducer(createHeartbeatState(), {
    type: 'validPolicy', nowMs: start, revision: 4, ttlMs: 30_000,
  });
  assert.equal(state.revision, 4);
  assert.equal(state.expiresAtMs, start + 30_000);
  assert.equal(state.degraded, false);
  assert.equal(state.statusText, '网页保护正常');
});

test('two consecutive transport failures enter fail-open degradation', () => {
  let state = heartbeatReducer(createHeartbeatState(), {
    type: 'validPolicy', nowMs: start, revision: 4, ttlMs: 60_000,
  });
  state = heartbeatReducer(state, { type: 'transportFailure', nowMs: start + 1_000 });
  assert.equal(state.degraded, false);
  state = heartbeatReducer(state, { type: 'transportFailure', nowMs: start + 2_000 });
  assert.equal(state.degraded, true);
  assert.equal(state.statusText, DEGRADATION_TEXT);
  assert.equal(state.clearProtection, true);
});

test('TTL expiry or 45 seconds without valid policy fails open on alarm tick', () => {
  let ttl = heartbeatReducer(createHeartbeatState(), {
    type: 'validPolicy', nowMs: start, revision: 1, ttlMs: 10_000,
  });
  ttl = heartbeatReducer(ttl, { type: 'tick', nowMs: start + 10_000 });
  assert.equal(ttl.degraded, true);

  let silent = heartbeatReducer(createHeartbeatState(), {
    type: 'validPolicy', nowMs: start, revision: 1, ttlMs: 120_000,
  });
  silent = heartbeatReducer(silent, { type: 'tick', nowMs: start + 45_000 });
  assert.equal(silent.degraded, true);
});

test('bounded exponential backoff increases and recovery resets it', () => {
  assert.deepEqual([0, 1, 2, 3, 4, 5, 99].map(nextBackoffMs), [1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000]);
  let state = createHeartbeatState();
  state = heartbeatReducer(state, { type: 'transportFailure', nowMs: start });
  assert.equal(state.nextAttemptAtMs, start + 1_000);
  state = heartbeatReducer(state, { type: 'transportFailure', nowMs: start + 1_000 });
  assert.equal(state.nextAttemptAtMs, start + 3_000);
  state = heartbeatReducer(state, { type: 'validPolicy', nowMs: start + 2_000, revision: 2, ttlMs: 45_000 });
  assert.equal(state.failureCount, 0);
  assert.equal(state.nextAttemptAtMs, start + 2_000);
  assert.equal(state.degraded, false);
});

test('service-worker restart restores heartbeat state without refreshing TTL', () => {
  let state = heartbeatReducer(createHeartbeatState(), {
    type: 'validPolicy', nowMs: start, revision: 9, ttlMs: 20_000,
  });
  state = heartbeatReducer(state, { type: 'transportFailure', nowMs: start + 2_000 });
  const restored = restoreHeartbeatStorage(projectHeartbeatStorage(state));
  assert.deepEqual(restored, state);
  const expired = heartbeatReducer(restored, { type: 'tick', nowMs: start + 20_000 });
  assert.equal(expired.degraded, true);
});
