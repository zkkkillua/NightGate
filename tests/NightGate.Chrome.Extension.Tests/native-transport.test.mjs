import test from 'node:test';
import assert from 'node:assert/strict';

import {
  NATIVE_HOST_NAME,
  createNativeTransport,
} from '../../src/NightGate.Chrome.Extension/lib/native-transport.mjs';

const profileToken = 'A'.repeat(43);
const policy = Object.freeze({
  revision: 7,
  gateId: 'gate-7',
  evaluatedAtUtc: '2026-07-14T15:30:00.000Z',
  lastStartAtUtc: '2026-07-14T15:35:00.000Z',
  ttlMs: 45_000,
  mode: 'unrestricted',
  overrideKind: null,
  lockAtUtc: '2026-07-14T16:10:00.000Z',
  wakeAtUtc: '2026-07-15T01:00:00.000Z',
  siteRules: [],
});

function harness({ reply, failure, requestId = 'request-1' } = {}) {
  const calls = [];
  const chromeApi = {
    runtime: {
      async sendNativeMessage(hostName, message) {
        calls.push({ hostName, message: structuredClone(message) });
        if (failure) throw failure;
        if (typeof reply === 'function') return reply(structuredClone(message));
        return reply;
      },
    },
  };
  const transport = createNativeTransport(chromeApi, {
    profileToken,
    requestIdFactory: () => requestId,
    timeoutMs: 1_000,
  });
  return { calls, transport };
}

test('getPolicy uses the fixed native host and validates the correlated response', async () => {
  const h = harness({
    reply: request => ({
      version: 1,
      type: 'getPolicyResult',
      requestId: request.requestId,
      profileToken: request.profileToken,
      payload: policy,
    }),
  });

  const result = await h.transport.getPolicy({
    minimumRevision: 6,
    profileToken,
  });

  assert.deepEqual(result, policy);
  assert.deepEqual(h.calls, [{
    hostName: NATIVE_HOST_NAME,
    message: {
      version: 1,
      type: 'getPolicy',
      requestId: 'request-1',
      profileToken,
      payload: {},
    },
  }]);
  assert.equal(Object.isFrozen(result), true);
});

test('send accepts only the host command whitelist and returns a validated acknowledgement', async () => {
  const h = harness({
    reply: request => ({
      version: 1,
      type: `${request.type}Result`,
      requestId: request.requestId,
      profileToken: request.profileToken,
      payload: { accepted: true },
    }),
  });
  const event = {
    timestamp: '2026-07-14T15:30:00.000Z',
    eventType: 'mediaPlaying',
    ruleId: 'video-rule',
    category: 'video',
  };

  assert.equal(await h.transport.send('mediaState', event), true);
  assert.equal(h.calls[0].message.type, 'mediaState');
  const heartbeat = {
    revision: 7,
    extensionVersion: '0.1.0',
    incognitoAllowed: false,
    protectionReady: true,
  };
  assert.equal(await h.transport.send('heartbeat', heartbeat), true);
  assert.deepEqual(h.calls[1].message.payload, heartbeat);
  await assert.rejects(() => h.transport.send('requestOverride', {}), TypeError);
  assert.equal(h.calls.length, 2);
});

test('wrong correlation and malformed responses fail open instead of being trusted', async () => {
  const h = harness({
    reply: {
      version: 1,
      type: 'getPolicyResult',
      requestId: 'someone-else',
      profileToken,
      payload: policy,
    },
  });

  await assert.rejects(
    () => h.transport.getPolicy({ minimumRevision: -1, profileToken }),
    TypeError,
  );
});

test('a stale policy is rejected against the caller minimum revision', async () => {
  const h = harness({
    reply: request => ({
      version: 1,
      type: 'getPolicyResult',
      requestId: request.requestId,
      profileToken,
      payload: { ...policy, revision: 4 },
    }),
  });

  await assert.rejects(
    () => h.transport.getPolicy({ minimumRevision: 5, profileToken }),
    TypeError,
  );
});

test('missing native host and timeout are marked as immediate fail-open failures', async () => {
  const missing = harness({ failure: new Error('Specified native messaging host not found.') });
  await assert.rejects(
    () => missing.transport.getPolicy({ minimumRevision: -1, profileToken }),
    error => error instanceof Error && error.failOpen === true,
  );

  const never = new Promise(() => {});
  const timed = harness({ reply: () => never });
  timed.transport = createNativeTransport({
    runtime: { sendNativeMessage: () => never },
  }, {
    profileToken,
    requestIdFactory: () => 'timeout-1',
    timeoutMs: 5,
  });
  await assert.rejects(
    () => timed.transport.getPolicy({ minimumRevision: -1, profileToken }),
    error => error instanceof Error && error.failOpen === true && /timeout/i.test(error.message),
  );
});

test('constructor rejects mutable host selection and invalid dependencies', () => {
  assert.throws(() => createNativeTransport({}, { profileToken }), TypeError);
  assert.throws(() => createNativeTransport({ runtime: { sendNativeMessage() {} } }, {
    profileToken: 'short',
  }), TypeError);
  assert.equal(NATIVE_HOST_NAME, 'com.nightgate.host');
});
