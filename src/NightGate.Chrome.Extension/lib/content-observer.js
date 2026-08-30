(function installContentObserver(root) {
  'use strict';

  const MEDIA_SELECTOR = 'audio,video';
  const MAX_LEASE_MS = 120_000;

  function isMedia(node) {
    return node && typeof node.matches === 'function' && node.matches(MEDIA_SELECTOR);
  }

  function create(dependencies) {
    const { document, MutationObserverClass, tokenFactory, send } = dependencies;
    const setTimeoutFn = dependencies.setTimeoutFn || globalThis.setTimeout;
    const clearTimeoutFn = dependencies.clearTimeoutFn || globalThis.clearTimeout;
    const monotonicClock = dependencies.monotonicClock
      || (() => (globalThis.performance?.timeOrigin ?? Number.NaN)
        + (globalThis.performance?.now?.() ?? Number.NaN));
    const showLocalPage = dependencies.showLocalPage || (() => {});
    const metadata = new WeakMap();
    const tracked = new Set();
    let mutationObserver = null;
    let started = false;
    const deadlineTimers = new Map();

    function receiveLease(value) {
      if (!value || !Number.isFinite(value.leaseMs)
          || value.leaseMs <= 0 || value.leaseMs > MAX_LEASE_MS
          || !Number.isFinite(value.leaseDeadlineMonotonicMs)
          || value.leaseDeadlineMonotonicMs <= 0) return null;
      const receivedAtMs = monotonicClock();
      const relativeDeadlineMs = receivedAtMs + value.leaseMs;
      if (!Number.isFinite(receivedAtMs) || !Number.isFinite(relativeDeadlineMs)) return null;
      return {
        receivedAtMs,
        deadlineMs: Math.min(value.leaseDeadlineMonotonicMs, relativeDeadlineMs),
      };
    }

    function leaseFresh(lease) {
      const nowMs = monotonicClock();
      return Number.isFinite(nowMs)
        && nowMs >= lease.receivedAtMs
        && nowMs < lease.deadlineMs;
    }

    function remember(element) {
      let entry = metadata.get(element);
      if (!entry) {
        entry = { token: tokenFactory(), generation: 0 };
        metadata.set(element, entry);
        tracked.add(element);
      }
      return entry;
    }

    function clearElementDeadlines(element) {
      for (const [key, timer] of deadlineTimers) {
        if (timer.element !== element) continue;
        clearTimeoutFn(timer.id);
        deadlineTimers.delete(key);
      }
    }

    async function report(element, playback) {
      if (!isMedia(element)) return;
      const entry = remember(element);
      let response;
      try {
        response = await send({
          type: 'mediaObservation',
          mediaToken: entry.token,
          sourceGeneration: entry.generation,
          playback,
        });
      } catch {
        return;
      }
      if (!exactControlKeys(response, [
        'decision', 'leaseMs', 'leaseDeadlineMonotonicMs',
      ]) || response.decision !== 'pause') return;
      const lease = receiveLease(response);
      if (lease && leaseFresh(lease) && !element.paused) element.pause();
    }

    function observeElement(element) {
      if (!isMedia(element)) return;
      remember(element);
      if (!element.paused && !element.ended) void report(element, 'playing');
    }

    function scan(node) {
      if (isMedia(node)) observeElement(node);
      if (node && typeof node.querySelectorAll === 'function') {
        for (const element of node.querySelectorAll(MEDIA_SELECTOR)) observeElement(element);
      }
    }

    function eventPlayback(event) {
      const playback = event.type === 'play' ? 'playing' : event.type === 'pause' ? 'paused' : 'ended';
      return report(event.target, playback);
    }

    function sourceChanged(event) {
      const element = event.target;
      if (!isMedia(element)) return;
      const entry = remember(element);
      clearElementDeadlines(element);
      entry.generation += 1;
      const playback = element.ended ? 'ended' : element.paused ? 'paused' : 'playing';
      return report(element, playback);
    }

    function exactControlKeys(message, keys) {
      return message && typeof message === 'object' && !Array.isArray(message)
        && Object.keys(message).length === keys.length
        && keys.every(key => Object.prototype.hasOwnProperty.call(message, key));
    }

    function deadlineKey(message) {
      return JSON.stringify([message.gateId, message.mediaToken, message.sourceGeneration]);
    }

    function validMediaIdentity(message) {
      return typeof message.mediaToken === 'string'
        && Number.isInteger(message.sourceGeneration) && message.sourceGeneration >= 0;
    }

    function handleControl(message) {
      if (message?.type !== 'nightGateControl') return false;
      if (message.command === 'showLocalPage') {
        if (!exactControlKeys(message, [
          'type', 'command', 'page', 'leaseMs', 'leaseDeadlineMonotonicMs',
        ])
            || !['blocked', 'finished'].includes(message.page)) return false;
        const lease = receiveLease(message);
        if (!lease) return false;
        for (const element of tracked) {
          if (!leaseFresh(lease)) return true;
          if (!element.paused) element.pause();
        }
        if (leaseFresh(lease)) showLocalPage(message.page);
        return true;
      }
      if (typeof message.gateId !== 'string' || !message.gateId) return false;
      if (message.command === 'cancelPause') {
        if (!exactControlKeys(message, ['type', 'command', 'gateId', 'mediaToken', 'sourceGeneration'])
            || !validMediaIdentity(message)) return false;
        const key = deadlineKey(message);
        const prior = deadlineTimers.get(key);
        if (prior) clearTimeoutFn(prior.id);
        deadlineTimers.delete(key);
        return true;
      }
      if (message.command !== 'pauseAt'
          || !exactControlKeys(message, [
            'type', 'command', 'gateId', 'mediaToken', 'sourceGeneration',
            'delayMs', 'leaseMs', 'leaseDeadlineMonotonicMs',
          ])
          || !validMediaIdentity(message)
          || !Number.isFinite(message.delayMs) || message.delayMs < 0 || message.delayMs > 6 * 60 * 60_000
      ) return false;
      const lease = receiveLease(message);
      if (!lease) return false;
      const element = [...tracked].find(candidate => {
        const entry = metadata.get(candidate);
        return entry?.token === message.mediaToken && entry.generation === message.sourceGeneration;
      });
      if (!element) return false;
      const key = deadlineKey(message);
      const prior = deadlineTimers.get(key);
      if (prior) clearTimeoutFn(prior.id);
      deadlineTimers.delete(key);
      if (!leaseFresh(lease)) return true;
      const scheduleAtMs = monotonicClock();
      if (!Number.isFinite(scheduleAtMs)
          || scheduleAtMs < lease.receivedAtMs
          || scheduleAtMs >= lease.deadlineMs
          || message.delayMs >= lease.deadlineMs - scheduleAtMs) return true;
      if (message.delayMs === 0) {
        if (leaseFresh(lease) && !element.paused) element.pause();
        return true;
      }
      const id = setTimeoutFn(() => {
        deadlineTimers.delete(key);
        const current = metadata.get(element);
        if (leaseFresh(lease)
            && current?.token === message.mediaToken
            && current.generation === message.sourceGeneration
            && !element.paused) element.pause();
      }, message.delayMs);
      deadlineTimers.set(key, { id, element });
      return true;
    }

    function start() {
      if (started) return;
      started = true;
      for (const name of ['play', 'pause', 'ended']) document.addEventListener(name, eventPlayback, true);
      for (const name of ['loadstart', 'emptied']) document.addEventListener(name, sourceChanged, true);
      for (const element of document.querySelectorAll(MEDIA_SELECTOR)) observeElement(element);
      mutationObserver = new MutationObserverClass(records => {
        for (const record of records) for (const node of record.addedNodes || []) scan(node);
      });
      mutationObserver.observe(document, { childList: true, subtree: true });
    }

    function stop() {
      if (!started) return;
      started = false;
      for (const name of ['play', 'pause', 'ended']) document.removeEventListener(name, eventPlayback, true);
      for (const name of ['loadstart', 'emptied']) document.removeEventListener(name, sourceChanged, true);
      mutationObserver?.disconnect();
      for (const timer of deadlineTimers.values()) clearTimeoutFn(timer.id);
      deadlineTimers.clear();
    }

    return Object.freeze({ start, stop, report, scan, handleControl });
  }

  root.NightGateContentObserver = Object.freeze({ create });
}(globalThis));
