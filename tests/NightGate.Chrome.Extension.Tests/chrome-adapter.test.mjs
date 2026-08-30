import test from 'node:test';
import assert from 'node:assert/strict';

import * as chromeAdapter from '../../src/NightGate.Chrome.Extension/lib/chrome-adapter.mjs';
import { createWorkerController } from '../../src/NightGate.Chrome.Extension/lib/worker-controller.mjs';

const {
  HEARTBEAT_ALARM,
  attachChromeListeners,
  createChromeEffects,
} = chromeAdapter;

const integrationBaseTime = Date.parse('2026-07-12T15:35:00.000Z');
const integrationPolicy = {
  revision: integrationBaseTime,
  gateId: 'gate-adapter-integration',
  evaluatedAtUtc: new Date(integrationBaseTime).toISOString(),
  lastStartAtUtc: new Date(integrationBaseTime).toISOString(),
  ttlMs: 1_000,
  mode: 'blocked',
  lockAtUtc: new Date(integrationBaseTime + 60_000).toISOString(),
  wakeAtUtc: new Date(integrationBaseTime + 3_600_000).toISOString(),
  overrideKind: null,
  siteRules: [{ ruleId: 'video-integration', category: 'video', domain: 'video.example' }],
};

function event() {
  const listeners = [];
  const registrations = [];
  return {
    addListener(listener, filter) {
      listeners.push(listener);
      registrations.push({ listener, filter: structuredClone(filter) });
    },
    removeListener(listener) {
      const index = listeners.indexOf(listener);
      if (index >= 0) listeners.splice(index, 1);
    },
    fire(...args) { return listeners.map(listener => listener(...args)); },
    listeners,
    registrations,
  };
}

function fakeChrome({
  hostPermission = true,
  approvedDomains = ['video.example', 'social.example'],
  supportedDomains = ['video.example', 'social.example', 'example.com'],
  approvedDomainsGet = null,
  alarmError = null,
  dnrReadError = null,
  dnrUpdateError = null,
  openTabs = [],
} = {}) {
  const calls = {
    updates: [], registered: [], unregistered: [], alarms: [], alarmClears: [], local: [], tabMessages: [],
    actionTitles: [], actionBadges: [], actionBadgeColors: [], optionsOpened: 0,
    tabQueries: [], executedScripts: [],
  };
  let sessionValue = null;
  const api = {
    runtime: {
      onMessage: event(), onStartup: event(), onInstalled: event(),
      async openOptionsPage() { calls.optionsOpened += 1; },
      getManifest() {
        return {
          optional_host_permissions: supportedDomains.flatMap(domain => [
            `http://${domain}/*`, `https://${domain}/*`,
            `http://*.${domain}/*`, `https://*.${domain}/*`,
          ]),
        };
      },
    },
    webNavigation: { onBeforeNavigate: event(), onHistoryStateUpdated: event(), onCommitted: event() },
    alarms: {
      onAlarm: event(),
      async clear(name) { calls.alarmClears.push(name); return true; },
      async create(name, options) {
        calls.alarms.push({ name, options });
        if (alarmError) throw alarmError;
      },
    },
    declarativeNetRequest: {
      async getSessionRules() {
        if (dnrReadError) throw dnrReadError;
        return [{ id: 9 }, { id: 3 }];
      },
      async updateSessionRules(update) {
        calls.updates.push(structuredClone(update));
        if (dnrUpdateError) throw dnrUpdateError;
      },
    },
    scripting: {
      async getRegisteredContentScripts() { return [{ id: 'nightgate-site-old' }, { id: 'other-extension-script' }]; },
      async unregisterContentScripts(value) { calls.unregistered.push(structuredClone(value)); },
      async registerContentScripts(value) { calls.registered.push(structuredClone(value)); },
      async executeScript(value) { calls.executedScripts.push(structuredClone(value)); return []; },
    },
    storage: {
      onChanged: event(),
      session: {
        async get() { return { nightGateSession: structuredClone(sessionValue) }; },
        async set(value) { sessionValue = structuredClone(value.nightGateSession); },
      },
      local: {
        async get(key) {
          if (approvedDomainsGet) return approvedDomainsGet(key);
          return { approvedDomains: structuredClone(approvedDomains) };
        },
        async set(value) { calls.local.push(structuredClone(value)); },
      },
    },
    extension: { async isAllowedIncognitoAccess() { return false; } },
    action: {
      onClicked: event(),
      async setTitle(value) { calls.actionTitles.push(structuredClone(value)); },
      async setBadgeText(value) { calls.actionBadges.push(structuredClone(value)); },
      async setBadgeBackgroundColor(value) { calls.actionBadgeColors.push(structuredClone(value)); },
    },
    tabs: {
      onRemoved: event(),
      async query(value) {
        calls.tabQueries.push(structuredClone(value));
        return structuredClone(openTabs);
      },
      async sendMessage(tabId, message, options) {
        calls.tabMessages.push({ tabId, message: structuredClone(message), options: structuredClone(options) });
        return { accepted: true };
      },
    },
    permissions: {
      onAdded: event(), onRemoved: event(),
      async contains() {
        return typeof hostPermission === 'function' ? hostPermission() : hostPermission;
      },
    },
  };
  return { api, calls, session: () => sessionValue };
}

test('extension action opens the site-permission options page', async () => {
  const { api, calls } = fakeChrome();
  attachChromeListeners(api, { async init() {} });

  assert.equal(api.action.onClicked.listeners.length, 1);
  api.action.onClicked.fire();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(calls.optionsOpened, 1);
});

test('a revoked optional site permission immediately revalidates the controller and fails open', async () => {
  const { api } = fakeChrome();
  const alarms = [];
  const controller = {
    async init() {},
    async onAlarm(value) { alarms.push(value); },
    async onContentMessage() {}, async onNavigation() {},
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();

  api.permissions.onRemoved.fire({ origins: ['https://video.example/*'] });
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(alarms, [
    { kind: 'policyExpiry' },
    { kind: 'heartbeat' },
  ]);
});

test('permission removal disarms protection at listener entry before an in-flight handler resumes', async () => {
  const { api } = fakeChrome();
  let armed = false;
  let releaseHandler;
  let handlerStarted;
  let restrictiveEffectApplied = false;
  const handlerGate = new Promise(resolve => { releaseHandler = resolve; });
  const startedGate = new Promise(resolve => { handlerStarted = resolve; });
  const controller = {
    async init() { armed = true; },
    async onAlarm() {},
    async onNavigation() {},
    async onContentMessage() {
      handlerStarted();
      await handlerGate;
      restrictiveEffectApplied = armed;
      return { decision: 'pause' };
    },
  };
  const adapter = attachChromeListeners(api, controller, {
    async clearProtection() { armed = false; },
  });
  await adapter.start();
  const response = new Promise(resolve => {
    api.runtime.onMessage.fire({ type: 'mediaObservation' }, {}, resolve);
  });
  await startedGate;

  api.permissions.onRemoved.fire({ origins: ['https://video.example/*'] });

  assert.equal(armed, false);
  releaseHandler();
  assert.deepEqual(await response, { decision: 'allow' });
  assert.equal(restrictiveEffectApplied, false);
});

test('a revoked permission still publishes a heartbeat after revalidation abandons the controller', async () => {
  const { api } = fakeChrome();
  const calls = [];
  const first = {
    async init() { calls.push(['first', 'init']); },
    async onAlarm(value) {
      calls.push(['first', value.kind]);
      throw new Error('permission revoked');
    },
    async onContentMessage() {}, async onNavigation() {},
  };
  const replacement = {
    async init() { calls.push(['replacement', 'init']); },
    async onAlarm(value) { calls.push(['replacement', value.kind]); },
    async onContentMessage() {}, async onNavigation() {},
  };
  let created = 0;
  const adapter = attachChromeListeners(api, async () => (++created === 1 ? first : replacement));
  await adapter.start();

  api.permissions.onRemoved.fire({ origins: ['https://video.example/*'] });
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(calls, [
    ['first', 'init'],
    ['first', 'policyExpiry'],
    ['replacement', 'init'],
    ['replacement', 'heartbeat'],
  ]);
});

test('saving site permission requests an immediate controller revalidation and policy retry', async () => {
  const { api } = fakeChrome();
  const calls = [];
  const controller = {
    async init() {},
    async onAlarm(value) { calls.push(['alarm', value]); },
    async refresh() { calls.push(['refresh']); },
    async onContentMessage() {}, async onNavigation() {},
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();
  let response;

  const returns = api.runtime.onMessage.fire({ type: 'nightGateSitePermissionsChanged' }, {}, value => {
    response = value;
  });
  assert.deepEqual(returns, [true]);
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(calls, [
    ['alarm', { kind: 'policyExpiry' }],
    ['refresh'],
    ['alarm', { kind: 'heartbeat' }],
  ]);
  assert.deepEqual(response, { ok: true });
});

test('listener adapter forwards messages and trusted senders without policy logic', async () => {
  const { api } = fakeChrome();
  const calls = { init: 0, alarms: [], messages: [], navigations: [] };
  const controller = {
    async init() { calls.init += 1; },
    async onAlarm(alarm) { calls.alarms.push(alarm); },
    async onContentMessage(message, sender) { calls.messages.push({ message, sender }); return { decision: 'pause' }; },
    async onNavigation(details) { calls.navigations.push(details); },
  };
  const adapter = attachChromeListeners(api, controller);
  const sender = { tab: { id: 7 }, documentId: 'trusted' };
  let response;
  const returns = api.runtime.onMessage.fire({ type: 'x' }, sender, value => { response = value; });
  assert.deepEqual(returns, [true]);
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(response, { decision: 'pause' });
  assert.deepEqual(calls.messages, [{ message: { type: 'x' }, sender }]);

  api.webNavigation.onBeforeNavigate.fire({ tabId: 7, frameId: 1, url: 'https://video.example/frame' });
  api.webNavigation.onBeforeNavigate.fire({ tabId: 7, frameId: 0, url: 'https://video.example/watch' });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls.navigations, [{
    tabId: 7, frameId: 0, url: 'https://video.example/', navigationKind: 'topNavigation',
  }]);

  api.alarms.onAlarm.fire({ name: 'other' });
  api.alarms.onAlarm.fire({ name: HEARTBEAT_ALARM });
  api.alarms.onAlarm.fire({ name: 'nightgate-policy-expiry' });
  api.alarms.onAlarm.fire({ name: 'nightgate-policy-last-start' });
  api.alarms.onAlarm.fire({ name: 'nightgate-policy-lock' });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls.alarms, [
    { kind: 'heartbeat' },
    { kind: 'policyExpiry' },
    { kind: 'lastStart' },
    { kind: 'lock' },
  ]);

  await adapter.start();
  assert.equal(calls.init, 1);
});

test('top-level start, startup, and install events share one controller initialization', async () => {
  const { api } = fakeChrome();
  let initialized = 0;
  const controller = {
    async init() { initialized += 1; }, async onAlarm() {}, async onContentMessage() {}, async onNavigation() {},
  };
  const adapter = attachChromeListeners(api, controller);
  const starting = adapter.start();
  api.runtime.onStartup.fire();
  api.runtime.onInstalled.fire();
  await starting;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(initialized, 1);
});

test('listeners await one lazy controller factory while initialization is in flight', async () => {
  const { api } = fakeChrome();
  let releaseFactory;
  const factoryGate = new Promise(resolve => { releaseFactory = resolve; });
  let factoryCalls = 0;
  let initialized = 0;
  const controller = {
    async init() { initialized += 1; },
    async onAlarm() {},
    async onNavigation() {},
    async onContentMessage() { return { decision: 'pause' }; },
  };
  const adapter = attachChromeListeners(api, async () => {
    factoryCalls += 1;
    await factoryGate;
    return controller;
  });
  let response;
  const starting = adapter.start();
  api.runtime.onStartup.fire();
  api.runtime.onInstalled.fire();
  api.runtime.onMessage.fire({ type: 'x' }, { tab: { id: 4 } }, value => { response = value; });
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(factoryCalls, 1);
  assert.equal(initialized, 0);
  releaseFactory();
  await starting;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(initialized, 1);
  assert.deepEqual(response, { decision: 'pause' });
});

test('readiness failure returns allow, clears DNR, and retries on a later message', async () => {
  const { api, calls } = fakeChrome();
  let factoryCalls = 0;
  const controller = {
    async init() {}, async onAlarm() {}, async onNavigation() {},
    async onContentMessage() { return { decision: 'pause' }; },
  };
  const adapter = attachChromeListeners(api, async () => {
    factoryCalls += 1;
    if (factoryCalls === 1) throw new Error('profile token unavailable');
    return controller;
  });
  let firstResponse;
  api.runtime.onMessage.fire({ type: 'x' }, {}, value => { firstResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(firstResponse, { decision: 'allow' });

  let secondResponse;
  api.runtime.onMessage.fire({ type: 'x' }, {}, value => { secondResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(secondResponse, { decision: 'pause' });
  assert.equal(factoryCalls, 2);
  assert.ok(calls.updates.length >= 2);
  assert.ok(calls.updates.every(update => update.addRules.length === 0));
  await assert.doesNotReject(() => adapter.start());
});

test('handler failure discards the controller so a later event rebuilds in fail-open mode', async () => {
  const { api } = fakeChrome();
  let factoryCalls = 0;
  const adapter = attachChromeListeners(api, async () => {
    factoryCalls += 1;
    const attempt = factoryCalls;
    return {
      async init() {},
      async onAlarm() {},
      async onNavigation() {},
      async onContentMessage() {
        if (attempt === 1) throw new Error('handler state is unusable');
        return { decision: 'pause' };
      },
    };
  });

  let firstResponse;
  api.runtime.onMessage.fire({ type: 'x' }, {}, value => { firstResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(firstResponse, { decision: 'allow' });

  let secondResponse;
  api.runtime.onMessage.fire({ type: 'x' }, {}, value => { secondResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(secondResponse, { decision: 'pause' });
  assert.equal(factoryCalls, 2);
});

test('a fatal invocation clears after every older handler and publishes a visible degradation', async () => {
  const { api, calls } = fakeChrome();
  let releaseRestriction;
  let restrictionStarted;
  const restrictionGate = new Promise(resolve => { releaseRestriction = resolve; });
  const startedGate = new Promise(resolve => { restrictionStarted = resolve; });
  let restricted = false;
  const controller = {
    async init() {},
    async onAlarm() {},
    async onNavigation() {},
    async onContentMessage(message) {
      if (message.type === 'restrict') {
        restrictionStarted();
        await restrictionGate;
        restricted = true;
        return { decision: 'pause' };
      }
      throw new Error('session write failed');
    },
  };
  attachChromeListeners(api, controller, {
    async clearProtection() { restricted = false; },
  });

  const restrictiveResponse = new Promise(resolve => {
    api.runtime.onMessage.fire({ type: 'restrict' }, {}, resolve);
  });
  await startedGate;
  const failureResponse = new Promise(resolve => {
    api.runtime.onMessage.fire({ type: 'fail' }, {}, resolve);
  });
  await new Promise(resolve => setImmediate(resolve));
  releaseRestriction();

  assert.deepEqual(await restrictiveResponse, { decision: 'pause' });
  assert.deepEqual(await failureResponse, { decision: 'allow' });
  assert.equal(restricted, false);
  assert.deepEqual(calls.actionBadges.at(-1), { text: '!' });
  assert.ok(calls.actionTitles.at(-1).title.includes('网页保护降级'));
});

test('policy expiry invalidates an in-flight real media response before the queued alarm handler runs', async () => {
  const { api, calls } = fakeChrome({ approvedDomains: ['video.example'] });
  const effects = createChromeEffects(api, { monotonicEpochClock: () => monotonic });
  let now = integrationBaseTime;
  let monotonic = integrationBaseTime;
  let releaseMedia;
  let mediaStarted;
  const mediaGate = new Promise(resolve => { releaseMedia = resolve; });
  const mediaStartedGate = new Promise(resolve => { mediaStarted = resolve; });
  const transport = {
    async getPolicy() { return structuredClone(integrationPolicy); },
    async send(type) {
      if (type === 'mediaState') {
        mediaStarted();
        await mediaGate;
      }
      return true;
    },
  };
  const controller = createWorkerController({
    wallClock: () => now,
    monotonicClock: () => monotonic,
    monotonicEpochClock: () => monotonic,
    transport,
    effects,
    profileToken: 'A'.repeat(43),
    extensionVersion: '0.1.0',
  });
  const adapter = attachChromeListeners(api, controller, {
    clearProtection: () => effects.clearDnr(),
  });
  await adapter.start();

  const response = new Promise(resolve => {
    api.runtime.onMessage.fire({
      type: 'mediaObservation', mediaToken: 'media-real', sourceGeneration: 0, playback: 'playing',
    }, {
      tab: { id: 7 }, documentId: 'real-document', url: 'https://video.example/watch',
    }, resolve);
  });
  await mediaStartedGate;
  now += 1_000;
  monotonic += 1_000;
  api.alarms.onAlarm.fire({ name: 'nightgate-policy-expiry' });
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(calls.updates.at(-1).addRules, []);
  assert.deepEqual(calls.actionBadges.at(-1), { text: '!' });
  releaseMedia();
  assert.deepEqual(await response, { decision: 'allow' });
  for (let index = 0; index < 4; index += 1) await new Promise(resolve => setImmediate(resolve));
  assert.equal(controller.getState().health.degraded, true);
});

test('permission removal invalidates an in-flight real policy apply before it can rearm', async () => {
  let permissionAllowed = true;
  const { api, calls } = fakeChrome({
    approvedDomains: ['video.example'],
    hostPermission: () => permissionAllowed,
  });
  let scriptReads = 0;
  let releaseSecondApply;
  let secondApplyStarted;
  const secondApplyGate = new Promise(resolve => { releaseSecondApply = resolve; });
  const secondApplyStartedGate = new Promise(resolve => { secondApplyStarted = resolve; });
  api.scripting.getRegisteredContentScripts = async () => {
    scriptReads += 1;
    if (scriptReads === 2) {
      secondApplyStarted();
      await secondApplyGate;
    }
    return [{ id: 'nightgate-site-old' }];
  };
  const heartbeats = [];
  let policyReads = 0;
  const transport = {
    async getPolicy() {
      policyReads += 1;
      return structuredClone(policyReads === 1
        ? integrationPolicy
        : {
          ...integrationPolicy,
          revision: integrationPolicy.revision + 1,
          evaluatedAtUtc: new Date(integrationPolicy.revision + 1).toISOString(),
        });
    },
    async send(type, payload) {
      if (type === 'heartbeat') heartbeats.push(structuredClone(payload));
      return true;
    },
  };
  const effects = createChromeEffects(api, { monotonicEpochClock: () => integrationBaseTime });
  const controller = createWorkerController({
    wallClock: () => integrationBaseTime,
    monotonicClock: () => integrationBaseTime,
    monotonicEpochClock: () => integrationBaseTime,
    transport,
    effects,
    profileToken: 'A'.repeat(43),
    extensionVersion: '0.1.3',
  });
  const adapter = attachChromeListeners(api, controller, {
    clearProtection: () => effects.clearDnr(),
  });
  await adapter.start();
  const registeredBaseline = calls.registered.length;
  const controlsBaseline = calls.tabMessages.length;

  api.alarms.onAlarm.fire({ name: HEARTBEAT_ALARM });
  await secondApplyStartedGate;
  permissionAllowed = false;
  api.permissions.onRemoved.fire({ origins: ['https://video.example/*'] });
  releaseSecondApply();
  for (let index = 0; index < 8; index += 1) await new Promise(resolve => setImmediate(resolve));

  assert.equal(calls.registered.length, registeredBaseline);
  assert.equal(calls.tabMessages.length, controlsBaseline);
  assert.equal(controller.getState().health.degraded, true);
  assert.equal(heartbeats.at(-1).protectionReady, false);
});

test('adapter startup clears old DNR rules before an alarm setup failure', async () => {
  const { api, calls } = fakeChrome({ alarmError: new Error('alarm unavailable') });
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {}, async onNavigation() {},
  };
  const adapter = attachChromeListeners(api, controller);

  await assert.rejects(() => adapter.start(), /alarm unavailable/);

  assert.ok(calls.updates.length >= 1);
  assert.ok(calls.updates.every(update => update.addRules.length === 0));
});

test('adapter stays degraded and never initializes restrictions when legacy DNR cleanup is unavailable', async () => {
  for (const failure of [
    { dnrReadError: new Error('DNR read unavailable') },
    { dnrUpdateError: new Error('DNR update unavailable') },
  ]) {
    const { api, calls } = fakeChrome(failure);
    let initialized = 0;
    const adapter = attachChromeListeners(api, {
      async init() { initialized += 1; },
      async onAlarm() {},
      async onContentMessage() { return { decision: 'pause' }; },
      async onNavigation() {},
    });

    await assert.rejects(() => adapter.start(), /legacy DNR cleanup/i);
    assert.equal(initialized, 0);
    assert.deepEqual(calls.actionBadges.at(-1), { text: '!' });
  }
});

test('standalone DNR cleanup is best effort and never installs a rule', async () => {
  for (const options of [
    { dnrReadError: new Error('read failed') },
    { dnrUpdateError: new Error('write failed') },
  ]) {
    const { api, calls } = fakeChrome(options);
    await assert.doesNotReject(() => chromeAdapter.clearSessionRulesBestEffort(api));
    assert.ok(calls.updates.every(update => update.addRules.length === 0));
  }
});

test('webNavigation history and committed events provide authoritative SPA/document revocation', async () => {
  const { api } = fakeChrome({ approvedDomains: ['example.com'] });
  const navigations = [];
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {},
    async onNavigation(details) { navigations.push(details); },
  };
  const adapter = attachChromeListeners(api, controller);
  for (const navigationEvent of [
    api.webNavigation.onBeforeNavigate,
    api.webNavigation.onHistoryStateUpdated,
    api.webNavigation.onCommitted,
  ]) {
    assert.equal(navigationEvent.registrations.length, 1, 'MV3 wake-up listener must register synchronously');
    assert.ok(navigationEvent.registrations[0].filter.url.some(filter => filter.hostEquals === 'video.example'));
    assert.ok(navigationEvent.registrations[0].filter.url.some(filter => filter.hostEquals === 'social.example'));
  }
  await adapter.start();
  api.webNavigation.onHistoryStateUpdated.fire({ tabId: 5, frameId: 1, url: 'https://frame.invalid' });
  api.webNavigation.onHistoryStateUpdated.fire({ tabId: 5, frameId: 0, url: 'https://example.com/spa' });
  api.webNavigation.onCommitted.fire({ tabId: 5, frameId: 0, documentId: 'new-doc', url: 'https://example.com/new' });
  api.tabs.onRemoved.fire(6, { isWindowClosing: false, windowId: 2 });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(navigations, [
    { tabId: 5, frameId: 0, url: 'https://example.com/', navigationKind: 'spaNavigation' },
    { tabId: 5, frameId: 0, documentId: 'new-doc', url: 'https://example.com/', navigationKind: 'documentReplaced' },
    { tabId: 6, navigationKind: 'tabRemoved' },
  ]);
});

test('webNavigation listeners filter approved domains before queueing and project only required fields', async () => {
  const { api } = fakeChrome({ approvedDomains: ['video.example'] });
  const navigations = [];
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {},
    async onNavigation(details) { navigations.push(structuredClone(details)); },
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();

  for (const navigationEvent of [
    api.webNavigation.onBeforeNavigate,
    api.webNavigation.onHistoryStateUpdated,
    api.webNavigation.onCommitted,
  ]) {
    const filters = navigationEvent.registrations.at(-1).filter.url;
    assert.ok(filters.some(filter => filter.hostEquals === 'video.example'));
    assert.ok(filters.some(filter => filter.hostSuffix === '.video.example'));
    assert.ok(filters.some(filter => filter.hostEquals === 'social.example'));
    assert.deepEqual(filters.every(filter => filter.schemes.join(',') === 'http,https'), true);
    assert.equal(navigationEvent.registrations.length, 1);
  }

  api.webNavigation.onCommitted.fire({
    tabId: 5, frameId: 0, documentId: 'ignored-doc', url: 'https://unselected.example/watch',
    transitionType: 'link', initiatorUrl: 'https://private.invalid/referrer', timeStamp: 123,
  });
  api.webNavigation.onCommitted.fire({
    tabId: 6, frameId: 0, documentId: 'selected-doc', url: 'https://sub.video.example/watch',
    transitionType: 'link', initiatorUrl: 'https://private.invalid/referrer', timeStamp: 456,
  });
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(navigations, [{
    tabId: 6, frameId: 0, documentId: 'selected-doc',
    url: 'https://sub.video.example/', navigationKind: 'documentReplaced',
  }]);
  assert.equal(JSON.stringify(navigations).includes('private.invalid'), false);

  api.storage.onChanged.fire({ approvedDomains: { newValue: ['social.example'] } }, 'local');
  await new Promise(resolve => setImmediate(resolve));
  api.webNavigation.onBeforeNavigate.fire({
    tabId: 7, frameId: 0, url: 'https://video.example/old-selection',
  });
  api.webNavigation.onBeforeNavigate.fire({
    tabId: 8, frameId: 0, url: 'https://social.example/new-selection',
  });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(navigations.length, 2);
  assert.equal(navigations.at(-1).tabId, 8);
});

test('a cold-start navigation reserves its place until approved domains finish loading', async () => {
  let releaseDomains;
  const domainsGate = new Promise(resolve => { releaseDomains = resolve; });
  const { api } = fakeChrome({
    approvedDomainsGet: async () => {
      await domainsGate;
      return { approvedDomains: ['video.example'] };
    },
  });
  const navigations = [];
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {},
    async onNavigation(details) { navigations.push(structuredClone(details)); },
  };
  attachChromeListeners(api, controller);

  api.webNavigation.onCommitted.fire({
    tabId: 12, frameId: 0, documentId: 'cold-document', url: 'https://video.example/first',
  });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(navigations, []);
  releaseDomains();
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(navigations, [{
    tabId: 12, frameId: 0, documentId: 'cold-document',
    url: 'https://video.example/', navigationKind: 'documentReplaced',
  }]);
});

test('a transient approved-domain read failure is retried before controller initialization', async () => {
  let reads = 0;
  const { api } = fakeChrome({
    approvedDomainsGet: async () => {
      reads += 1;
      if (reads === 1) throw new Error('storage temporarily unavailable');
      return { approvedDomains: ['video.example'] };
    },
  });
  let initialized = 0;
  const adapter = attachChromeListeners(api, {
    async init() { initialized += 1; },
    async onAlarm() {}, async onContentMessage() {}, async onNavigation() {},
  });

  await adapter.start();

  assert.equal(reads, 2);
  assert.equal(initialized, 1);
});

test('an approved navigation queued behind work is dropped if the domain is revoked before dequeue', async () => {
  const { api } = fakeChrome({ approvedDomains: ['video.example'] });
  let releaseFirst;
  let firstStarted;
  const firstGate = new Promise(resolve => { releaseFirst = resolve; });
  const firstStartedGate = new Promise(resolve => { firstStarted = resolve; });
  const navigations = [];
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {},
    async onNavigation(details) {
      navigations.push(details.tabId);
      if (details.tabId === 1) {
        firstStarted();
        await firstGate;
      }
    },
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();
  api.webNavigation.onCommitted.fire({
    tabId: 1, frameId: 0, documentId: 'first', url: 'https://video.example/one',
  });
  await firstStartedGate;
  api.webNavigation.onCommitted.fire({
    tabId: 2, frameId: 0, documentId: 'queued', url: 'https://video.example/two',
  });
  api.storage.onChanged.fire({ approvedDomains: { oldValue: ['video.example'], newValue: [] } }, 'local');
  releaseFirst();
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(navigations, [1]);
});

test('deleting approvedDomains synchronously revokes navigation without rereading stale storage', async () => {
  const { api } = fakeChrome({ approvedDomains: ['video.example'] });
  const navigations = [];
  const controller = {
    async init() {}, async onAlarm() {}, async onContentMessage() {},
    async onNavigation(details) { navigations.push(details.tabId); },
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();

  api.storage.onChanged.fire({ approvedDomains: { oldValue: ['video.example'], newValue: undefined } }, 'local');
  api.webNavigation.onCommitted.fire({
    tabId: 13, frameId: 0, documentId: 'revoked', url: 'https://video.example/removed',
  });
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(navigations, []);
});

test('fire-and-forget browser events reserve their serialized position at listener entry', async () => {
  const { api } = fakeChrome({ approvedDomains: ['example.com'] });
  const order = [];
  const controller = {
    async init() {},
    async onAlarm() {},
    async onNavigation() { order.push('navigation'); },
    async onContentMessage() { order.push('message'); return { decision: 'allow' }; },
  };
  const adapter = attachChromeListeners(api, controller);
  await adapter.start();

  api.webNavigation.onCommitted.fire({
    tabId: 5, frameId: 0, documentId: 'new-doc', url: 'https://example.com/new',
  });
  const response = new Promise(resolve => {
    api.runtime.onMessage.fire({ type: 'x' }, {}, resolve);
  });

  assert.deepEqual(await response, { decision: 'allow' });
  assert.deepEqual(order, ['navigation', 'message']);
});

test('Chrome effects clear legacy DNR rules and reject installation of persistent restrictions', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api);
  const addRules = [{ id: 200_000, condition: { resourceTypes: ['main_frame'] } }];
  await assert.rejects(() => effects.replaceDnr(addRules), /persistent DNR/i);
  await effects.replaceDnr([]);
  assert.deepEqual(calls.updates, [{ removeRuleIds: [3, 9], addRules: [] }]);
});

test('a delayed old DNR read cannot overwrite a newer fail-open clear', async () => {
  let releaseOldRead;
  const oldReadGate = new Promise(resolve => { releaseOldRead = resolve; });
  let reads = 0;
  let current = [{ id: 9, action: { type: 'redirect' } }];
  const updates = [];
  const api = {
    declarativeNetRequest: {
      async getSessionRules() {
        reads += 1;
        if (reads === 1) await oldReadGate;
        return structuredClone(current);
      },
      async updateSessionRules(update) {
        updates.push(structuredClone(update));
        const removed = new Set(update.removeRuleIds);
        current = current.filter(rule => !removed.has(rule.id)).concat(structuredClone(update.addRules));
      },
    },
  };
  const effects = createChromeEffects(api);
  const oldReplacement = effects.replaceDnr([]);
  await new Promise(resolve => setImmediate(resolve));
  const newestClear = effects.replaceDnr([]);
  await new Promise(resolve => setImmediate(resolve));
  releaseOldRead();
  await Promise.all([oldReplacement, newestClear]);

  assert.deepEqual(current, []);
  assert.deepEqual(updates, [{ removeRuleIds: [9], addRules: [] }]);
});

test('listener failure cleanup remains fail-open while a legacy DNR clear is in flight', async () => {
  const { api } = fakeChrome();
  let current = [{ id: 200_000, action: { type: 'redirect' }, condition: { resourceTypes: ['main_frame'] } }];
  api.declarativeNetRequest.getSessionRules = async () => structuredClone(current);
  api.declarativeNetRequest.updateSessionRules = async update => {
    const removed = new Set(update.removeRuleIds);
    current = current.filter(rule => !removed.has(rule.id)).concat(structuredClone(update.addRules));
  };
  const effects = createChromeEffects(api);
  const controller = {
    async init() {}, async onAlarm() {}, async onNavigation() {},
    async onContentMessage(message) {
      if (message.type === 'restrict') {
        await effects.replaceDnr([]);
        return { decision: 'allow' };
      }
      throw new Error('handler failed');
    },
  };
  const adapter = attachChromeListeners(api, controller, {
    clearProtection: () => effects.clearDnr(),
  });
  await adapter.start();

  let releaseOldRead;
  const oldReadGate = new Promise(resolve => { releaseOldRead = resolve; });
  let reads = 0;
  api.declarativeNetRequest.getSessionRules = async () => {
    reads += 1;
    if (reads === 1) await oldReadGate;
    return structuredClone(current);
  };
  let restrictiveResponse;
  let failureResponse;
  api.runtime.onMessage.fire({ type: 'restrict' }, {}, value => { restrictiveResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  api.runtime.onMessage.fire({ type: 'fail' }, {}, value => { failureResponse = value; });
  await new Promise(resolve => setImmediate(resolve));
  releaseOldRead();
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(restrictiveResponse, { decision: 'allow' });
  assert.deepEqual(failureResponse, { decision: 'allow' });
  assert.deepEqual(current, []);
});

test('policy-expiry listener entry disarms older navigation controls and only clears legacy DNR', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api, { monotonicEpochClock: () => 50_000 });
  const lease = { leaseMs: 10_000, leaseDeadlineMonotonicMs: 60_000 };
  let releaseNavigation;
  let navigationStarted;
  let alarmFinished;
  const navigationGate = new Promise(resolve => { releaseNavigation = resolve; });
  const navigationStartedGate = new Promise(resolve => { navigationStarted = resolve; });
  const alarmFinishedGate = new Promise(resolve => { alarmFinished = resolve; });
  const controller = {
    async init() {
      const permissionLease = await effects.preflightSiteRules([]);
      effects.armDnr?.(permissionLease);
    },
    async onContentMessage() { return { decision: 'allow' }; },
    async onNavigation() {
      navigationStarted();
      await navigationGate;
      await effects.pauseMediaTargets([{
        tabId: 3, documentId: 'old-document', mediaToken: 'media-old', sourceGeneration: 0,
      }], 'gate-old', lease);
      await effects.showLocalPage({ tabId: 3, documentId: 'old-document' }, 'blocked', lease);
      await effects.replaceDnr([]);
    },
    async onAlarm() {
      await effects.clearDnr();
      alarmFinished();
    },
  };
  const adapter = attachChromeListeners(api, controller, {
    clearProtection: () => effects.clearDnr(),
  });
  await adapter.start();

  api.webNavigation.onCommitted.fire({
    tabId: 3, frameId: 0, documentId: 'old-document', url: 'https://video.example/watch',
  });
  await navigationStartedGate;
  const baseline = calls.updates.length;
  const tabMessageBaseline = calls.tabMessages.length;
  api.alarms.onAlarm.fire({ name: 'nightgate-policy-expiry' });
  await new Promise(resolve => setImmediate(resolve));
  releaseNavigation();
  await alarmFinishedGate;
  await new Promise(resolve => setImmediate(resolve));

  assert.ok(calls.updates.length > baseline);
  assert.ok(calls.updates.slice(baseline).every(update => update.addRules.length === 0));
  assert.equal(calls.tabMessages.length, tabMessageBaseline);
});

test('Chrome effects preflight without mutation then apply document-start scripts for exact approved domains', async () => {
  const { api, calls } = fakeChrome({
    openTabs: [
      { id: 19, url: 'https://video.example/watch/1' },
      { id: 23, url: 'https://social.example/feed' },
    ],
  });
  const effects = createChromeEffects(api);
  const siteRules = [
    { ruleId: 'b', category: 'video', domain: 'video.example' },
    { ruleId: 'a', category: 'social', domain: 'social.example' },
  ];
  const permissionLease = await effects.preflightSiteRules(siteRules);
  assert.deepEqual(calls.unregistered, []);
  assert.deepEqual(calls.registered, []);
  await effects.applySiteRules(siteRules, permissionLease);
  assert.deepEqual(calls.unregistered, [{ ids: ['nightgate-site-old'] }]);
  const scripts = calls.registered[0];
  assert.equal(scripts.length, 2);
  assert.ok(scripts.every(script => script.runAt === 'document_start' && script.allFrames === false));
  assert.ok(scripts.every(script => JSON.stringify(script.js) === '["lib/content-observer.js","content-script.js"]'));
  assert.deepEqual(scripts[0].matches, [
    'http://social.example/*', 'https://social.example/*',
    'http://*.social.example/*', 'https://*.social.example/*',
  ]);
  assert.deepEqual(calls.tabQueries, [{
    url: [
      'http://social.example/*', 'https://social.example/*',
      'http://*.social.example/*', 'https://*.social.example/*',
      'http://video.example/*', 'https://video.example/*',
      'http://*.video.example/*', 'https://*.video.example/*',
    ],
  }]);
  assert.deepEqual(calls.executedScripts, [
    {
      target: { tabId: 19, frameIds: [0] },
      files: ['lib/content-observer.js', 'content-script.js'],
    },
    {
      target: { tabId: 23, frameIds: [0] },
      files: ['lib/content-observer.js', 'content-script.js'],
    },
  ]);
  assert.equal(JSON.stringify(scripts).includes('<all_urls>'), false);
});

test('a tab that closes or navigates away during backfill does not degrade remaining protection', async () => {
  const { api, calls } = fakeChrome({
    openTabs: [
      { id: 19, url: 'https://video.example/watch/1' },
      { id: 23, url: 'https://video.example/watch/2' },
    ],
  });
  let queryCount = 0;
  api.tabs.query = async value => {
    calls.tabQueries.push(structuredClone(value));
    queryCount += 1;
    return queryCount === 1
      ? [
        { id: 19, url: 'https://video.example/watch/1' },
        { id: 23, url: 'https://video.example/watch/2' },
      ]
      : [{ id: 23, url: 'https://video.example/watch/2' }];
  };
  api.scripting.executeScript = async value => {
    calls.executedScripts.push(structuredClone(value));
    if (value.target.tabId === 19) throw new Error('No tab with id: 19');
    return [];
  };
  const effects = createChromeEffects(api);
  const siteRules = [
    { ruleId: 'video', category: 'video', domain: 'video.example' },
  ];

  const permissionLease = await effects.preflightSiteRules(siteRules);
  await effects.applySiteRules(siteRules, permissionLease);

  assert.equal(queryCount, 2);
  assert.equal(calls.executedScripts.length, 2);
});

test('an already-open tab must finish backfill before Chrome reports protection ready', async () => {
  const { api } = fakeChrome({
    approvedDomains: ['video.example'],
    openTabs: [{ id: 19, url: 'https://video.example/watch/1' }],
  });
  api.scripting.executeScript = async () => {
    throw new Error('tab injection failed');
  };
  const heartbeats = [];
  const effects = createChromeEffects(api, { monotonicEpochClock: () => integrationBaseTime });
  const controller = createWorkerController({
    wallClock: () => integrationBaseTime,
    monotonicClock: () => integrationBaseTime,
    monotonicEpochClock: () => integrationBaseTime,
    transport: {
      async getPolicy() { return structuredClone(integrationPolicy); },
      async send(type, payload) {
        if (type === 'heartbeat') heartbeats.push(structuredClone(payload));
        return true;
      },
    },
    effects,
    profileToken: 'A'.repeat(43),
    extensionVersion: '0.1.3',
  });

  await controller.init();

  assert.equal(controller.getState().health.degraded, true);
  assert.equal(heartbeats.length, 1);
  assert.equal(heartbeats[0].protectionReady, false);
});

test('missing optional site permission keeps protection degraded instead of registering silently', async () => {
  const { api, calls } = fakeChrome({ hostPermission: false });
  const effects = createChromeEffects(api);
  await assert.rejects(() => effects.preflightSiteRules([
    { ruleId: 'r', category: 'video', domain: 'video.example' },
  ]));
  assert.deepEqual(calls.unregistered, []);
  assert.deepEqual(calls.registered, []);
});

test('host permission alone is insufficient when the user did not select the policy site', async () => {
  const { api, calls } = fakeChrome({ hostPermission: true, approvedDomains: [] });
  const effects = createChromeEffects(api);
  await assert.rejects(() => effects.preflightSiteRules([
    { ruleId: 'r', category: 'video', domain: 'video.example' },
  ]));
  assert.deepEqual(calls.unregistered, []);
  assert.deepEqual(calls.registered, []);
});

test('Chrome effects expose session storage, visible warning status, and incognito detection', async () => {
  const { api, calls, session } = fakeChrome();
  const effects = createChromeEffects(api);
  await effects.saveSession({ version: 1 });
  assert.deepEqual(session(), { version: 1 });
  assert.deepEqual(await effects.loadSession(), { version: 1 });
  await effects.setStatus('网页保护降级 · 隐身模式未受保护');
  await effects.setStatus('网页保护正常');
  assert.deepEqual(calls.local, [
    { protectionStatus: '网页保护降级 · 隐身模式未受保护' },
    { protectionStatus: '网页保护正常' },
  ]);
  assert.deepEqual(calls.actionTitles, [
    { title: '网页保护降级 · 隐身模式未受保护' },
    { title: '网页保护正常' },
  ]);
  assert.deepEqual(calls.actionBadges, [{ text: '!' }, { text: '' }]);
  assert.deepEqual(calls.actionBadgeColors, [{ color: '#b45309' }]);
  assert.equal(await effects.isIncognitoAllowed(), false);
});

test('Chrome effects schedule named one-shot policy wakeups and preserve the periodic fallback separately', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api);
  await effects.schedulePolicyWakeups({
    expiryDelayMs: 60_000,
    lastStartDelayMs: 120_000,
    lockDelayMs: 180_000,
  });

  assert.deepEqual(calls.alarmClears, [
    'nightgate-policy-expiry',
    'nightgate-policy-last-start',
    'nightgate-policy-lock',
  ]);
  assert.deepEqual(calls.alarms, [
    { name: 'nightgate-policy-expiry', options: { delayInMinutes: 1 } },
    { name: 'nightgate-policy-last-start', options: { delayInMinutes: 2 } },
    { name: 'nightgate-policy-lock', options: { delayInMinutes: 3 } },
  ]);
  assert.equal(calls.alarms.some(alarm => alarm.name === HEARTBEAT_ALARM), false);
});

test('Chrome effects direct deadline, cancellation, and fallback pause to trusted document identities', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api, { monotonicEpochClock: () => 50_000 });
  const grant = {
    tabId: 7, documentId: 'trusted-doc', mediaToken: 'media-a', sourceGeneration: 2, gateId: 'gate-1',
  };
  const lease = { leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000 };
  await effects.scheduleGrantPause(grant, 'gate-1', 12_345, lease);
  await effects.cancelGrantPause(grant, 'gate-1');
  await effects.pauseMediaTargets([grant], 'gate-1', lease);
  assert.deepEqual(calls.tabMessages, [
    {
      tabId: 7,
      message: {
        type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
        mediaToken: 'media-a', sourceGeneration: 2, delayMs: 12_345,
        leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000,
      },
      options: { documentId: 'trusted-doc' },
    },
    {
      tabId: 7,
      message: {
        type: 'nightGateControl', command: 'cancelPause', gateId: 'gate-1',
        mediaToken: 'media-a', sourceGeneration: 2,
      },
      options: { documentId: 'trusted-doc' },
    },
    {
      tabId: 7,
      message: {
        type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
        mediaToken: 'media-a', sourceGeneration: 2, delayMs: 0,
        leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000,
      },
      options: { documentId: 'trusted-doc' },
    },
  ]);
});

test('a permission-generation change prevents a prepared rule set from rearming protection', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api, { monotonicEpochClock: () => 50_000 });
  const siteRules = [{ ruleId: 'video', category: 'video', domain: 'video.example' }];
  const permissionLease = await effects.preflightSiteRules(siteRules);

  await effects.clearDnr();

  await assert.rejects(
    () => effects.applySiteRules(siteRules, permissionLease),
    /permission generation changed/u,
  );
  assert.throws(
    () => effects.armDnr(permissionLease),
    /permission generation changed/u,
  );
  assert.deepEqual(calls.registered, []);
});

test('Chrome effects direct only a local-page enum to the authoritative document', async () => {
  const { api, calls } = fakeChrome();
  const effects = createChromeEffects(api, { monotonicEpochClock: () => 50_000 });
  const lease = { leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000 };
  await effects.showLocalPage({ tabId: 7, documentId: 'trusted-doc' }, 'finished', lease);
  assert.deepEqual(calls.tabMessages, [{
    tabId: 7,
    message: {
      type: 'nightGateControl', command: 'showLocalPage', page: 'finished',
      leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000,
    },
    options: { documentId: 'trusted-doc' },
  }]);
  assert.equal(JSON.stringify(calls.tabMessages).includes('url'), false);
  await assert.rejects(() => effects.showLocalPage(
    { tabId: 7, documentId: 'trusted-doc' }, 'https://example.com', lease));
});

test('Chrome effects clamp every restrictive control at dispatch and drop an expired lease', async () => {
  const { api, calls } = fakeChrome();
  let monotonicNow = 55_000;
  const effects = createChromeEffects(api, { monotonicEpochClock: () => monotonicNow });
  const target = {
    tabId: 7, documentId: 'trusted-doc', mediaToken: 'media-a', sourceGeneration: 2,
  };
  const lease = { leaseMs: 30_000, leaseDeadlineMonotonicMs: 80_000 };

  await effects.pauseMediaTargets([target], 'gate-1', lease);
  assert.equal(calls.tabMessages[0].message.leaseMs, 25_000);
  assert.equal(calls.tabMessages[0].message.leaseDeadlineMonotonicMs, 80_000);

  monotonicNow = 80_000;
  await effects.showLocalPage(target, 'blocked', lease);
  assert.equal(calls.tabMessages.length, 1);
});
