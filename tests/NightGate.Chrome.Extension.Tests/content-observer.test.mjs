import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/NightGate.Chrome.Extension/lib/content-observer.js', import.meta.url), 'utf8');
const bootstrapSource = readFileSync(new URL('../../src/NightGate.Chrome.Extension/content-script.js', import.meta.url), 'utf8');

function loadFactory() {
  const context = { Promise, WeakMap, Set };
  vm.runInNewContext(source, context, { filename: 'content-observer.js' });
  return context.NightGateContentObserver;
}

function media(overrides = {}) {
  return {
    tagName: 'VIDEO',
    currentSrc: 'https://cdn.invalid/private-a.mp4',
    src: '',
    paused: false,
    ended: false,
    autoplay: false,
    pauseCalls: 0,
    pause() { this.pauseCalls += 1; this.paused = true; },
    matches(selector) { return selector === 'audio,video'; },
    querySelectorAll() { return []; },
    ...overrides,
  };
}

function harness(elements = []) {
  const listeners = {};
  const windowListeners = {};
  const document = {
    querySelectorAll() { return elements; },
    addEventListener(name, callback) { listeners[name] = callback; },
    removeEventListener(name) { delete listeners[name]; },
  };
  const window = {
    history: {
      pushState(...args) { return args.length; },
      replaceState(...args) { return args.length; },
    },
    addEventListener(name, callback) { windowListeners[name] = callback; },
    removeEventListener(name) { delete windowListeners[name]; },
  };
  class FakeMutationObserver {
    constructor(callback) { this.callback = callback; FakeMutationObserver.instance = this; }
    observe() {}
    disconnect() {}
  }
  const messages = [];
  const localPages = [];
  let nextResponse = { decision: 'allow' };
  const timers = new Map();
  let timerId = 0;
  let token = 0;
  let monotonicMs = 0;
  let monotonicSequence = [];
  const observer = loadFactory().create({
    document,
    window,
    MutationObserverClass: FakeMutationObserver,
    tokenFactory: () => `media-${++token}`,
    send: async message => { messages.push(structuredClone(message)); return structuredClone(nextResponse); },
    showLocalPage: page => { localPages.push(page); },
    setTimeoutFn(callback, delay) { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
    clearTimeoutFn(id) { timers.delete(id); },
    monotonicClock: () => monotonicSequence.length ? monotonicSequence.shift() : monotonicMs,
  });
  return {
    observer, messages, localPages, listeners, windowListeners, window, FakeMutationObserver,
    timers,
    setResponse(value) { nextResponse = value; },
    setMonotonic(value) { monotonicMs = value; },
    setMonotonicSequence(values) {
      monotonicSequence = [...values];
      if (values.length) monotonicMs = values.at(-1);
    },
  };
}

test('observer reports only privacy-safe identity and playback state', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');
  assert.deepEqual(h.messages.at(-1), {
    type: 'mediaObservation', mediaToken: 'media-1', sourceGeneration: 0, playback: 'playing',
  });
  const json = JSON.stringify(h.messages);
  for (const secret of ['cdn.invalid', 'private-a.mp4', 'currentSrc', 'url', 'title']) assert.equal(json.includes(secret), false);
});

test('source lifecycle events increment generation without reading currentSrc', async () => {
  const element = media({ paused: true });
  Object.defineProperty(element, 'currentSrc', {
    configurable: true,
    get() { throw new Error('media URL must not be read'); },
  });
  const h = harness([element]);
  h.observer.start();
  element.paused = false;
  await h.observer.report(element, 'playing');
  await h.listeners.loadstart({ target: element });
  assert.deepEqual(h.messages.map(item => item.sourceGeneration), [0, 1]);
  assert.equal(source.includes('currentSrc'), false);
});

test('play, pause, ended, autoplay, and newly created media are observed', async () => {
  const autoplay = media({ autoplay: true });
  const h = harness([autoplay]);
  h.observer.start();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.messages[0].playback, 'playing');

  await h.listeners.play({ type: 'play', target: autoplay });
  await h.listeners.pause({ type: 'pause', target: autoplay });
  await h.listeners.ended({ type: 'ended', target: autoplay });
  assert.deepEqual(h.messages.slice(-3).map(item => item.playback), ['playing', 'paused', 'ended']);

  const added = media({ autoplay: true });
  h.FakeMutationObserver.instance.callback([{ addedNodes: [added] }]);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.messages.at(-1).mediaToken, 'media-2');
});

test('mutation scan reports already-playing non-autoplay media inserted after playback starts', async () => {
  const playing = media({
    autoplay: false,
    paused: false,
    currentSrc: 'https://cdn.invalid/private-before-insertion.mp4',
  });
  const container = {
    matches() { return false; },
    querySelectorAll(selector) { return selector === 'audio,video' ? [playing] : []; },
  };
  const h = harness();
  h.observer.start();

  h.FakeMutationObserver.instance.callback([{ addedNodes: [container] }]);
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(h.messages, [{
    type: 'mediaObservation', mediaToken: 'media-1', sourceGeneration: 0, playback: 'playing',
  }]);
  const json = JSON.stringify(h.messages);
  for (const secret of ['cdn.invalid', 'private-before-insertion', 'currentSrc', '.mp4']) {
    assert.equal(json.includes(secret), false, secret);
  }
});

test('media pause responses require a fresh bounded absolute monotonic lease', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();

  h.setResponse({ decision: 'pause' });
  await h.observer.report(element, 'playing');
  assert.equal(element.pauseCalls, 0);

  h.setResponse({
    decision: 'pause', leaseMs: 30_000, leaseDeadlineMonotonicMs: 20_000,
  });
  await h.observer.report(element, 'playing');
  assert.equal(element.pauseCalls, 1);

  element.paused = false;
  h.setMonotonic(20_000);
  await h.observer.report(element, 'playing');
  assert.equal(element.pauseCalls, 1);

  element.paused = false;
  h.setResponse({
    decision: 'pause', leaseMs: Number.NaN, leaseDeadlineMonotonicMs: 50_000,
  });
  await h.observer.report(element, 'playing');
  assert.equal(element.pauseCalls, 1);

  element.paused = false;
  h.setResponse({
    decision: 'pause', leaseMs: 1_000, leaseDeadlineMonotonicMs: 50_000, extra: true,
  });
  await h.observer.report(element, 'playing');
  assert.equal(element.pauseCalls, 1);
});

test('media pause rechecks the shorter relative lease immediately before the side effect', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  h.setResponse({
    decision: 'pause', leaseMs: 10, leaseDeadlineMonotonicMs: 100_000,
  });
  h.setMonotonicSequence([1_000, 1_010]);

  await h.observer.report(element, 'playing');

  assert.equal(element.pauseCalls, 0);
});

test('media transport faults fail open', async () => {

  const faulty = loadFactory().create({
    document: { querySelectorAll: () => [], addEventListener() {}, removeEventListener() {} },
    window: { history: { pushState() {}, replaceState() {} }, addEventListener() {}, removeEventListener() {} },
    MutationObserverClass: class { observe() {} disconnect() {} },
    tokenFactory: () => 'token',
    send: async () => { throw new Error('worker asleep'); },
  });
  const second = media();
  await assert.doesNotReject(() => faulty.report(second, 'playing'));
  assert.equal(second.pauseCalls, 0);
});

test('content observer leaves History APIs untouched and has no page-side SPA signal', () => {
  const h = harness();
  const originalPushState = h.window.history.pushState;
  const originalReplaceState = h.window.history.replaceState;
  h.observer.start();

  assert.equal(h.window.history.pushState, originalPushState);
  assert.equal(h.window.history.replaceState, originalReplaceState);
  assert.deepEqual(h.windowListeners, {});
  assert.equal(h.observer.notifySpa, undefined);
  assert.equal(h.messages.some(message => message.type === 'spaNavigation'), false);
});

test('trusted local deadline pauses the granted item at exact cutoff without a new media event', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');
  const result = h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 20_000,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  });
  assert.equal(result, true);
  const timer = [...h.timers.values()][0];
  assert.equal(timer.delay, 20_000);
  assert.equal(element.pauseCalls, 0);
  h.setMonotonic(20_000);
  timer.callback();
  assert.equal(element.pauseCalls, 1);
});

test('fail-open cancellation removes a scheduled local pause', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');
  h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 20_000,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  });
  h.observer.handleControl({
    type: 'nightGateControl', command: 'cancelPause', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0,
  });
  assert.equal(h.timers.size, 0);
  assert.equal(element.pauseCalls, 0);
});

test('same-document media keep independent gate deadlines and zero-delay pause is synchronous', async () => {
  const grant = media();
  const loser = media();
  const h = harness([grant, loser]);
  h.observer.start();
  await h.observer.report(grant, 'playing');
  await h.observer.report(loser, 'playing');

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-2', sourceGeneration: 0, delayMs: 0,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  }), true);
  assert.equal(loser.pauseCalls, 1);
  assert.equal(h.timers.size, 0);

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 20_000,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  }), true);
  assert.equal(grant.pauseCalls, 0);
  assert.equal(loser.pauseCalls, 1);
  assert.deepEqual([...h.timers.values()].map(timer => timer.delay), [20_000]);

  loser.paused = false;
  h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-2', sourceGeneration: 0, delayMs: 30_000,
    leaseMs: 40_000, leaseDeadlineMonotonicMs: 40_000,
  });
  assert.equal(h.timers.size, 2);

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'cancelPause', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0,
  }), true);
  assert.deepEqual([...h.timers.values()].map(timer => timer.delay), [30_000]);
  h.setMonotonic(30_000);
  [...h.timers.values()][0].callback();
  assert.equal(loser.pauseCalls, 2);
  assert.equal(grant.pauseCalls, 0);
});

test('pause lease is rechecked with a monotonic clock when a delayed timer finally runs', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 20_000,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  }), true);
  const timer = [...h.timers.values()][0];
  h.setMonotonic(30_000);
  timer.callback();

  assert.equal(element.pauseCalls, 0);
});

test('a cutoff outside the current lease is discarded until a fresh lease reschedules it', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 20_000,
    leaseMs: 10_000, leaseDeadlineMonotonicMs: 10_000,
  }), true);
  assert.equal(h.timers.size, 0);

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 5_000,
    leaseMs: 10_000, leaseDeadlineMonotonicMs: 10_000,
  }), true);
  const timer = [...h.timers.values()][0];
  h.setMonotonic(5_000);
  timer.callback();
  assert.equal(element.pauseCalls, 1);
});

test('pauseAt discards delayed delivery and bounds its timer by the earlier absolute deadline', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');

  h.setMonotonic(40_000);
  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 0,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  }), true);
  assert.equal(element.pauseCalls, 0);
  assert.equal(h.timers.size, 0);

  h.setMonotonic(50_000);
  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 6_000,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 55_000,
  }), true);
  assert.equal(h.timers.size, 0);
});

test('pauseAt exact schema rejects a missing or invalid absolute lease', async () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  await h.observer.report(element, 'playing');

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 0, leaseMs: 30_000,
  }), false);
  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'pauseAt', gateId: 'gate-1',
    mediaToken: 'media-1', sourceGeneration: 0, delayMs: 0,
    leaseMs: 30_000, leaseDeadlineMonotonicMs: Number.POSITIVE_INFINITY,
  }), false);
  assert.equal(element.pauseCalls, 0);
});

test('trusted authoritative SPA control requires a fresh bounded lease', () => {
  const first = media();
  const second = media();
  const h = harness([first, second]);
  h.observer.start();

  const accepted = h.observer.handleControl({
    type: 'nightGateControl', command: 'showLocalPage', page: 'finished',
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  });

  assert.equal(accepted, true);
  assert.equal(first.pauseCalls, 1);
  assert.equal(second.pauseCalls, 1);
  assert.deepEqual(h.localPages, ['finished']);
  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'showLocalPage', page: 'https://example.com',
    leaseMs: 30_000, leaseDeadlineMonotonicMs: 30_000,
  }), false);
  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'showLocalPage', page: 'blocked',
  }), false);
});

test('showLocalPage rechecks expiry before each restrictive side effect', () => {
  const element = media();
  const h = harness([element]);
  h.observer.start();
  h.setMonotonicSequence([1_000, 1_001, 1_002]);

  assert.equal(h.observer.handleControl({
    type: 'nightGateControl', command: 'showLocalPage', page: 'blocked',
    leaseMs: 2, leaseDeadlineMonotonicMs: 100_000,
  }), true);

  assert.equal(element.pauseCalls, 1);
  assert.deepEqual(h.localPages, []);
});

test('content bootstrap maps a local page enum to an extension URL without an original location', () => {
  let dependencies;
  const replacements = [];
  const context = {
    NightGateContentObserver: {
      create(value) {
        dependencies = value;
        return { start() {}, handleControl() { return false; } };
      },
    },
    chrome: {
      runtime: {
        sendMessage() {},
        getURL(path) { return `chrome-extension://nightgate/${path}`; },
        onMessage: { addListener() {} },
      },
    },
    document: {},
    window: { location: { replace(value) { replacements.push(value); } } },
    MutationObserver: class {},
    performance: { timeOrigin: 10_000, now: () => 123 },
    Uint8Array,
    crypto: { randomUUID: () => 'media-token' },
  };
  vm.runInNewContext(bootstrapSource, context, { filename: 'content-script.js' });

  assert.equal(dependencies.monotonicClock(), 10_123);
  dependencies.showLocalPage('finished');
  assert.deepEqual(replacements, ['chrome-extension://nightgate/finished.html']);
  assert.equal(replacements[0].includes('?'), false);
  assert.equal(replacements[0].includes('#'), false);
});

test('content bootstrap is idempotent when an already-open tab is backfilled repeatedly', () => {
  let createCalls = 0;
  let startCalls = 0;
  let listenerCalls = 0;
  const context = {
    NightGateContentObserver: {
      create() {
        createCalls += 1;
        return {
          start() { startCalls += 1; },
          handleControl() { return false; },
        };
      },
    },
    chrome: {
      runtime: {
        sendMessage() {},
        getURL(path) { return `chrome-extension://nightgate/${path}`; },
        onMessage: { addListener() { listenerCalls += 1; } },
      },
    },
    document: {},
    window: { location: { replace() {} } },
    MutationObserver: class {},
    performance: { timeOrigin: 10_000, now: () => 123 },
    Uint8Array,
    crypto: { randomUUID: () => 'media-token' },
  };

  vm.runInNewContext(bootstrapSource, context, { filename: 'content-script.js' });
  vm.runInNewContext(bootstrapSource, context, { filename: 'content-script.js' });

  assert.equal(createCalls, 1);
  assert.equal(startCalls, 1);
  assert.equal(listenerCalls, 1);
});
