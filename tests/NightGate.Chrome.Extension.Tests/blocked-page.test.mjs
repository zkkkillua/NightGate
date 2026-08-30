import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const scriptPath = fileURLToPath(new URL('../../src/NightGate.Chrome.Extension/blocked-page.js', import.meta.url));
const pagePath = fileURLToPath(new URL('../../src/NightGate.Chrome.Extension/blocked.html', import.meta.url));

const flushAsyncWork = async () => {
  await Promise.resolve();
  await new Promise(resolve => setImmediate(resolve));
};

async function createPage(sendMessage, options = {}) {
  assert.equal(existsSync(scriptPath), true, 'blocked-page.js must exist');
  const timeOrigin = options.timeOrigin ?? 1_000;
  let elapsedMs = options.elapsedMs ?? 200;
  let backs = 0;
  let nextTimerId = 1;
  let locationReads = 0;
  const sent = [];
  const timers = new Map();
  const notice = { hidden: true };
  const absoluteNow = () => timeOrigin + elapsedMs;
  const context = {
    chrome: {
      runtime: {
        async sendMessage(message) {
          sent.push(structuredClone(message));
          return sendMessage(message, sent.length);
        },
      },
    },
    history: {
      length: options.historyLength ?? 2,
      back() { backs += 1; },
    },
    document: {
      getElementById(id) { return id === 'protection-released' ? notice : null; },
    },
    performance: {
      timeOrigin,
      now() { return elapsedMs; },
    },
    setTimeout(callback, delayMs) {
      const id = nextTimerId;
      nextTimerId += 1;
      timers.set(id, { callback, deadline: absoluteNow() + delayMs });
      return id;
    },
    clearTimeout(id) {
      timers.delete(id);
    },
  };
  Object.defineProperty(context, 'location', {
    configurable: false,
    get() {
      locationReads += 1;
      throw new Error('blocked page must not inspect the original URL');
    },
  });

  vm.runInNewContext(readFileSync(scriptPath, 'utf8'), context, { filename: scriptPath });
  await flushAsyncWork();

  return {
    get backs() { return backs; },
    get locationReads() { return locationReads; },
    get noticeShown() { return notice.hidden === false; },
    get sent() { return structuredClone(sent); },
    get timerDeadlines() {
      return [...timers.values()].map(timer => timer.deadline).sort((left, right) => left - right);
    },
    async advanceTo(deadline) {
      assert.ok(deadline >= absoluteNow(), 'fake monotonic clock cannot move backwards');
      elapsedMs = deadline - timeOrigin;
      while (true) {
        const dueTimers = [...timers.entries()]
          .filter(([, timer]) => timer.deadline <= absoluteNow())
          .sort((left, right) => left[1].deadline - right[1].deadline);
        if (dueTimers.length === 0) break;
        for (const [id, timer] of dueTimers) {
          timers.delete(id);
          timer.callback();
        }
        await flushAsyncWork();
      }
    },
    jumpToWithoutRunningTimers(deadline) {
      assert.ok(deadline >= absoluteNow(), 'fake monotonic clock cannot move backwards');
      elapsedMs = deadline - timeOrigin;
    },
  };
}

test('fresh stayBlocked renews before the lease boundary while keeping a hard release timer', async () => {
  const responses = [
    { decision: 'stayBlocked', leaseMs: 300, leaseDeadlineMonotonicMs: 2_000 },
    { decision: 'stayBlocked', leaseMs: 1_000, leaseDeadlineMonotonicMs: 2_200 },
    { decision: 'allow' },
  ];
  const page = await createPage(async () => responses.shift());

  assert.deepEqual(page.sent, [{ type: 'blockedPageFreshness' }]);
  assert.deepEqual(page.timerDeadlines, [1_350, 1_500], 'renewal precedes the hard lease boundary');
  assert.equal(page.backs, 0);

  await page.advanceTo(1_350);
  assert.deepEqual(page.sent, [
    { type: 'blockedPageFreshness' },
    { type: 'blockedPageFreshness' },
  ]);
  assert.deepEqual(page.timerDeadlines, [1_775, 2_200], 'absolute deadline caps the refreshed lease duration');
  assert.equal(page.backs, 0);

  await page.advanceTo(1_775);
  assert.equal(page.sent.length, 3);
  assert.equal(page.backs, 1);
  assert.deepEqual(page.timerDeadlines, []);
  assert.equal(page.locationReads, 0);
});

test('an initial freshness request that never settles fails open on a short watchdog', async () => {
  const never = new Promise(() => {});
  const page = await createPage(async () => never);

  assert.deepEqual(page.timerDeadlines, [3_200]);
  await page.advanceTo(3_200);

  assert.equal(page.backs, 1);
  assert.deepEqual(page.timerDeadlines, []);
});

test('a pending or late renewal cannot retain the page beyond the prior absolute lease', async () => {
  let resolveRenewal;
  const renewal = new Promise(resolve => { resolveRenewal = resolve; });
  const page = await createPage(async (_message, attempt) => attempt === 1
    ? { decision: 'stayBlocked', leaseMs: 300, leaseDeadlineMonotonicMs: 1_500 }
    : renewal);

  assert.deepEqual(page.timerDeadlines, [1_350, 1_500]);
  await page.advanceTo(1_350);
  assert.equal(page.sent.length, 2);
  assert.deepEqual(page.timerDeadlines, [1_500]);

  await page.advanceTo(1_500);
  assert.equal(page.backs, 1);
  resolveRenewal({
    decision: 'stayBlocked', leaseMs: 1_000, leaseDeadlineMonotonicMs: 3_000,
  });
  await flushAsyncWork();

  assert.equal(page.backs, 1);
  assert.deepEqual(page.timerDeadlines, []);
});

test('a renewal response arriving after the old deadline fails open even if its timer callback was delayed', async () => {
  let resolveRenewal;
  const renewal = new Promise(resolve => { resolveRenewal = resolve; });
  const page = await createPage(async (_message, attempt) => attempt === 1
    ? { decision: 'stayBlocked', leaseMs: 300, leaseDeadlineMonotonicMs: 1_500 }
    : renewal);
  await page.advanceTo(1_350);

  page.jumpToWithoutRunningTimers(1_501);
  resolveRenewal({
    decision: 'stayBlocked', leaseMs: 1_000, leaseDeadlineMonotonicMs: 3_000,
  });
  await flushAsyncWork();

  assert.equal(page.backs, 1);
  assert.deepEqual(page.timerDeadlines, []);
});

test('invalid or expired restrictive responses fail open instead of retaining a stale block', async () => {
  const invalidResponses = [
    ['missing lease fields', { decision: 'stayBlocked' }],
    ['zero lease', { decision: 'stayBlocked', leaseMs: 0, leaseDeadlineMonotonicMs: 2_000 }],
    ['oversized lease', { decision: 'stayBlocked', leaseMs: 120_001, leaseDeadlineMonotonicMs: 200_000 }],
    ['expired absolute deadline', { decision: 'stayBlocked', leaseMs: 1_000, leaseDeadlineMonotonicMs: 1_200 }],
    ['unexpected field', {
      decision: 'stayBlocked', leaseMs: 1_000, leaseDeadlineMonotonicMs: 2_000, originalUrl: 'https://example.test',
    }],
  ];

  for (const [label, response] of invalidResponses) {
    const page = await createPage(async () => response);
    assert.equal(page.backs, 1, label);
    assert.deepEqual(page.timerDeadlines, [], label);
    assert.equal(page.locationReads, 0, label);
  }
});

test('blocked page returns through browser history on fail-open or worker failure without reading a URL', async () => {
  const allowed = await createPage(async () => ({ decision: 'allow' }));
  const failed = await createPage(async () => { throw new Error('worker unavailable'); });
  assert.equal(allowed.backs, 1);
  assert.equal(failed.backs, 1);
  assert.deepEqual(allowed.sent, [{ type: 'blockedPageFreshness' }]);
  assert.deepEqual(failed.sent, [{ type: 'blockedPageFreshness' }]);
  assert.equal(allowed.locationReads, 0);
  assert.equal(failed.locationReads, 0);
});

test('stale blocked page with no prior history explicitly says protection ended', async () => {
  const allowed = await createPage(async () => ({ decision: 'allow' }), { historyLength: 1 });
  const failed = await createPage(async () => { throw new Error('worker unavailable'); }, { historyLength: 1 });
  assert.equal(allowed.backs, 0);
  assert.deepEqual(allowed.sent, [{ type: 'blockedPageFreshness' }]);
  assert.equal(allowed.noticeShown, true);
  assert.equal(failed.backs, 0);
  assert.equal(failed.noticeShown, true);
  const page = readFileSync(pagePath, 'utf8');
  assert.match(page, /id="protection-released"/);
  assert.match(page, /&#20445;&#25252;&#24050;&#35299;&#38500;&#65292;&#35831;&#37325;&#35797;&#12290;/);
});
