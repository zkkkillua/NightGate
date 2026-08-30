import { attachChromeListeners, createChromeEffects } from './lib/chrome-adapter.mjs';
import { createNativeTransport } from './lib/native-transport.mjs';
import { createWorkerController } from './lib/worker-controller.mjs';

function newProfileToken() {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

async function profileToken() {
  const stored = await chrome.storage.local.get('profileToken');
  if (/^[A-Za-z0-9_-]{43}$/.test(stored.profileToken ?? '')) return stored.profileToken;
  const value = newProfileToken();
  await chrome.storage.local.set({ profileToken: value });
  return value;
}

const monotonicEpochClock = () => performance.timeOrigin + performance.now();
const effects = createChromeEffects(chrome, { monotonicEpochClock });

async function buildController() {
  const token = await profileToken();
  return createWorkerController({
    wallClock: () => Date.now(),
    monotonicClock: () => performance.now(),
    monotonicEpochClock,
    transport: createNativeTransport(chrome, { profileToken: token }),
    effects,
    profileToken: token,
    extensionVersion: chrome.runtime.getManifest().version,
  });
}

const listenerAdapter = attachChromeListeners(chrome, buildController, {
  clearProtection: () => effects.clearDnr(),
});
void listenerAdapter.start().catch(() => {});
