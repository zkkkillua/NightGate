(function startNightGateContentScript() {
  'use strict';

  const installationKey = '__nightGateContentScriptInstalledV1';
  if (globalThis[installationKey] === true) return;
  const factory = globalThis.NightGateContentObserver;
  if (!factory || typeof chrome === 'undefined' || !chrome.runtime?.sendMessage) return;

  const tokenFactory = () => {
    const value = globalThis.crypto?.randomUUID?.();
    if (value) return value;
    const bytes = new Uint8Array(16);
    globalThis.crypto?.getRandomValues?.(bytes);
    return Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
  };

  const observer = factory.create({
    document,
    MutationObserverClass: MutationObserver,
    tokenFactory,
    send: message => chrome.runtime.sendMessage(message),
    monotonicClock: () => performance.timeOrigin + performance.now(),
    showLocalPage: page => {
      if (page === 'blocked' || page === 'finished') {
        window.location.replace(chrome.runtime.getURL(`${page}.html`));
      }
    },
  });
  observer.start();
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    const accepted = observer.handleControl(message);
    if (accepted) sendResponse({ accepted: true });
    return accepted;
  });
  Object.defineProperty(globalThis, installationKey, {
    value: true,
    configurable: false,
    enumerable: false,
    writable: false,
  });
}());
