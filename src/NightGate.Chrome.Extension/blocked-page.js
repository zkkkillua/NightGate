(function verifyNightGateBlock() {
  'use strict';

  const MAX_LEASE_MS = 120_000;
  const INITIAL_HANDSHAKE_TIMEOUT_MS = 2_000;
  let active = true;
  let releaseTimer = null;
  let renewalTimer = null;
  let requestSequence = 0;
  let activeLeaseDeadline = null;

  const monotonicNow = () => performance.timeOrigin + performance.now();

  const clearTimers = () => {
    if (releaseTimer !== null) clearTimeout(releaseTimer);
    if (renewalTimer !== null) clearTimeout(renewalTimer);
    releaseTimer = null;
    renewalTimer = null;
  };

  const resumePriorPage = () => {
    if (!active) return;
    active = false;
    requestSequence += 1;
    activeLeaseDeadline = null;
    clearTimers();
    if (Number.isInteger(history.length) && history.length > 1) {
      history.back();
      return;
    }
    const notice = document.getElementById('protection-released');
    if (notice) notice.hidden = false;
  };

  const leaseDeadline = response => {
    if (!response || typeof response !== 'object' || Array.isArray(response)) return null;
    const keys = Object.keys(response).sort();
    if (keys.length !== 3
      || keys[0] !== 'decision'
      || keys[1] !== 'leaseDeadlineMonotonicMs'
      || keys[2] !== 'leaseMs'
      || response.decision !== 'stayBlocked'
      || !Number.isFinite(response.leaseMs)
      || response.leaseMs <= 0
      || response.leaseMs > MAX_LEASE_MS
      || !Number.isFinite(response.leaseDeadlineMonotonicMs)) return null;

    const now = monotonicNow();
    if (!Number.isFinite(now)) return null;
    const deadline = Math.min(response.leaseDeadlineMonotonicMs, now + response.leaseMs);
    return Number.isFinite(deadline) && deadline > now ? { deadline, now } : null;
  };

  const armLease = lease => {
    clearTimers();
    activeLeaseDeadline = lease.deadline;
    const remainingMs = lease.deadline - lease.now;
    releaseTimer = setTimeout(resumePriorPage, remainingMs);
    const renewalLeadMs = Math.min(1_000, remainingMs / 2);
    renewalTimer = setTimeout(() => {
      renewalTimer = null;
      refresh(true);
    }, remainingMs - renewalLeadMs);
  };

  const refresh = hasActiveLease => {
    if (!active) return;
    const requestId = ++requestSequence;
    const requestNow = monotonicNow();
    const responseBoundary = hasActiveLease
      ? activeLeaseDeadline
      : requestNow + INITIAL_HANDSHAKE_TIMEOUT_MS;
    if (!Number.isFinite(requestNow) || !Number.isFinite(responseBoundary)
        || requestNow >= responseBoundary) {
      resumePriorPage();
      return;
    }
    if (!hasActiveLease) {
      clearTimers();
      releaseTimer = setTimeout(resumePriorPage, responseBoundary - requestNow);
    }
    try {
      Promise.resolve(chrome.runtime.sendMessage({ type: 'blockedPageFreshness' }))
        .then(response => {
          if (!active || requestId !== requestSequence) return;
          const lease = leaseDeadline(response);
          if (!lease || lease.now >= responseBoundary) {
            resumePriorPage();
            return;
          }
          armLease(lease);
        })
        .catch(() => {
          if (active && requestId === requestSequence) resumePriorPage();
        });
    } catch {
      resumePriorPage();
    }
  };

  refresh(false);
}());
