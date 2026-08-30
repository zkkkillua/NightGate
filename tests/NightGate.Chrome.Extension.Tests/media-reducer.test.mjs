import test from 'node:test';
import assert from 'node:assert/strict';

import {
  createMediaState,
  mediaDecision,
  mediaKey,
  mediaReducer,
  projectMediaStorage,
  restoreMediaStorage,
} from '../../src/NightGate.Chrome.Extension/lib/media-reducer.mjs';

const before = Date.parse('2026-07-12T15:34:00.000Z');
const cutoff = Date.parse('2026-07-12T15:35:00.000Z');
const lock = Date.parse('2026-07-12T16:10:00.000Z');

const policy = (overrides = {}) => ({
  revision: 1,
  gateId: 'gate-1',
  mode: 'grandfatherOneMedia',
  overrideKind: null,
  evaluatedAtUtc: new Date(cutoff + 30_000).toISOString(),
  lastStartAtUtc: new Date(cutoff).toISOString(),
  lockAtUtc: new Date(lock).toISOString(),
  ...overrides,
});

const event = (overrides = {}) => ({
  type: 'media',
  tabId: 4,
  documentId: 'doc-a',
  mediaToken: 'media-a',
  sourceGeneration: 0,
  ruleId: 'video-1',
  playback: 'playing',
  receivedMonotonicMs: before,
  ...overrides,
});

function reduce(...actions) {
  return actions.reduce(mediaReducer, createMediaState());
}

test('grandfather transition grants the most recently playing pre-gate item', () => {
  const older = event({ tabId: 2, mediaToken: 'older', receivedMonotonicMs: before - 1000 });
  const newer = event({ tabId: 9, mediaToken: 'newer', receivedMonotonicMs: before });
  const state = reduce(older, newer, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.equal(state.grant.key, mediaKey(newer));
  assert.equal(mediaDecision(state, newer, cutoff), 'allow');
  assert.equal(mediaDecision(state, older, cutoff), 'pause');
});

test('candidate tie is resolved by stable lowest tab ID', () => {
  const high = event({ tabId: 10, mediaToken: 'high' });
  const low = event({ tabId: 3, mediaToken: 'low' });
  const state = reduce(high, low, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.equal(state.grant.tabId, 3);
});

test('pause and resume of the granted generation preserve its grant', () => {
  const playing = event();
  let state = reduce(playing, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  state = mediaReducer(state, event({ playback: 'paused', receivedMonotonicMs: cutoff + 1 }));
  assert.equal(state.grant.key, mediaKey(playing));
  state = mediaReducer(state, event({ playback: 'playing', receivedMonotonicMs: cutoff + 2 }));
  assert.equal(mediaDecision(state, playing, cutoff + 2), 'allow');
});

test('ended irreversibly consumes the gate and replay cannot re-grant', () => {
  let state = reduce(event(), { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  state = mediaReducer(state, event({ playback: 'ended', receivedMonotonicMs: cutoff + 1 }));
  assert.equal(state.grant, null);
  assert.deepEqual(state.consumedGateIds, ['gate-1']);

  state = mediaReducer(state, event({ playback: 'playing', receivedMonotonicMs: cutoff + 2 }));
  state = mediaReducer(state, { type: 'policy', policy: policy({ revision: 2 }), receivedMonotonicMs: cutoff + 30_002 });
  assert.equal(state.grant, null);
  assert.equal(mediaDecision(state, event(), cutoff + 2), 'pause');
});

test('changed source generation and a new element are next items and are blocked', () => {
  const original = event();
  const changedSource = event({ sourceGeneration: 1 });
  const newElement = event({ mediaToken: 'media-b' });
  let state = reduce(original, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  state = mediaReducer(state, changedSource);
  assert.equal(state.grant, null);
  assert.deepEqual(state.consumedGateIds, ['gate-1']);
  assert.equal(mediaDecision(state, changedSource, cutoff + 1), 'pause');
  assert.equal(mediaDecision(state, newElement, cutoff + 1), 'pause');
});

test('worker start after cutoff without a pre-gate candidate grants none', () => {
  const state = reduce({ type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.equal(state.grant, null);
  const autoplay = event({ receivedMonotonicMs: cutoff + 30_001 });
  assert.equal(mediaDecision(mediaReducer(state, autoplay), autoplay, cutoff + 30_001), 'pause');
});

test('media started after authoritative last-start cutoff but before first policy is never grandfathered', () => {
  const postGate = event({ receivedMonotonicMs: cutoff + 10_000 });
  const state = reduce(postGate, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.equal(state.grant, null);
  assert.equal(mediaDecision(state, postGate, cutoff + 30_000), 'pause');
});

test('an armed unrestricted policy locally enters grandfather mode at its monotonic last-start cutoff', () => {
  const beforeGatePolicy = policy({
    mode: 'unrestricted',
    evaluatedAtUtc: new Date(before).toISOString(),
    lastStartAtUtc: new Date(cutoff).toISOString(),
  });
  let state = reduce(
    event({ tabId: 4, mediaToken: 'current', receivedMonotonicMs: cutoff - 1 }),
    { type: 'policy', policy: beforeGatePolicy, receivedMonotonicMs: before },
  );

  const early = mediaReducer(state, { type: 'lastStart', nowMonotonicMs: cutoff - 1 });
  assert.equal(early.policy.mode, 'unrestricted');
  assert.equal(early.grant, null);

  state = mediaReducer(state, { type: 'lastStart', nowMonotonicMs: cutoff });
  assert.equal(state.policy.mode, 'grandfatherOneMedia');
  assert.equal(state.grant?.tabId, 4);
  assert.equal(state.grant?.mediaToken, 'current');
});

test('logical UTC policy deltas map to local monotonic deadlines despite wall-clock offset', () => {
  const shiftedPolicy = policy({
    evaluatedAtUtc: '2026-07-12T17:35:30.000Z',
    lastStartAtUtc: '2026-07-12T17:35:00.000Z',
    lockAtUtc: '2026-07-12T18:10:00.000Z',
  });
  const receivedMono = 1_000_000;
  const eligible = event({ receivedMonotonicMs: receivedMono - 30_001 });
  const tooLate = event({ tabId: 8, mediaToken: 'late', receivedMonotonicMs: receivedMono - 29_999 });
  const state = reduce(eligible, tooLate, {
    type: 'policy', policy: shiftedPolicy, receivedMonotonicMs: receivedMono,
  });
  assert.equal(state.grant.key, mediaKey(eligible));
  assert.equal(state.policy.localLastStartDeadlineMs, receivedMono - 30_000);
  assert.equal(state.policy.localLockDeadlineMs, receivedMono + 34.5 * 60_000);
  assert.equal(mediaDecision(state, eligible, state.policy.localLockDeadlineMs - 1), 'allow');
  assert.equal(mediaDecision(state, eligible, state.policy.localLockDeadlineMs), 'pause');
});

test('an exact repeated evaluation keeps its original monotonic cutoffs', () => {
  const snapshot = policy({ revision: 4 });
  const first = reduce(event(), {
    type: 'policy', policy: snapshot, receivedMonotonicMs: cutoff + 30_000,
  });

  const repeated = mediaReducer(first, {
    type: 'policy', policy: structuredClone(snapshot), receivedMonotonicMs: cutoff + 45_000,
  });

  assert.equal(repeated.policy.localLastStartDeadlineMs, first.policy.localLastStartDeadlineMs);
  assert.equal(repeated.policy.localLockDeadlineMs, first.policy.localLockDeadlineMs);
  assert.equal(repeated.grant?.key, first.grant?.key);
});

test('an equivalent policy renewal refreshes time without changing the semantic revision', () => {
  const snapshot = policy({ revision: 4 });
  const first = reduce(event(), {
    type: 'policy', policy: snapshot, receivedMonotonicMs: cutoff + 30_000,
  });
  const renewedPolicy = policy({
    revision: 4,
    evaluatedAtUtc: new Date(cutoff + 45_000).toISOString(),
  });

  const renewed = mediaReducer(first, {
    type: 'policy', policy: renewedPolicy, receivedMonotonicMs: cutoff + 45_000,
  });

  assert.equal(renewed.policy.revision, 4);
  assert.equal(renewed.policy.evaluatedAtUtc, renewedPolicy.evaluatedAtUtc);
  assert.equal(renewed.policy.localLastStartDeadlineMs, first.policy.localLastStartDeadlineMs);
  assert.equal(renewed.policy.localLockDeadlineMs, first.policy.localLockDeadlineMs);
  assert.equal(renewed.grant?.key, first.grant?.key);
});

test('new document, SPA route, and top-level navigation revoke before inheritance', () => {
  for (const kind of ['documentReplaced', 'spaNavigation', 'topNavigation']) {
    let state = reduce(event(), { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
    state = mediaReducer(state, { type: 'navigation', kind, tabId: 4, documentId: 'doc-a', nowMonotonicMs: cutoff + 1 });
    assert.equal(state.grant, null, kind);
    assert.deepEqual(state.consumedGateIds, ['gate-1'], kind);
  }
});

test('navigation removes stale playing candidates so a later gate cannot inherit an old document', () => {
  let state = reduce(event());
  state = mediaReducer(state, {
    type: 'navigation', kind: 'documentReplaced', tabId: 4, documentId: 'new-doc', nowMonotonicMs: cutoff,
  });
  state = mediaReducer(state, {
    type: 'policy', policy: policy({ gateId: 'gate-later' }), receivedMonotonicMs: cutoff + 30_000,
  });
  assert.equal(state.grant, null);
  assert.deepEqual(state.candidates, {});
});

test('closing a tab removes its stale playing candidate before the gate chooses a survivor', () => {
  let state = createMediaState();
  state = mediaReducer(state, event({ tabId: 4, mediaToken: 'closed', receivedMonotonicMs: cutoff - 1 }));
  state = mediaReducer(state, event({ tabId: 9, mediaToken: 'survivor', receivedMonotonicMs: cutoff - 2 }));

  state = mediaReducer(state, {
    type: 'navigation', kind: 'tabRemoved', tabId: 4, documentId: '', nowMonotonicMs: cutoff,
  });
  state = mediaReducer(state, {
    type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000,
  });

  assert.equal(state.grant?.tabId, 9);
  assert.equal(state.grant?.mediaToken, 'survivor');
});

test('exact lock cutoff and blocked or TeamRescue projection revoke immediately', () => {
  let state = reduce(event(), { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.equal(mediaDecision(state, event(), lock - 1), 'allow');
  state = mediaReducer(state, { type: 'tick', nowMonotonicMs: lock });
  assert.equal(mediaDecision(state, event(), lock), 'pause');

  for (const p of [
    policy({ mode: 'blocked' }),
    policy({ overrideKind: 'teamRescue' }),
    policy({ overrideKind: 'entertainment' }),
  ]) {
    const projected = reduce(event(), { type: 'policy', policy: p, receivedMonotonicMs: cutoff + 30_000 });
    assert.equal(projected.grant, null);
    assert.equal(mediaDecision(projected, event(), cutoff), 'pause');
  }
});

test('full override permits playback but expiry never re-grandfathers override media', () => {
  let state = reduce(event(), { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  state = mediaReducer(state, { type: 'policy', policy: policy({ revision: 2, mode: 'fullOverride', overrideKind: 'emergency' }), receivedMonotonicMs: cutoff + 30_001 });
  const duringOverride = event({ mediaToken: 'override-media', receivedMonotonicMs: cutoff + 2 });
  state = mediaReducer(state, duringOverride);
  assert.equal(mediaDecision(state, duringOverride, cutoff + 2), 'allow');

  state = mediaReducer(state, { type: 'policy', policy: policy({ revision: 3 }), receivedMonotonicMs: cutoff + 20 * 60_000 });
  assert.equal(state.grant, null);
  assert.equal(mediaDecision(state, duringOverride, cutoff + 20 * 60_000), 'pause');
});

test('TeamRescue blocks playback even when the service mode says fullOverride', () => {
  const playing = event();
  const state = reduce(playing, {
    type: 'policy',
    policy: policy({ mode: 'fullOverride', overrideKind: 'teamRescue' }),
    receivedMonotonicMs: cutoff + 30_000,
  });

  assert.equal(state.grant, null);
  assert.equal(mediaDecision(state, playing, cutoff + 30_000), 'pause');
});

test('session projection preserves bounded privacy-safe recent candidates across a pre-gate restart', () => {
  const old = event({ tabId: 90, mediaToken: 'old', receivedMonotonicMs: before - 7 * 60 * 60_000 });
  const actions = [old, ...Array.from({ length: 40 }, (_, index) => event({
    tabId: index + 1,
    mediaToken: `media-${index}`,
    receivedMonotonicMs: before - index,
  }))];
  const beforeRestart = actions.reduce(mediaReducer, createMediaState());
  const stored = projectMediaStorage(beforeRestart, { nowMonotonicMs: before, maxAgeMs: 6 * 60 * 60_000, maxCandidates: 32 });
  assert.equal(stored.latestPlayingCandidates.length, 32);
  assert.equal(JSON.stringify(stored).includes('old'), false);
  for (const forbidden of ['url', 'currentSrc', 'title', 'referrer']) {
    assert.equal(JSON.stringify(stored).includes(forbidden), false);
  }
  const restored = restoreMediaStorage(structuredClone(stored));
  const gated = mediaReducer(restored, { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  assert.notEqual(gated.grant, null);
});

test('session restore rebases candidate age when the worker monotonic clock restarts', () => {
  const priorClockNow = 10_000_000;
  const restartedClockNow = 100_000;
  const beforeGate = event({ receivedMonotonicMs: priorClockNow - 60_000 });
  const stored = projectMediaStorage(reduce(beforeGate), { nowMonotonicMs: priorClockNow });

  const restored = restoreMediaStorage(stored, { nowMonotonicMs: restartedClockNow });
  assert.equal(Object.values(restored.candidates)[0].lastPlayingMonotonicMs, restartedClockNow - 60_000);

  const gated = mediaReducer(restored, {
    type: 'policy', policy: policy(), receivedMonotonicMs: restartedClockNow,
  });
  assert.equal(gated.grant?.key, mediaKey(beforeGate));
});

test('session projection preserves grant and consumed metadata across restricted restart', () => {
  const state = reduce(event(), { type: 'policy', policy: policy(), receivedMonotonicMs: cutoff + 30_000 });
  const stored = projectMediaStorage(state);
  assert.deepEqual(Object.keys(stored).sort(), [
    'capturedAtMonotonicMs', 'consumedGateIds', 'grant', 'latestPlayingCandidates', 'policy',
  ].sort());
  const restored = restoreMediaStorage(structuredClone(stored));
  assert.equal(restored.grant.key, state.grant.key);
  assert.equal(mediaDecision(restored, event(), cutoff + 1), 'allow');

  const consumed = mediaReducer(restored, event({ playback: 'ended', receivedMonotonicMs: cutoff + 2 }));
  const restarted = restoreMediaStorage(projectMediaStorage(consumed));
  assert.equal(restarted.grant, null);
  assert.deepEqual(restarted.consumedGateIds, ['gate-1']);
});
