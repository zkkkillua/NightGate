import test from 'node:test';
import assert from 'node:assert/strict';

import {
  createWorkerController,
  normalizeContentObservation,
} from '../../src/NightGate.Chrome.Extension/lib/worker-controller.mjs';

const baseTime = Date.parse('2026-07-12T15:34:00.000Z');
const profileToken = 'A'.repeat(43);
const policy = (overrides = {}) => ({
  revision: 1,
  gateId: 'gate-1',
  evaluatedAtUtc: '2026-07-12T15:35:00.000Z',
  lastStartAtUtc: '2026-07-12T15:35:00.000Z',
  ttlMs: 45_000,
  mode: 'unrestricted',
  lockAtUtc: '2026-07-12T16:10:00.000Z',
  wakeAtUtc: '2026-07-13T00:15:00.000Z',
  overrideKind: null,
  siteRules: [{ ruleId: 'video-1', category: 'video', domain: 'example.com' }],
  ...overrides,
});

function content(overrides = {}) {
  return {
    type: 'mediaObservation',
    mediaToken: 'media-a',
    sourceGeneration: 0,
    playback: 'playing',
    ...overrides,
  };
}

function sender(overrides = {}) {
  return {
    tab: { id: 4 },
    documentId: 'trusted-document',
    url: 'https://watch.example.com/private?q=secret',
    ...overrides,
  };
}

function harness({
  policies = [policy()], incognito = true, stored = null, loadSessionError = null, incognitoError = null,
  sitePreflight = null, siteApply = null, nativeSend = null, saveSessionErrorAt = null,
  extensionVersion = '0.1.0',
} = {}) {
  let now = baseTime;
  let monotonic = baseTime;
  let monotonicEpoch = baseTime;
  let index = 0;
  let dnrArmed = true;
  const calls = {
    dnr: [], cleanup: [], saved: [], statuses: [], native: [], scripts: [], preflights: [],
    scheduled: [], cancelled: [], paused: [], localPages: [],
    wakeups: [], clearedWakeups: 0, policyRequests: 0, order: [],
  };
  const transport = {
    async getPolicy() {
      calls.policyRequests += 1;
      calls.order.push('refresh');
      const item = policies[Math.min(index++, policies.length - 1)];
      if (item instanceof Error) throw item;
      return structuredClone(typeof item === 'function' ? await item() : item);
    },
    async send(type, payload) {
      calls.order.push(`native:${type}`);
      calls.native.push({ type, payload: structuredClone(payload) });
      const accepted = await nativeSend?.(type, structuredClone(payload));
      return accepted ?? true;
    },
  };
  const effects = {
    async clearDnr() { dnrArmed = false; calls.cleanup.push([]); },
    async loadSession() {
      if (loadSessionError) throw loadSessionError;
      return structuredClone(stored);
    },
    async saveSession(value) {
      if (calls.saved.length + 1 === saveSessionErrorAt) throw new Error('session write failed');
      calls.saved.push(structuredClone(value));
      stored = structuredClone(value);
    },
    async replaceDnr(rules) {
      if (rules.length) throw new TypeError('persistent DNR restrictions are not supported');
      const effectiveRules = dnrArmed ? rules : [];
      calls.dnr.push(structuredClone(effectiveRules));
      calls.order.push(effectiveRules.length ? 'dnr:set' : 'dnr:clear');
    },
    armDnr() { const wasArmed = dnrArmed; dnrArmed = true; calls.order.push('effects:arm'); return wasArmed; },
    disarmDnr() { dnrArmed = false; },
    async preflightSiteRules(rules) {
      calls.preflights.push(structuredClone(rules));
      await sitePreflight?.(structuredClone(rules), calls.preflights.length);
    },
    async applySiteRules(rules) {
      calls.scripts.push(structuredClone(rules));
      await siteApply?.(structuredClone(rules), calls.scripts.length);
    },
    async setStatus(text) { calls.statuses.push(text); },
    async isIncognitoAllowed() {
      if (incognitoError) throw incognitoError;
      return typeof incognito === 'function' ? incognito() : incognito;
    },
    async scheduleGrantPause(grant, gateId, delayMs, lease) {
      if (!dnrArmed) return;
      calls.order.push('media:schedule');
      calls.scheduled.push({ grant: structuredClone(grant), gateId, delayMs, lease: structuredClone(lease) });
    },
    async cancelGrantPause(grant, gateId) { calls.cancelled.push({ grant: structuredClone(grant), gateId }); },
    async pauseMediaTargets(targets, gateId, lease) {
      if (!dnrArmed) return;
      calls.paused.push({ targets: structuredClone(targets), gateId, lease: structuredClone(lease) });
    },
    async showLocalPage(target, page, lease) {
      calls.localPages.push({ target: structuredClone(target), page, lease: structuredClone(lease) });
    },
    async schedulePolicyWakeups(value) { calls.wakeups.push(structuredClone(value)); },
    async clearPolicyWakeups() { calls.clearedWakeups += 1; },
  };
  const controller = createWorkerController({
    wallClock: () => now,
    monotonicClock: () => monotonic,
    monotonicEpochClock: () => monotonicEpoch,
    transport,
    effects,
    profileToken,
    extensionVersion,
  });
  return {
    controller, calls,
    setNow(value) { now = value; monotonic = value; monotonicEpoch = value; },
    setWall(value) { now = value; },
    setMonotonic(value) { monotonic = value; monotonicEpoch = value; },
    setMonotonicEpoch(value) { monotonicEpoch = value; },
    async invalidateDnrAtEntry() {
      effects.disarmDnr();
      await effects.replaceDnr([]);
      await effects.setStatus('网页保护降级');
    },
    getStored() { return stored; },
  };
}

test('content observation trusts sender tab/document and locally matches its URL', () => {
  const normalized = normalizeContentObservation(content(), sender(), policy(), baseTime);
  assert.deepEqual(normalized, {
    type: 'media',
    tabId: 4,
    documentId: 'trusted-document',
    mediaToken: 'media-a',
    sourceGeneration: 0,
    ruleId: 'video-1',
    playback: 'playing',
    receivedMonotonicMs: baseTime,
    category: 'video',
  });
  assert.equal(JSON.stringify(normalized).includes('private'), false);
  assert.equal(JSON.stringify(normalized).includes('example.com'), false);
});

test('content observation rejects page-asserted tab, document, rule, URL, and unknown fields', () => {
  for (const extra of [
    { tabId: 99 }, { documentId: 'fake' }, { ruleId: 'fake' },
    { url: 'https://evil.test' }, { currentSrc: 'https://cdn.test/item' }, { extra: true },
  ]) {
    assert.throws(() => normalizeContentObservation(content(extra), sender(), policy(), baseTime));
  }
});

test('restrictive responses use a cross-context monotonic epoch deadline plus a bounded relative lease', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked' })] });
  h.setMonotonicEpoch(9_000_000);
  await h.controller.init();

  assert.deepEqual(await h.controller.onContentMessage(content(), sender()), {
    decision: 'pause',
    leaseMs: 45_000,
    leaseDeadlineMonotonicMs: 9_045_000,
  });
});

test('controller grants one pre-gate media item without installing persistent DNR', async () => {
  const h = harness({ policies: [policy(), policy({ revision: 2, mode: 'grandfatherOneMedia' })] });
  await h.controller.init();
  assert.equal((await h.controller.onContentMessage(content(), sender())).decision, 'allow');
  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();

  const state = h.controller.getState();
  assert.equal(state.media.grant.tabId, 4);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.equal((await h.controller.onContentMessage(content(), sender())).decision, 'allow');
  assert.equal(h.calls.scheduled.at(-1).delayMs, 35 * 60_000);
});

test('a new gate cancels the old timer even when it grants the same media identity', async () => {
  const h = harness({ policies: [
    policy(),
    policy({ revision: 2, mode: 'grandfatherOneMedia', gateId: 'gate-1' }),
    policy({ revision: 3, mode: 'grandfatherOneMedia', gateId: 'gate-2' }),
  ] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();
  const oldGrant = structuredClone(h.controller.getState().media.grant);
  const cancellationsBefore = h.calls.cancelled.length;

  await h.controller.refresh();

  const newGrant = h.controller.getState().media.grant;
  assert.equal(newGrant.key, oldGrant.key);
  assert.equal(newGrant.gateId, 'gate-2');
  assert.deepEqual(h.calls.cancelled.slice(cancellationsBefore), [{
    grant: oldGrant,
    gateId: 'gate-1',
  }]);
  assert.equal(h.calls.scheduled.at(-1).gateId, 'gate-2');
});

test('a playback event at an armed local last-start cutoff blocks the new item without waiting for an alarm', async () => {
  const localGatePolicy = policy({
    evaluatedAtUtc: new Date(baseTime).toISOString(),
    lastStartAtUtc: new Date(baseTime + 1_000).toISOString(),
    lockAtUtc: new Date(baseTime + 60_000).toISOString(),
    wakeAtUtc: new Date(baseTime + 8 * 60 * 60_000).toISOString(),
    ttlMs: 120_000,
  });
  const h = harness({ policies: [localGatePolicy] });
  await h.controller.init();
  assert.deepEqual(
    await h.controller.onContentMessage(
      content({ mediaToken: 'current' }),
      sender({ tab: { id: 4 } }),
    ),
    { decision: 'allow' },
  );

  h.setMonotonic(baseTime + 1_000);
  const blocked = await h.controller.onContentMessage(
      content({ mediaToken: 'new-item' }),
      sender({ tab: { id: 9 } }),
    );
  assert.deepEqual(blocked, {
    decision: 'pause',
    leaseMs: 44_000,
    leaseDeadlineMonotonicMs: baseTime + 45_000,
  });

  const state = h.controller.getState();
  assert.equal(state.media.policy.mode, 'grandfatherOneMedia');
  assert.equal(state.media.grant?.tabId, 4);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('grandfather transition immediately pauses every playing target except the selected grant', async () => {
  const h = harness({ policies: [policy(), policy({ revision: 2, mode: 'grandfatherOneMedia' })] });
  await h.controller.init();
  await h.controller.onContentMessage(content({ mediaToken: 'media-grant' }), sender({ tab: { id: 4 } }));
  await h.controller.onContentMessage(content({ mediaToken: 'media-other' }), sender({ tab: { id: 9 } }));

  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();

  assert.equal(h.controller.getState().media.grant?.mediaToken, 'media-grant');
  assert.deepEqual(h.calls.paused.at(-1), {
    gateId: 'gate-1',
    lease: {
      leaseMs: 45_000,
      leaseDeadlineMonotonicMs: Date.parse('2026-07-12T15:35:45.000Z'),
    },
    targets: [{
      tabId: 9,
      documentId: 'trusted-document',
      mediaToken: 'media-other',
      sourceGeneration: 0,
    }],
  });
  assert.equal(h.calls.scheduled.at(-1).grant.mediaToken, 'media-grant');
  assert.equal(h.calls.scheduled.at(-1).delayMs, 35 * 60_000);
});

test('grandfather transition with no eligible grant immediately pauses every playing target', async () => {
  const h = harness({ policies: [policy(), policy({
    revision: 2,
    mode: 'grandfatherOneMedia',
    lastStartAtUtc: '2026-07-12T15:34:30.000Z',
  })] });
  await h.controller.init();
  h.setNow(Date.parse('2026-07-12T15:34:31.000Z'));
  await h.controller.onContentMessage(content(), sender());

  h.setNow(Date.parse('2026-07-12T15:34:32.000Z'));
  await h.controller.refresh();

  assert.equal(h.controller.getState().media.grant, null);
  assert.deepEqual(h.calls.paused.at(-1), {
    gateId: 'gate-1',
    lease: {
      leaseMs: 45_000,
      leaseDeadlineMonotonicMs: Date.parse('2026-07-12T15:35:17.000Z'),
    },
    targets: [{
      tabId: 4,
      documentId: 'trusted-document',
      mediaToken: 'media-a',
      sourceGeneration: 0,
    }],
  });
  assert.equal(h.calls.scheduled.length, 0);
});

test('exact lock alarm pauses trusted active targets without waiting for another media event', async () => {
  const nearLock = '2026-07-12T15:35:44.000Z';
  const h = harness({ policies: [
    policy(), policy({ revision: 2, mode: 'grandfatherOneMedia', lockAtUtc: nearLock, ttlMs: 60_000 }),
  ] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();
  assert.equal(h.calls.paused.length, 0);

  h.setNow(Date.parse(nearLock));
  await h.controller.onAlarm({ kind: 'lock' });
  assert.equal(h.calls.paused.length, 1);
  assert.equal(h.calls.paused[0].targets[0].tabId, 4);
  assert.equal(h.calls.paused[0].targets[0].documentId, 'trusted-document');
  assert.equal(h.calls.paused[0].targets[0].mediaToken, 'media-a');
});

test('top-level navigation revokes exclusion before returning from notification', async () => {
  const h = harness({ policies: [policy(), policy({ revision: 2, mode: 'grandfatherOneMedia' })] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();
  await h.controller.onNavigation({ tabId: 4, frameId: 0, documentId: 'next-doc', url: 'https://example.com/next' });

  assert.equal(h.controller.getState().media.grant, null);
  assert.ok(h.calls.dnr.at(-1).every(rule => !('excludedTabIds' in rule.condition)));
});

test('tab removal discards its candidate even though Chrome supplies no frame identifier', async () => {
  const h = harness();
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender({ tab: { id: 4 } }));

  await h.controller.onNavigation({ tabId: 4, navigationKind: 'tabRemoved' });

  assert.deepEqual(h.controller.getState().media.candidates, {});
});

test('page-authored SPA messages are ignored because webNavigation is authoritative', async () => {
  const granted = harness({ policies: [policy(), policy({ revision: 2, mode: 'grandfatherOneMedia' })] });
  await granted.controller.init();
  await granted.controller.onContentMessage(content(), sender());
  granted.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await granted.controller.refresh();
  assert.deepEqual(
    await granted.controller.onContentMessage({ type: 'spaNavigation' }, sender()),
    { decision: 'allow' },
  );
  assert.equal(granted.controller.getState().media.grant?.mediaToken, 'media-a');

  const blocked = harness({ policies: [policy({ mode: 'blocked' })] });
  await blocked.controller.init();
  assert.deepEqual(
    await blocked.controller.onContentMessage({ type: 'spaNavigation' }, sender()),
    { decision: 'allow' },
  );
  assert.deepEqual(blocked.calls.localPages, []);
});

test('authoritative SPA navigation pauses the old grant then shows finished in that trusted document', async () => {
  const h = harness({ policies: [policy(), policy({ revision: 2, mode: 'grandfatherOneMedia' })] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await h.controller.refresh();

  await h.controller.onNavigation({
    tabId: 4,
    frameId: 0,
    documentId: 'trusted-document',
    navigationKind: 'spaNavigation',
    url: 'https://watch.example.com/private-route?q=secret#fragment',
  });

  assert.equal(h.controller.getState().media.grant, null);
  assert.deepEqual(h.calls.paused.at(-1), {
    gateId: 'gate-1',
    lease: {
      leaseMs: 45_000,
      leaseDeadlineMonotonicMs: Date.parse('2026-07-12T15:35:45.000Z'),
    },
    targets: [{
      tabId: 4,
      documentId: 'trusted-document',
      mediaToken: 'media-a',
      sourceGeneration: 0,
    }],
  });
  assert.deepEqual(h.calls.localPages, [{
    target: { tabId: 4, documentId: 'trusted-document' },
    page: 'finished',
    lease: {
      leaseMs: 45_000,
      leaseDeadlineMonotonicMs: Date.parse('2026-07-12T15:35:45.000Z'),
    },
  }]);
  const controlJson = JSON.stringify({ paused: h.calls.paused, localPages: h.calls.localPages });
  for (const secret of ['private-route', 'secret', 'fragment', 'title', 'currentSrc', 'cdn.invalid']) {
    assert.equal(controlJson.includes(secret), false, secret);
  }
});

test('authoritative SPA navigation projects a local page only for a restricted document', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked' })] });
  await h.controller.init();

  await h.controller.onNavigation({
    tabId: 8,
    frameId: 0,
    documentId: 'trusted-blocked-document',
    navigationKind: 'spaNavigation',
    url: 'https://example.com/private-route',
  });

  assert.deepEqual(h.calls.localPages, [{
    target: { tabId: 8, documentId: 'trusted-blocked-document' },
    page: 'blocked',
    lease: {
      leaseMs: 45_000,
      leaseDeadlineMonotonicMs: baseTime + 45_000,
    },
  }]);

  for (const mode of ['unrestricted', 'fullOverride', 'failOpen']) {
    const open = harness({ policies: [policy({ mode })] });
    await open.controller.init();
    await open.controller.onNavigation({
      tabId: 8,
      frameId: 0,
      documentId: 'trusted-open-document',
      navigationKind: 'spaNavigation',
      url: 'https://example.com/private-route',
    });
    assert.deepEqual(open.calls.localPages, [], mode);
  }
});

test('a blocked navigation that outlives TTL in native logging performs no restrictive page effect', async () => {
  let releaseNative;
  let nativeStarted;
  const nativeGate = new Promise(resolve => { releaseNative = resolve; });
  const nativeStartedGate = new Promise(resolve => { nativeStarted = resolve; });
  const h = harness({
    policies: [policy({ mode: 'blocked', ttlMs: 1_000 })],
    nativeSend: async type => {
      if (type === 'navigationAttempt') {
        nativeStarted();
        await nativeGate;
      }
    },
  });
  await h.controller.init();

  const handling = h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'new-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/private-route',
  });
  await nativeStartedGate;
  h.setMonotonic(baseTime + 1_000);
  releaseNative();
  await handling;

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.localPages, []);
  assert.deepEqual(h.calls.paused, []);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('a navigation racing the local last-start cutoff is replaced after commit by a leased local control', async () => {
  const localGatePolicy = policy({
    evaluatedAtUtc: new Date(baseTime).toISOString(),
    lastStartAtUtc: new Date(baseTime + 1_000).toISOString(),
    lockAtUtc: new Date(baseTime + 60_000).toISOString(),
    wakeAtUtc: new Date(baseTime + 8 * 60 * 60_000).toISOString(),
    ttlMs: 120_000,
  });
  const h = harness({ policies: [localGatePolicy] });
  await h.controller.init();
  h.setMonotonic(baseTime + 1_000);

  await h.controller.onNavigation({
    tabId: 8, frameId: 0, navigationKind: 'topNavigation',
    url: 'https://example.com/new-item',
  });
  await h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'new-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/new-item',
  });

  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.deepEqual(h.calls.localPages, [{
    target: { tabId: 8, documentId: 'new-document' },
    page: 'blocked',
    lease: {
      leaseMs: 44_000,
      leaseDeadlineMonotonicMs: baseTime + 45_000,
    },
  }]);
});

test('unrestricted navigation does not emit a blocked-navigation event', async () => {
  const h = harness({ policies: [policy({ mode: 'unrestricted' })] });
  await h.controller.init();
  await h.controller.onNavigation({ tabId: 4, frameId: 0, documentId: 'next-doc', url: 'https://example.com/next' });
  assert.equal(h.calls.native.some(item => item.type === 'navigationAttempt'), false);
});

test('controller blocks TeamRescue media even when the service mode says fullOverride', async () => {
  const h = harness({ policies: [policy({ mode: 'fullOverride', overrideKind: 'teamRescue' })] });
  await h.controller.init();

  const response = await h.controller.onContentMessage(content(), sender());

  assert.deepEqual(response, {
    decision: 'pause',
    leaseMs: 45_000,
    leaseDeadlineMonotonicMs: baseTime + 45_000,
  });
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('two policy transport failures clear DNR and grants and show Chinese degradation', async () => {
  const h = harness({ policies: [
    policy({ mode: 'grandfatherOneMedia' }), new Error('offline'), new Error('offline'),
  ] });
  await h.controller.init();
  await h.controller.refresh();
  await h.controller.refresh();
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('degraded protection neither records nor persists new page activity', async () => {
  const h = harness({ policies: [
    policy({ mode: 'grandfatherOneMedia' }), new Error('offline'), new Error('offline'),
  ] });
  await h.controller.init();
  await h.controller.refresh();
  await h.controller.refresh();
  assert.equal(h.controller.getState().health.degraded, true);
  const nativeBefore = h.calls.native.length;
  const savesBefore = h.calls.saved.length;

  assert.deepEqual(await h.controller.onContentMessage(content(), sender()), { decision: 'allow' });

  assert.equal(h.calls.native.length, nativeBefore);
  assert.equal(h.calls.saved.length, savesBefore);
});

test('blocked-page freshness handshake stays only while a restricted policy is fresh', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked', ttlMs: 1_000 })] });
  await h.controller.init();
  const blockedSender = sender({
    url: 'chrome-extension://abcdefghijklmnopabcdefghijklmnop/blocked.html',
  });

  assert.deepEqual(
    await h.controller.onContentMessage({ type: 'blockedPageFreshness' }, blockedSender),
    {
      decision: 'stayBlocked',
      leaseMs: 1_000,
      leaseDeadlineMonotonicMs: baseTime + 1_000,
    },
  );

  h.setMonotonic(baseTime + 1_000);
  assert.deepEqual(
    await h.controller.onContentMessage({ type: 'blockedPageFreshness' }, blockedSender),
    { decision: 'allow' },
  );
  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('blocked-page freshness refreshes the native policy before keeping a cached restriction', async () => {
  const h = harness({ policies: [
    policy({ revision: 1, mode: 'blocked' }),
    policy({ revision: 2, mode: 'unrestricted' }),
  ] });
  await h.controller.init();
  const blockedSender = sender({
    url: 'chrome-extension://abcdefghijklmnopabcdefghijklmnop/blocked.html',
  });

  assert.deepEqual(
    await h.controller.onContentMessage({ type: 'blockedPageFreshness' }, blockedSender),
    { decision: 'allow' },
  );
  assert.equal(h.calls.policyRequests, 2);
  assert.equal(h.controller.getState().media.policy.mode, 'unrestricted');
});

test('configured navigation refreshes the native policy before showing a cached blocked page', async () => {
  const h = harness({ policies: [
    policy({ revision: 1, mode: 'blocked' }),
    policy({ revision: 2, mode: 'unrestricted' }),
  ] });
  await h.controller.init();

  await h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'new-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/new-item',
  });

  assert.equal(h.calls.policyRequests, 2);
  assert.deepEqual(h.calls.localPages, []);
  assert.equal(h.calls.native.some(item => item.type === 'navigationAttempt'), false);
});

test('a page event after the policy freshness deadline fails open before recording or pausing', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked', ttlMs: 1_000 })] });
  await h.controller.init();
  const nativeBefore = h.calls.native.length;
  h.setMonotonic(baseTime + 1_000);

  assert.deepEqual(await h.controller.onContentMessage(content(), sender()), { decision: 'allow' });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.policy.mode, 'failOpen');
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.equal(h.calls.native.length, nativeBefore);
});

test('a navigation after the policy freshness deadline clears DNR before applying stale restrictions', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked', ttlMs: 1_000 })] });
  await h.controller.init();
  const nativeBefore = h.calls.native.length;
  h.setMonotonic(baseTime + 1_000);

  await h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'stale-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/new-item',
  });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.deepEqual(h.calls.localPages, []);
  assert.equal(h.calls.native.length, nativeBefore);
});

test('one stale policy response immediately fails open while transport failures remain two-strike', async () => {
  const h = harness({ policies: [
    policy({ revision: 2, mode: 'blocked' }),
    policy({ revision: 1, mode: 'blocked' }),
  ] });
  await h.controller.init();
  assert.equal(h.controller.getState().health.degraded, false);

  await h.controller.refresh();

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('a policy response whose TTL expires in flight fails open before restrictive effects', async () => {
  let releasePolicy;
  const policyGate = new Promise(resolve => { releasePolicy = resolve; });
  const evaluatedAtUtc = new Date(baseTime).toISOString();
  const h = harness({ policies: [
    policy({ revision: 1, mode: 'unrestricted', ttlMs: 120_000, evaluatedAtUtc }),
    async () => {
      await policyGate;
      return policy({ revision: 2, mode: 'grandfatherOneMedia', ttlMs: 1_000, evaluatedAtUtc });
    },
  ] });
  await h.controller.init();
  await h.controller.onContentMessage(content({ mediaToken: 'media-grant' }), sender({ tab: { id: 4 } }));
  await h.controller.onContentMessage(content({ mediaToken: 'media-loser' }), sender({ tab: { id: 9 } }));
  const before = {
    scripts: h.calls.scripts.length,
    paused: h.calls.paused.length,
    scheduled: h.calls.scheduled.length,
    dnr: h.calls.dnr.length,
  };

  const refreshing = h.controller.refresh();
  await new Promise(resolve => setImmediate(resolve));
  h.setNow(baseTime + 1_000);
  releasePolicy();

  assert.equal(await refreshing, false);
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.calls.scripts.length, before.scripts);
  assert.equal(h.calls.paused.length, before.paused);
  assert.equal(h.calls.scheduled.length, before.scheduled);
  assert.ok(h.calls.dnr.slice(before.dnr).every(rules => rules.length === 0));
});

test('site permission rejection preflights before policy commit or restrictive side effects', async () => {
  let rejectPreflight;
  const preflightGate = new Promise((_, reject) => { rejectPreflight = reject; });
  const h = harness({
    policies: [
      policy({
        revision: 1, mode: 'unrestricted', ttlMs: 120_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
      policy({ revision: 2, mode: 'grandfatherOneMedia', ttlMs: 120_000 }),
    ],
    sitePreflight: async (_rules, attempt) => {
      if (attempt === 2) await preflightGate;
    },
  });
  await h.controller.init();
  await h.controller.onContentMessage(content({ mediaToken: 'media-grant' }), sender({ tab: { id: 4 } }));
  await h.controller.onContentMessage(content({ mediaToken: 'media-loser' }), sender({ tab: { id: 9 } }));
  const before = {
    scripts: h.calls.scripts.length,
    paused: h.calls.paused.length,
    scheduled: h.calls.scheduled.length,
    wakeups: h.calls.wakeups.length,
    dnr: h.calls.dnr.length,
  };

  const refreshing = h.controller.refresh();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(h.controller.getState().policy.revision, 1);
  assert.equal(h.controller.getState().health.degraded, false);
  assert.deepEqual(await h.controller.onContentMessage(content(), sender()), { decision: 'allow' });
  assert.equal(h.calls.scripts.length, before.scripts);
  assert.equal(h.calls.paused.length, before.paused);
  assert.equal(h.calls.scheduled.length, before.scheduled);
  assert.equal(h.calls.wakeups.length, before.wakeups);
  assert.equal(h.calls.dnr.length, before.dnr);

  const rejection = new Error('Optional site permission is missing');
  rejection.failOpen = true;
  rejectPreflight(rejection);
  assert.equal(await refreshing, false);

  const failed = h.controller.getState();
  assert.equal(failed.policy.revision, 1);
  assert.equal(failed.health.degraded, true);
  assert.equal(failed.media.policy.mode, 'failOpen');
  assert.equal(h.calls.scripts.length, before.scripts);
  assert.equal(h.calls.paused.length, before.paused);
  assert.equal(h.calls.scheduled.length, before.scheduled);
  assert.equal(h.calls.localPages.length, 0);
  assert.ok(h.calls.dnr.slice(before.dnr).every(rules => rules.length === 0));
});

test('heartbeat reports protection not ready after a previously applied policy loses site permission', async () => {
  let rejectSitePermission = false;
  const h = harness({
    policies: [
      policy({
        revision: 1,
        ttlMs: 120_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
      policy({ revision: 2, ttlMs: 120_000 }),
    ],
    sitePreflight: async () => {
      if (!rejectSitePermission) return;
      const error = new Error('Optional site permission is missing');
      error.failOpen = true;
      throw error;
    },
  });

  await h.controller.init();
  rejectSitePermission = true;
  assert.equal(await h.controller.refresh(), false);
  assert.equal(h.controller.getState().policy.revision, 1);
  assert.equal(h.controller.getState().health.degraded, true);

  await h.controller.onAlarm({ kind: 'heartbeat' });

  assert.deepEqual(h.calls.native.filter(call => call.type === 'heartbeat').at(-1), {
    type: 'heartbeat',
    payload: {
      revision: 1,
      extensionVersion: '0.1.0',
      incognitoAllowed: true,
      protectionReady: false,
    },
  });
});

test('policy expiry during site preflight fails open before arming restrictive effects', async () => {
  let h;
  h = harness({
    policies: [
      policy({
        revision: 1, mode: 'unrestricted', ttlMs: 120_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
      policy({
        revision: 2, mode: 'grandfatherOneMedia', ttlMs: 1_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
    ],
    sitePreflight: async (_rules, attempt) => {
      if (attempt === 2) h.setMonotonic(baseTime + 1_000);
    },
  });
  await h.controller.init();
  const restrictiveBefore = h.calls.dnr.filter(rules => rules.length > 0).length;

  assert.equal(await h.controller.refresh(), false);
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.calls.dnr.filter(rules => rules.length > 0).length, restrictiveBefore);
  assert.equal(h.calls.scheduled.length, 0);
  assert.equal(h.calls.paused.length, 0);
});

test('policy expiry while document-start scripts are applied fails open before arming media controls', async () => {
  let h;
  h = harness({
    policies: [
      policy({
        revision: 1, mode: 'unrestricted', ttlMs: 120_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
      policy({
        revision: 2, mode: 'grandfatherOneMedia', ttlMs: 1_000,
        evaluatedAtUtc: new Date(baseTime).toISOString(),
      }),
    ],
    siteApply: async (_rules, attempt) => {
      if (attempt === 2) h.setMonotonic(baseTime + 1_000);
    },
  });
  await h.controller.init();
  const restrictiveBefore = h.calls.dnr.filter(rules => rules.length > 0).length;

  assert.equal(await h.controller.refresh(), false);
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.calls.dnr.filter(rules => rules.length > 0).length, restrictiveBefore);
  assert.equal(h.calls.scheduled.length, 0);
  assert.equal(h.calls.paused.length, 0);
});

test('a delayed media handler re-evaluates pause after concurrent policy expiry fails open', async () => {
  let releaseNative;
  let nativeStarted;
  const nativeGate = new Promise(resolve => { releaseNative = resolve; });
  const nativeStartedGate = new Promise(resolve => { nativeStarted = resolve; });
  const h = harness({
    policies: [
      policy({ revision: 1, mode: 'blocked', ttlMs: 1_000 }),
      new Error('offline'),
    ],
    nativeSend: async type => {
      if (type === 'mediaState') {
        nativeStarted();
        await nativeGate;
      }
    },
  });
  await h.controller.init();

  const handling = h.controller.onContentMessage(content(), sender());
  await nativeStartedGate;
  h.setMonotonic(baseTime + 1_000);
  await h.controller.onAlarm({ kind: 'policyExpiry' });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  releaseNative();
  assert.deepEqual(await handling, { decision: 'allow' });
});

test('a media handler that outlives TTL fails open without waiting for an expiry alarm', async () => {
  let releaseNative;
  let nativeStarted;
  const nativeGate = new Promise(resolve => { releaseNative = resolve; });
  const nativeStartedGate = new Promise(resolve => { nativeStarted = resolve; });
  const h = harness({
    policies: [policy({ revision: 1, mode: 'blocked', ttlMs: 1_000 })],
    nativeSend: async type => {
      if (type === 'mediaState') {
        nativeStarted();
        await nativeGate;
      }
    },
  });
  await h.controller.init();

  const handling = h.controller.onContentMessage(content(), sender());
  await nativeStartedGate;
  h.setMonotonic(baseTime + 1_000);
  releaseNative();

  assert.deepEqual(await handling, { decision: 'allow' });
  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('a rejected media privacy event immediately fails open and degrades protection', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => type === 'mediaState' ? false : true,
  });
  await h.controller.init();
  const savesBefore = h.calls.saved.length;
  const clearedWakeupsBefore = h.calls.clearedWakeups;

  const response = await h.controller.onContentMessage(content(), sender());

  assert.deepEqual(response, { decision: 'allow' });
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.equal(h.calls.saved.length, savesBefore + 1);
  assert.equal(h.calls.clearedWakeups, clearedWakeupsBefore + 1);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
  assert.equal(
    h.calls.native.filter(item => item.type === 'heartbeat').at(-1).payload.protectionReady,
    false,
  );
});

test('a failed media privacy event immediately fails open and degrades protection', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => {
      if (type === 'mediaState') throw new Error('event persistence unavailable');
      return true;
    },
  });
  await h.controller.init();
  const savesBefore = h.calls.saved.length;
  const clearedWakeupsBefore = h.calls.clearedWakeups;

  const response = await h.controller.onContentMessage(content(), sender());

  assert.deepEqual(response, { decision: 'allow' });
  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.equal(h.calls.saved.length, savesBefore + 1);
  assert.equal(h.calls.clearedWakeups, clearedWakeupsBefore + 1);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('a rejected navigation privacy event skips restrictive effects and degrades protection', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => type === 'navigationAttempt' ? false : true,
  });
  await h.controller.init();
  const savesBefore = h.calls.saved.length;
  const clearedWakeupsBefore = h.calls.clearedWakeups;

  await h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'new-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/private-route',
  });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.equal(h.calls.saved.length, savesBefore + 1);
  assert.equal(h.calls.clearedWakeups, clearedWakeupsBefore + 1);
  assert.deepEqual(h.calls.localPages, []);
  assert.deepEqual(h.calls.paused, []);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
  assert.equal(
    h.calls.native.filter(item => item.type === 'heartbeat').at(-1).payload.protectionReady,
    false,
  );
});

test('a failed navigation privacy event skips restrictive effects and degrades protection', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => {
      if (type === 'navigationAttempt') throw new Error('event persistence unavailable');
      return true;
    },
  });
  await h.controller.init();
  const savesBefore = h.calls.saved.length;
  const clearedWakeupsBefore = h.calls.clearedWakeups;

  await h.controller.onNavigation({
    tabId: 8, frameId: 0, documentId: 'new-document', navigationKind: 'documentReplaced',
    url: 'https://example.com/private-route',
  });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.equal(h.calls.saved.length, savesBefore + 1);
  assert.equal(h.calls.clearedWakeups, clearedWakeupsBefore + 1);
  assert.deepEqual(h.calls.localPages, []);
  assert.deepEqual(h.calls.paused, []);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('a runtime session-write failure abandons restrictive state and propagates for adapter cleanup', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    saveSessionErrorAt: 2,
  });
  await h.controller.init();

  await assert.rejects(
    () => h.controller.onContentMessage(content(), sender()),
    /session write failed/,
  );

  const state = h.controller.getState();
  assert.equal(state.health.degraded, true);
  assert.equal(state.media.policy.mode, 'failOpen');
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('a navigation session-write failure also abandons restriction and publishes degradation', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    saveSessionErrorAt: 2,
  });
  await h.controller.init();

  await assert.rejects(
    () => h.controller.onNavigation({ tabId: 4, navigationKind: 'tabRemoved' }),
    /session write failed/,
  );

  const state = h.controller.getState();
  assert.equal(state.health.degraded, true);
  assert.equal(state.media.policy.mode, 'failOpen');
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.statuses.at(-1).includes('网页保护降级'));
});

test('an exact repeated evaluation does not re-anchor cutoffs or renew health TTL', async () => {
  const snapshot = policy({ revision: 4, mode: 'blocked', ttlMs: 60_000 });
  const h = harness({ policies: [snapshot, structuredClone(snapshot)] });
  await h.controller.init();
  const before = h.controller.getState();
  const dnrCalls = h.calls.dnr.length;

  h.setMonotonic(baseTime + 10_000);
  await h.controller.refresh();

  const after = h.controller.getState();
  assert.equal(after.media.policy.localLastStartDeadlineMs, before.media.policy.localLastStartDeadlineMs);
  assert.equal(after.media.policy.localLockDeadlineMs, before.media.policy.localLockDeadlineMs);
  assert.equal(after.health.lastValidAtMs, before.health.lastValidAtMs);
  assert.equal(after.health.expiresAtMs, before.health.expiresAtMs);
  assert.equal(h.calls.dnr.length, dnrCalls);
});

test('a newer freshness evaluation with the same semantic revision stays ready', async () => {
  const first = policy({
    revision: 4,
    mode: 'blocked',
    evaluatedAtUtc: new Date(baseTime).toISOString(),
    lastStartAtUtc: new Date(baseTime + 60_000).toISOString(),
    lockAtUtc: new Date(baseTime + 120_000).toISOString(),
  });
  const renewal = {
    ...structuredClone(first),
    evaluatedAtUtc: new Date(baseTime + 30_000).toISOString(),
  };
  const h = harness({ policies: [first, renewal] });
  await h.controller.init();

  h.setNow(baseTime + 30_000);
  await h.controller.onAlarm({ kind: 'heartbeat' });

  const after = h.controller.getState();
  assert.equal(after.health.degraded, false);
  assert.equal(after.health.revision, 4);
  assert.equal(after.health.lastValidAtMs, baseTime + 30_000);
  assert.equal(after.policy.evaluatedAtUtc, renewal.evaluatedAtUtc);
  assert.deepEqual(h.calls.native.at(-1), {
    type: 'heartbeat',
    payload: {
      revision: 4,
      extensionVersion: '0.1.0',
      incognitoAllowed: true,
      protectionReady: true,
    },
  });
});

test('an equivalent renewal survives a local last-start projection', async () => {
  const first = policy({
    revision: 5,
    mode: 'unrestricted',
    evaluatedAtUtc: new Date(baseTime).toISOString(),
    lastStartAtUtc: new Date(baseTime + 1_000).toISOString(),
    lockAtUtc: new Date(baseTime + 120_000).toISOString(),
  });
  const renewal = {
    ...structuredClone(first),
    evaluatedAtUtc: new Date(baseTime + 500).toISOString(),
  };
  const h = harness({ policies: [first, renewal] });
  await h.controller.init();
  h.setNow(baseTime + 1_000);
  await h.controller.onContentMessage(content(), sender());
  assert.equal(h.controller.getState().media.policy.mode, 'grandfatherOneMedia');

  h.setNow(baseTime + 2_000);
  assert.equal(await h.controller.refresh(), true);

  const after = h.controller.getState();
  assert.equal(after.health.degraded, false);
  assert.equal(after.policy.revision, 5);
  assert.equal(after.media.policy.mode, 'grandfatherOneMedia');
  assert.equal(after.media.policy.evaluatedAtUtc, renewal.evaluatedAtUtc);
});

test('an exact evaluation replay at its established TTL fails open', async () => {
  const snapshot = policy({ revision: 4, mode: 'blocked', ttlMs: 10_000 });
  const h = harness({ policies: [snapshot, structuredClone(snapshot)] });
  await h.controller.init();

  h.setMonotonic(baseTime + 10_000);
  await h.controller.refresh();

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('policy acceptance schedules TTL and the next future last-start wakeup', async () => {
  const h = harness({ policies: [policy({
    revision: 4,
    mode: 'unrestricted',
    ttlMs: 30_000,
    lastStartAtUtc: '2026-07-12T15:36:00.000Z',
    lockAtUtc: '2026-07-12T15:37:00.000Z',
  })] });

  await h.controller.init();

  assert.deepEqual(h.calls.wakeups.at(-1), {
    expiryDelayMs: 30_000,
    lastStartDelayMs: 60_000,
    lockDelayMs: null,
  });
});

test('policy cutoffs use the service evaluation mapped at response receipt instead of request start', async () => {
  let h;
  h = harness({ policies: [async () => {
    h.setNow(baseTime + 8_000);
    return policy({
      revision: 4,
      mode: 'unrestricted',
      evaluatedAtUtc: new Date(baseTime + 7_000).toISOString(),
      lastStartAtUtc: new Date(baseTime + 17_000).toISOString(),
      lockAtUtc: new Date(baseTime + 27_000).toISOString(),
      wakeAtUtc: new Date(baseTime + 60_000).toISOString(),
    });
  }] });

  await h.controller.init();

  const state = h.controller.getState();
  assert.equal(state.health.lastValidAtMs, baseTime + 7_000);
  assert.equal(state.media.policy.localLastStartDeadlineMs, baseTime + 17_000);
  assert.equal(state.media.policy.localLockDeadlineMs, baseTime + 27_000);
  assert.deepEqual(h.calls.wakeups.at(-1), {
    expiryDelayMs: 44_000,
    lastStartDelayMs: 9_000,
    lockDelayMs: null,
  });
});

test('a cached service evaluation maps absolute cutoffs without adding snapshot age', async () => {
  const h = harness({ policies: [policy({
    revision: 4,
    mode: 'unrestricted',
    ttlMs: 45_000,
    evaluatedAtUtc: new Date(baseTime - 30_000).toISOString(),
    lastStartAtUtc: new Date(baseTime + 1_000).toISOString(),
    lockAtUtc: new Date(baseTime + 61_000).toISOString(),
    wakeAtUtc: new Date(baseTime + 3_600_000).toISOString(),
  })] });

  await h.controller.init();

  const state = h.controller.getState();
  assert.equal(state.health.lastValidAtMs, baseTime - 30_000);
  assert.equal(state.media.policy.localLastStartDeadlineMs, baseTime + 1_000);
  assert.equal(state.media.policy.localLockDeadlineMs, baseTime + 61_000);
  assert.deepEqual(h.calls.wakeups.at(-1), {
    expiryDelayMs: 15_000,
    lastStartDelayMs: 1_000,
    lockDelayMs: null,
  });
});

test('a cached unrestricted snapshot whose cutoff passed projects last-start without persistent DNR', async () => {
  const h = harness({ policies: [policy({
    revision: 4,
    mode: 'unrestricted',
    ttlMs: 45_000,
    evaluatedAtUtc: new Date(baseTime - 30_000).toISOString(),
    lastStartAtUtc: new Date(baseTime - 1_000).toISOString(),
    lockAtUtc: new Date(baseTime + 60_000).toISOString(),
    wakeAtUtc: new Date(baseTime + 3_600_000).toISOString(),
  })] });

  await h.controller.init();

  assert.equal(h.controller.getState().media.policy.mode, 'grandfatherOneMedia');
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('grandfather policy acceptance schedules TTL and its future lock wakeup', async () => {
  const h = harness({ policies: [policy({
    revision: 4,
    mode: 'grandfatherOneMedia',
    ttlMs: 30_000,
    lockAtUtc: '2026-07-12T15:36:00.000Z',
  })] });

  await h.controller.init();

  assert.deepEqual(h.calls.wakeups.at(-1), {
    expiryDelayMs: 30_000,
    lastStartDelayMs: null,
    lockDelayMs: 60_000,
  });
});

test('expiry wakeup clears protection before refresh and a stale replay cannot re-enable it', async () => {
  const restricted = policy({ revision: 2, mode: 'grandfatherOneMedia', ttlMs: 10_000 });
  const h = harness({ policies: [policy(), restricted, structuredClone(restricted)] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  await h.controller.refresh();
  assert.equal(h.controller.getState().media.grant?.mediaToken, 'media-a');
  h.calls.order.length = 0;

  h.setMonotonic(baseTime + 10_000);
  await h.controller.onAlarm({ kind: 'policyExpiry' });

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().media.grant, null);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.ok(h.calls.clearedWakeups >= 1);
  assert.ok(h.calls.order.indexOf('dnr:clear') < h.calls.order.indexOf('refresh'));
});

test('a queued expiry alarm from an older evaluation cannot degrade a newer fresh policy', async () => {
  const first = policy({ revision: 1, mode: 'blocked', ttlMs: 10_000 });
  const newer = policy({
    revision: 2,
    mode: 'blocked',
    ttlMs: 20_000,
    evaluatedAtUtc: '2026-07-12T15:35:05.000Z',
  });
  const h = harness({ policies: [first, newer] });
  await h.controller.init();
  h.setMonotonic(baseTime + 5_000);
  await h.controller.refresh();
  const dnrCallsBeforeAlarm = h.calls.dnr.length;

  h.setMonotonic(baseTime + 10_000);
  await h.controller.onAlarm({ kind: 'policyExpiry' });

  assert.equal(h.controller.getState().health.degraded, false);
  assert.equal(h.controller.getState().health.revision, 2);
  assert.equal(h.calls.policyRequests, 2);
  assert.equal(h.calls.dnr.length, dnrCallsBeforeAlarm);
});

test('a stale expiry entry restores local controls and renews the current grant lease for a newer fresh policy', async () => {
  const first = policy({ revision: 1, mode: 'unrestricted', ttlMs: 10_000 });
  const newer = policy({ revision: 2, mode: 'grandfatherOneMedia', ttlMs: 20_000 });
  const h = harness({ policies: [first, newer] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setMonotonic(baseTime + 5_000);
  await h.controller.refresh();
  const schedulesBeforeAlarm = h.calls.scheduled.length;
  const preflightsBeforeAlarm = h.calls.preflights.length;

  await h.invalidateDnrAtEntry();
  assert.deepEqual(h.calls.dnr.at(-1), []);
  h.setMonotonic(baseTime + 10_000);
  await h.controller.onAlarm({ kind: 'policyExpiry' });

  assert.equal(h.controller.getState().health.degraded, false);
  assert.equal(h.controller.getState().health.revision, 2);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.equal(h.calls.scheduled.length, schedulesBeforeAlarm + 1);
  assert.deepEqual(h.calls.scheduled.at(-1).lease, {
    leaseMs: 15_000,
    leaseDeadlineMonotonicMs: baseTime + 25_000,
  });
  assert.equal(h.calls.preflights.length, preflightsBeforeAlarm + 1);
  assert.equal(h.calls.statuses.at(-1), '网页保护正常');
});

test('a stale expiry entry cannot re-arm after site permission preflight is revoked', async () => {
  const denied = new Error('Optional site permission is missing');
  denied.failOpen = true;
  const h = harness({
    policies: [
      policy({ revision: 1, mode: 'unrestricted', ttlMs: 10_000 }),
      policy({ revision: 2, mode: 'grandfatherOneMedia', ttlMs: 20_000 }),
    ],
    sitePreflight: async (_rules, attempt) => {
      if (attempt === 3) throw denied;
    },
  });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setMonotonic(baseTime + 5_000);
  await h.controller.refresh();
  const schedulesBeforeAlarm = h.calls.scheduled.length;
  await h.invalidateDnrAtEntry();
  h.setMonotonic(baseTime + 10_000);

  await assert.rejects(() => h.controller.onAlarm({ kind: 'policyExpiry' }), /permission/);

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.calls.scheduled.length, schedulesBeforeAlarm);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('last-start wakeup refreshes immediately through backoff so the service can project grandfather', async () => {
  const initial = policy({
    revision: 1,
    mode: 'unrestricted',
    ttlMs: 60_000,
    lastStartAtUtc: '2026-07-12T15:35:01.000Z',
  });
  const projected = policy({
    revision: 2,
    mode: 'grandfatherOneMedia',
    ttlMs: 60_000,
    evaluatedAtUtc: '2026-07-12T15:35:01.000Z',
    lastStartAtUtc: '2026-07-12T15:35:01.000Z',
  });
  const h = harness({ policies: [initial, new Error('offline'), projected] });
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  h.setMonotonic(baseTime + 500);
  await h.controller.refresh();
  assert.equal(h.controller.getState().health.nextAttemptAtMs, baseTime + 1_500);

  h.setMonotonic(baseTime + 1_000);
  await h.controller.onAlarm({ kind: 'lastStart' });

  assert.equal(h.calls.policyRequests, 3);
  assert.equal(h.controller.getState().media.policy.mode, 'grandfatherOneMedia');
  assert.equal(h.controller.getState().media.grant?.mediaToken, 'media-a');
});

test('last-start wakeup transport failure immediately fails open', async () => {
  const initial = policy({
    revision: 1,
    mode: 'unrestricted',
    ttlMs: 60_000,
    lastStartAtUtc: '2026-07-12T15:35:01.000Z',
  });
  const h = harness({ policies: [initial, new Error('offline'), new Error('offline')] });
  await h.controller.init();
  h.setMonotonic(baseTime + 500);
  await h.controller.refresh();
  assert.equal(h.controller.getState().health.degraded, false);

  h.setMonotonic(baseTime + 1_000);
  await h.controller.onAlarm({ kind: 'lastStart' });

  assert.equal(h.calls.policyRequests, 3);
  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('a queued last-start alarm from an older policy cannot degrade an already restricted fresh policy', async () => {
  const initial = policy({
    revision: 1,
    mode: 'unrestricted',
    ttlMs: 60_000,
    lastStartAtUtc: '2026-07-12T15:35:10.000Z',
  });
  const restricted = policy({
    revision: 2,
    mode: 'grandfatherOneMedia',
    ttlMs: 60_000,
    evaluatedAtUtc: '2026-07-12T15:35:05.000Z',
  });
  const h = harness({ policies: [initial, restricted, new Error('stale alarm must not refresh')] });
  await h.controller.init();
  h.setMonotonic(baseTime + 5_000);
  await h.controller.refresh();

  h.setMonotonic(baseTime + 10_000);
  await h.controller.onAlarm({ kind: 'lastStart' });

  assert.equal(h.controller.getState().health.degraded, false);
  assert.equal(h.controller.getState().media.policy.mode, 'grandfatherOneMedia');
  assert.equal(h.calls.policyRequests, 2);
});

test('a changed snapshot with the same evaluation identity fails open without rewriting policy', async () => {
  const original = policy({ revision: 4, mode: 'blocked', ttlMs: 60_000 });
  const changed = policy({ revision: 4, mode: 'unrestricted', ttlMs: 60_000 });
  const h = harness({ policies: [original, changed] });
  await h.controller.init();

  h.setMonotonic(baseTime + 1_000);
  await h.controller.refresh();

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().policy.mode, 'blocked');
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('a newer revision with a backward evaluation time fails open', async () => {
  const h = harness({ policies: [
    policy({ revision: 4, mode: 'blocked', evaluatedAtUtc: '2026-07-12T15:35:00.000Z' }),
    policy({ revision: 5, mode: 'blocked', evaluatedAtUtc: '2026-07-12T15:34:59.000Z' }),
  ] });
  await h.controller.init();

  h.setMonotonic(baseTime + 1_000);
  await h.controller.refresh();

  assert.equal(h.controller.getState().health.degraded, true);
  assert.equal(h.controller.getState().policy.revision, 4);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('worker restart refreshes before enforcing and fails open when refresh is unavailable', async () => {
  const first = harness({ policies: [policy({ ttlMs: 10_000 })] });
  await first.controller.init();
  const stored = first.getStored();

  const restart = harness({ policies: [new Error('offline')], stored });
  restart.setNow(baseTime + 9_000);
  await restart.controller.init();
  assert.equal(restart.controller.getState().health.degraded, true);
  assert.deepEqual(restart.calls.dnr.at(-1), []);
});

test('worker restart rejects a persisted evaluation replay until a genuinely fresh evaluation arrives', async () => {
  const snapshot = policy({ revision: 4, mode: 'blocked', ttlMs: 120_000 });
  const first = harness({ policies: [snapshot] });
  await first.controller.init();

  const restart = harness({ policies: [structuredClone(snapshot)], stored: first.getStored() });
  restart.setMonotonic(100_000);
  await restart.controller.init();

  assert.equal(restart.controller.getState().health.degraded, true);
  assert.deepEqual(restart.calls.dnr.at(-1), []);
});

test('controller initialization clears DNR before a session load failure', async () => {
  const h = harness({ loadSessionError: new Error('session unavailable') });

  await assert.rejects(() => h.controller.init(), /session unavailable/);

  assert.deepEqual(h.calls.cleanup, [[]]);
  assert.deepEqual(h.calls.dnr, []);
});

test('controller initialization clears DNR before an incognito query failure', async () => {
  const h = harness({ incognitoError: new Error('incognito unavailable') });

  await assert.rejects(() => h.controller.init(), /incognito unavailable/);

  assert.deepEqual(h.calls.cleanup, [[]]);
  assert.deepEqual(h.calls.dnr, []);
});

test('startup compatibility heartbeat failure fails open before requesting policy', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => {
      if (type === 'heartbeat') throw new Error('health publication unavailable');
    },
  });

  await assert.rejects(() => h.controller.init(), /health publication unavailable/);

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.equal(h.calls.policyRequests, 0);
  assert.deepEqual(h.calls.paused, []);
  assert.deepEqual(h.calls.scheduled, []);
  assert.deepEqual(h.calls.localPages, []);
  assert.ok(h.calls.clearedWakeups >= 1);
});

test('a rejected startup compatibility heartbeat cannot reach policy or restrictive effects', async () => {
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => type === 'heartbeat' ? false : true,
  });

  await assert.rejects(() => h.controller.init(), /rejected heartbeat/);
  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
  assert.equal(h.calls.policyRequests, 0);
  assert.deepEqual(h.calls.paused, []);
  assert.deepEqual(h.calls.scheduled, []);
  assert.deepEqual(h.calls.localPages, []);
});

test('an incompatible 0.1.3 restart with a persisted playing candidate fails open before policy and media effects', async () => {
  const first = harness();
  await first.controller.init();
  await first.controller.onContentMessage(content(), sender());
  const stored = first.getStored();

  const restart = harness({
    policies: [policy({ mode: 'grandfatherOneMedia' })],
    stored,
    extensionVersion: '0.1.3',
    nativeSend: async type => type === 'heartbeat' ? false : true,
  });
  restart.setMonotonic(100_000);

  await assert.rejects(() => restart.controller.init(), /rejected heartbeat/);

  assert.equal(restart.calls.policyRequests, 0);
  assert.deepEqual(restart.calls.preflights, []);
  assert.deepEqual(restart.calls.scripts, []);
  assert.deepEqual(restart.calls.paused, []);
  assert.deepEqual(restart.calls.scheduled, []);
  assert.deepEqual(restart.calls.localPages, []);
  assert.ok(restart.calls.dnr.every(rules => rules.length === 0));
  assert.equal(restart.controller.getState().health.degraded, true);
});

test('startup compatibility handshake precedes policy and readiness publication', async () => {
  const h = harness({ policies: [policy({ mode: 'blocked' })] });

  await h.controller.init();

  assert.deepEqual(
    h.calls.order.filter(item => item === 'refresh' || item === 'native:heartbeat'),
    ['native:heartbeat', 'refresh', 'native:heartbeat'],
  );
  const heartbeats = h.calls.native.filter(call => call.type === 'heartbeat');
  assert.equal(heartbeats[0].payload.protectionReady, false);
  assert.equal(heartbeats[1].payload.protectionReady, true);
});

test('worker restart rebases a persisted pre-gate candidate onto the new monotonic clock', async () => {
  const first = harness({ policies: [
    policy({ revision: 1, ttlMs: 120_000 }),
    policy({ revision: 2, ttlMs: 120_000 }),
  ] });
  first.setMonotonic(9_940_000);
  await first.controller.init();
  await first.controller.onContentMessage(content(), sender());
  first.setMonotonic(10_000_000);
  await first.controller.refresh();

  const restart = harness({
    policies: [policy({
      revision: 3,
      evaluatedAtUtc: '2026-07-12T15:35:30.000Z',
      lastStartAtUtc: '2026-07-12T15:35:00.000Z',
      mode: 'grandfatherOneMedia',
      ttlMs: 120_000,
    })],
    stored: first.getStored(),
  });
  restart.setMonotonic(100_000);
  await restart.controller.init();

  const state = restart.controller.getState();
  assert.equal(state.media.grant?.tabId, 4);
  assert.equal(Object.values(state.media.candidates)[0].lastPlayingMonotonicMs, 40_000);
  assert.equal(state.media.policy.localLastStartDeadlineMs, 70_000);
});

test('worker restart preserves a paused grant only after the same gate refreshes successfully', async () => {
  const first = harness({ policies: [
    policy({ revision: 1, mode: 'unrestricted' }),
    policy({ revision: 2, mode: 'grandfatherOneMedia' }),
  ] });
  await first.controller.init();
  await first.controller.onContentMessage(content(), sender());
  first.setNow(Date.parse('2026-07-12T15:35:00.000Z'));
  await first.controller.refresh();
  await first.controller.onContentMessage(content({ playback: 'paused' }), sender());
  assert.equal(first.controller.getState().media.grant?.mediaToken, 'media-a');

  const restart = harness({
    policies: [policy({ revision: 3, mode: 'grandfatherOneMedia' })],
    stored: first.getStored(),
  });
  restart.setMonotonic(100_000);
  await restart.controller.init();

  assert.equal(restart.controller.getState().media.grant?.mediaToken, 'media-a');
  assert.equal(restart.calls.scheduled.at(-1).grant.mediaToken, 'media-a');
  assert.ok(restart.calls.scheduled.at(-1).lease.leaseMs > 0);
  assert.equal(
    restart.calls.scheduled.at(-1).lease.leaseDeadlineMonotonicMs,
    100_000 + restart.calls.scheduled.at(-1).lease.leaseMs,
  );
  assert.ok(restart.calls.order.indexOf('effects:arm') < restart.calls.order.indexOf('media:schedule'));
});

test('incognito detection reports unprotected mode without claiming to enable it', async () => {
  const h = harness({ incognito: false });
  await h.controller.init();
  assert.ok(h.calls.statuses.some(text => text.includes('隐身模式未受保护')));
  assert.equal(h.calls.statuses.some(text => text.includes('已启用隐身')), false);
});

test('periodic heartbeat refreshes the observable incognito warning without a worker restart', async () => {
  let allowed = true;
  const h = harness({ incognito: () => allowed });
  await h.controller.init();
  assert.deepEqual(h.calls.native.filter(call => call.type === 'heartbeat'), [
    {
      type: 'heartbeat',
      payload: {
        revision: -1, extensionVersion: '0.1.0', incognitoAllowed: true, protectionReady: false,
      },
    },
    {
      type: 'heartbeat',
      payload: {
        revision: 1, extensionVersion: '0.1.0', incognitoAllowed: true, protectionReady: true,
      },
    },
  ]);
  assert.equal(h.calls.statuses.at(-1).includes('隐身模式未受保护'), false);

  allowed = false;
  await h.controller.onAlarm({ kind: 'heartbeat' });
  assert.deepEqual(h.calls.native.filter(call => call.type === 'heartbeat').at(-1), {
    type: 'heartbeat',
    payload: {
      revision: 1, extensionVersion: '0.1.0', incognitoAllowed: false, protectionReady: true,
    },
  });
  assert.ok(h.calls.statuses.at(-1).includes('隐身模式未受保护'));

  allowed = true;
  await h.controller.onAlarm({ kind: 'heartbeat' });
  assert.deepEqual(h.calls.native.filter(call => call.type === 'heartbeat').at(-1), {
    type: 'heartbeat',
    payload: {
      revision: 1, extensionVersion: '0.1.0', incognitoAllowed: true, protectionReady: true,
    },
  });
  assert.equal(h.calls.statuses.at(-1).includes('隐身模式未受保护'), false);
});

test('periodic heartbeat publication failure abandons active browser restrictions', async () => {
  let heartbeatCalls = 0;
  const h = harness({
    policies: [policy({ mode: 'blocked' })],
    nativeSend: async type => {
      if (type === 'heartbeat' && ++heartbeatCalls === 3) {
        throw new Error('periodic health publication unavailable');
      }
    },
  });
  await h.controller.init();

  await assert.rejects(
    () => h.controller.onAlarm({ kind: 'heartbeat' }),
    /periodic health publication unavailable/,
  );

  assert.equal(h.controller.getState().health.degraded, true);
  assert.deepEqual(h.calls.dnr.at(-1), []);
});

test('native event payloads and session projections recursively omit page secrets', async () => {
  const h = harness();
  await h.controller.init();
  await h.controller.onContentMessage(content(), sender());
  const nativeJson = JSON.stringify(h.calls.native);
  const storedJson = JSON.stringify(h.calls.saved.at(-1));
  for (const secret of ['private', '?q=secret', 'currentSrc', 'title', 'referrer']) {
    assert.equal(nativeJson.includes(secret), false, secret);
    assert.equal(storedJson.includes(secret), false, secret);
  }
  assert.deepEqual(Object.keys(h.calls.native.at(-1).payload).sort(), ['category', 'eventType', 'ruleId', 'timestamp']);
});
