import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(
  new URL('../../src/NightGate.Chrome.Extension/service-worker.js', import.meta.url),
  'utf8',
);

function instrumentServiceWorker() {
  return source
    .replace(/^import .*?;\r?\n/gmu, '')
    .replace('void start();', 'globalThis.startPromise = start();')
    .replace('void listenerAdapter.start().catch(() => {});', 'globalThis.startPromise = listenerAdapter.start();')
    .replace('void listenerAdapter.start();', 'globalThis.startPromise = listenerAdapter.start();');
}

test('service worker wires the real fixed native transport instead of a placeholder', () => {
  assert.match(source, /createNativeTransport\(chrome,/u);
  assert.match(source, /chrome\.runtime\.getManifest\(\)\.version/u);
  assert.doesNotMatch(source, /unavailableTransport/u);
});

test('service worker passes the manifest version into the controller heartbeat dependency', async () => {
  const profileToken = 'A'.repeat(43);
  let controllerDependencies;
  const chrome = {
    runtime: { getManifest: () => ({ version: '4.5.6' }) },
    storage: { local: { async get() { return { profileToken }; } } },
  };
  const context = {
    chrome,
    createChromeEffects: () => ({ async clearDnr() {} }),
    createNativeTransport: () => ({ transport: true }),
    createWorkerController: dependencies => {
      controllerDependencies = dependencies;
      return { async init() {} };
    },
    attachChromeListeners: (_chrome, sourceValue, options) => ({
      async start() {
        await options.clearProtection();
        await (await sourceValue()).init();
      },
    }),
    crypto: { getRandomValues() {} },
    btoa: () => '',
  };

  vm.runInNewContext(instrumentServiceWorker(), context, { filename: 'service-worker.js' });
  await context.startPromise;

  assert.equal(controllerDependencies.extensionVersion, '4.5.6');
});

test('service worker clears DNR before profile-token local storage can fail', async () => {
  const order = [];
  const chrome = {
    storage: {
      local: {
        async get() {
          order.push('profileToken');
          throw new Error('local storage unavailable');
        },
      },
    },
  };
  const instrumented = instrumentServiceWorker();
  const context = {
    chrome,
    createChromeEffects: () => ({
      async clearDnr() { order.push('clearDnr'); },
    }),
    createWorkerController: () => ({}),
    attachChromeListeners: (_chrome, sourceValue, options) => ({
      async start() {
        await options.clearProtection();
        const controller = typeof sourceValue === 'function' ? await sourceValue() : sourceValue;
        await controller?.init?.();
      },
    }),
    clearSessionRulesBestEffort: async () => { throw new Error('standalone cleanup must not be used'); },
    crypto: { getRandomValues() {} },
    btoa: () => '',
  };

  vm.runInNewContext(instrumented, context, { filename: 'service-worker.js' });
  await assert.rejects(context.startPromise, /local storage unavailable/);

  assert.deepEqual(order, ['clearDnr', 'profileToken']);
});

test('service worker registers listeners synchronously before startup cleanup settles', async () => {
  const order = [];
  let releaseCleanup;
  const cleanupGate = new Promise(resolve => { releaseCleanup = resolve; });
  const chrome = {
    storage: { local: { async get() { throw new Error('stop after cleanup'); } } },
  };
  const instrumented = instrumentServiceWorker();
  const context = {
    chrome,
    createChromeEffects: () => ({
      async clearDnr() {
        order.push('clearDnr');
        await cleanupGate;
      },
    }),
    createWorkerController: () => ({}),
    attachChromeListeners(_chrome, sourceValue, options) {
      order.push('listeners');
      return {
        async start() {
          await options.clearProtection();
          const controller = typeof sourceValue === 'function' ? await sourceValue() : sourceValue;
          await controller?.init?.();
        },
      };
    },
    async clearSessionRulesBestEffort() { throw new Error('standalone cleanup must not be used'); },
    crypto: { getRandomValues() {} },
    btoa: () => '',
  };

  vm.runInNewContext(instrumented, context, { filename: 'service-worker.js' });
  assert.equal(order[0], 'listeners');
  releaseCleanup();
  await assert.rejects(context.startPromise, /stop after cleanup/);
});
