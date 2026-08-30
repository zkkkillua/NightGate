import { domainMatchesHost, normalizeDomain, normalizeUrlHost } from './domain.mjs';

export const HEARTBEAT_ALARM = 'nightgate-policy-heartbeat';
export const POLICY_EXPIRY_ALARM = 'nightgate-policy-expiry';
export const LAST_START_ALARM = 'nightgate-policy-last-start';
export const LOCK_ALARM = 'nightgate-policy-lock';
const SESSION_KEY = 'nightGateSession';
const SCRIPT_PREFIX = 'nightgate-site-';
const ONE_SHOT_ALARMS = [POLICY_EXPIRY_ALARM, LAST_START_ALARM, LOCK_ALARM];
const ALARM_KIND = Object.freeze({
  [HEARTBEAT_ALARM]: 'heartbeat',
  [POLICY_EXPIRY_ALARM]: 'policyExpiry',
  [LAST_START_ALARM]: 'lastStart',
  [LOCK_ALARM]: 'lock',
});
const MAX_POLICY_LEASE_MS = 120_000;

function fireAndForget(operation) {
  try {
    Promise.resolve(operation()).catch(() => {});
  } catch {
    // Browser listeners cannot surface an asynchronous result; the fail-open path handles it.
  }
}

async function setVisibleStatus(chromeApi, text) {
  await chromeApi.storage.local.set({ protectionStatus: text });
  const warning = text.includes('降级') || text.includes('未受保护');
  const actionUpdates = [
    ['setTitle', { title: text }],
    ['setBadgeText', { text: warning ? '!' : '' }],
    ...(warning ? [['setBadgeBackgroundColor', { color: '#b45309' }]] : []),
  ];
  for (const [method, value] of actionUpdates) {
    try {
      await chromeApi.action?.[method]?.(value);
    } catch {
      // The stored status remains available even if the toolbar surface is unavailable.
    }
  }
}

export async function clearSessionRulesBestEffort(chromeApi) {
  try {
    const existing = await chromeApi.declarativeNetRequest.getSessionRules();
    const removeRuleIds = [...new Set(existing.map(rule => rule.id))].sort((a, b) => a - b);
    await chromeApi.declarativeNetRequest.updateSessionRules({ removeRuleIds, addRules: [] });
    return true;
  } catch {
    return false;
  }
}

function approvedNavigationDomains(value) {
  if (!Array.isArray(value)) return [];
  const normalized = [];
  for (const item of value) {
    try {
      normalized.push(normalizeDomain(item));
    } catch {
      // Ignore malformed local settings instead of widening the listener filter.
    }
  }
  return [...new Set(normalized)].sort();
}

function supportedNavigationDomains(chromeApi) {
  let patterns;
  try {
    patterns = chromeApi.runtime.getManifest()?.optional_host_permissions;
  } catch {
    return [];
  }
  if (!Array.isArray(patterns)) return [];
  const domains = patterns.map(pattern => {
    if (typeof pattern !== 'string') return null;
    const match = /^(?:http|https):\/\/(?:\*\.)?([^/*]+)\/\*$/.exec(pattern);
    return match?.[1] ?? null;
  }).filter(Boolean);
  return approvedNavigationDomains(domains);
}

function navigationUrlFilter(domains) {
  return {
    url: domains.flatMap(domain => [
      { schemes: ['http', 'https'], hostEquals: domain },
      { schemes: ['http', 'https'], hostSuffix: `.${domain}` },
    ]),
  };
}

function isApprovedNavigationUrl(value, domains) {
  try {
    const host = normalizeUrlHost(value);
    return domains.some(domain => domainMatchesHost(host, domain));
  } catch {
    return false;
  }
}

function projectNavigation(details, navigationKind) {
  const projected = {
    tabId: details?.tabId,
    frameId: details?.frameId,
    url: `https://${normalizeUrlHost(details?.url)}/`,
    navigationKind,
  };
  if (typeof details?.documentId === 'string') projected.documentId = details.documentId;
  return projected;
}

export function attachChromeListeners(chromeApi, controllerSource, options = {}) {
  const controllerFactory = typeof controllerSource === 'function'
    ? controllerSource
    : async () => controllerSource;
  const clearProtection = typeof options.clearProtection === 'function'
    ? options.clearProtection
    : () => clearSessionRulesBestEffort(chromeApi);
  const supportedDomains = supportedNavigationDomains(chromeApi);
  let controller = null;
  let readiness = null;
  let invocationTail = Promise.resolve();
  let failOpenEpoch = 0;
  let navigationDomains = [];
  let navigationApprovalsReady = false;
  let navigationApprovalFailed = false;
  let navigationApprovalEpoch = 0;
  let navigationApprovalReadiness = Promise.resolve();

  const clearProtectionBestEffort = async () => {
    try {
      return await clearProtection() !== false;
    } catch {
      return false;
    }
  };

  const publishDegradedBestEffort = async () => {
    try {
      await setVisibleStatus(chromeApi, '网页保护降级');
    } catch {
      // DNR cleanup remains the primary fail-open boundary when status storage is unavailable.
    }
  };

  const beginNavigationApprovalLoad = () => {
    const loadEpoch = navigationApprovalEpoch;
    navigationApprovalFailed = false;
    const attempt = (async () => {
      const settings = await chromeApi.storage.local.get('approvedDomains');
      if (navigationApprovalEpoch === loadEpoch) {
        installApprovedNavigationDomains(settings?.approvedDomains);
        navigationApprovalsReady = true;
        navigationApprovalFailed = false;
      }
    })();
    navigationApprovalReadiness = attempt;
    void attempt.catch(() => {
      if (navigationApprovalReadiness === attempt) navigationApprovalFailed = true;
    });
    return attempt;
  };

  const ensureNavigationApprovals = () => navigationApprovalFailed
    ? beginNavigationApprovalLoad()
    : navigationApprovalReadiness;

  const initialize = () => {
    if (controller) return Promise.resolve(controller);
    if (readiness) return readiness;
    const attempt = (async () => {
      if (!await clearProtectionBestEffort()) {
        throw new Error('legacy DNR cleanup is unavailable');
      }
      await chromeApi.alarms.clear(HEARTBEAT_ALARM);
      await chromeApi.alarms.create(HEARTBEAT_ALARM, { delayInMinutes: 0.5, periodInMinutes: 0.5 });
      await ensureNavigationApprovals();
      const nextController = await controllerFactory();
      if (!nextController || typeof nextController.init !== 'function') {
        throw new TypeError('invalid worker controller');
      }
      await nextController.init();
      controller = nextController;
      return controller;
    })();
    let tracked;
    tracked = attempt
      .catch(async error => {
        await clearProtectionBestEffort();
        await publishDegradedBestEffort();
        throw error;
      })
      .finally(() => {
        if (readiness === tracked) readiness = null;
      });
    readiness = tracked;
    return tracked;
  };

  const invokeOnce = async (method, ...args) => {
    try {
      const ready = await initialize();
      if (typeof ready[method] !== 'function') throw new TypeError('invalid worker handler');
      return await ready[method](...args);
    } catch (error) {
      controller = null;
      await clearProtectionBestEffort();
      await publishDegradedBestEffort();
      throw error;
    }
  };

  const enqueueInvocation = (precondition, method, ...args) => {
    const entryEpoch = failOpenEpoch;
    const operation = invocationTail.then(async () => {
      await precondition;
      const result = await invokeOnce(method, ...args);
      return method === 'onContentMessage' && entryEpoch !== failOpenEpoch
        ? { decision: 'allow' }
        : result;
    });
    invocationTail = operation.catch(() => {});
    return operation;
  };

  const invoke = (method, ...args) => enqueueInvocation(Promise.resolve(), method, ...args);

  const enqueuePermissionRevalidation = (retryPolicy, failOpenFirst = Promise.resolve()) => {
    const operation = invocationTail.then(async () => {
      await failOpenFirst;
      try {
        await invokeOnce('onAlarm', { kind: 'policyExpiry' });
      } catch {
        // The alarm path already clears stale protection before reporting its failure.
      }
      if (retryPolicy) await invokeOnce('refresh');
      await invokeOnce('onAlarm', { kind: 'heartbeat' });
    });
    invocationTail = operation.catch(() => {});
    return operation;
  };

  const installApprovedNavigationDomains = rawDomains => {
    const supported = new Set(supportedDomains);
    navigationDomains = approvedNavigationDomains(rawDomains)
      .filter(domain => supported.has(domain));
  };

  const enqueueNavigation = navigation => {
    const operation = invocationTail.then(async () => {
      try {
        await ensureNavigationApprovals();
      } catch {
        return;
      }
      if (!isApprovedNavigationUrl(navigation.url, navigationDomains)) return;
      return invokeOnce('onNavigation', navigation);
    });
    invocationTail = operation.catch(() => {});
    return operation;
  };

  const navigationBindings = [
    [chromeApi.webNavigation.onBeforeNavigate, 'topNavigation'],
    [chromeApi.webNavigation.onHistoryStateUpdated, 'spaNavigation'],
    [chromeApi.webNavigation.onCommitted, 'documentReplaced'],
  ].map(([browserEvent, navigationKind]) => {
    const listener = details => {
      if (details?.frameId !== 0 || !isApprovedNavigationUrl(details.url, supportedDomains)) return;
      if (navigationApprovalsReady && !isApprovedNavigationUrl(details.url, navigationDomains)) return;
      fireAndForget(() => enqueueNavigation(projectNavigation(details, navigationKind)));
    };
    return { browserEvent, listener };
  });

  // MV3 wake-up listeners must remain registered synchronously for the fixed supported
  // catalog. User approval is rechecked immediately and again when the queued event runs.
  if (supportedDomains.length) {
    const filter = navigationUrlFilter(supportedDomains);
    for (const { browserEvent, listener } of navigationBindings) browserEvent.addListener(listener, filter);
  }

  chromeApi.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message?.type === 'nightGateSitePermissionsChanged') {
      enqueuePermissionRevalidation(true)
        .then(() => sendResponse({ ok: true }), () => sendResponse({ ok: false }));
      return true;
    }
    invoke('onContentMessage', message, sender)
      .then(sendResponse, () => sendResponse({ decision: 'allow' }));
    return true;
  });
  chromeApi.permissions?.onRemoved?.addListener(() => {
    failOpenEpoch += 1;
    const failOpenFirst = Promise.all([
      clearProtectionBestEffort(),
      publishDegradedBestEffort(),
    ]);
    fireAndForget(() => enqueuePermissionRevalidation(false, failOpenFirst));
  });
  chromeApi.storage.onChanged?.addListener((changes, areaName) => {
    if (areaName === 'local' && Object.hasOwn(changes ?? {}, 'approvedDomains')) {
      navigationApprovalEpoch += 1;
      installApprovedNavigationDomains(changes.approvedDomains?.newValue);
      navigationApprovalsReady = true;
      navigationApprovalFailed = false;
      navigationApprovalReadiness = Promise.resolve();
    }
  });
  chromeApi.tabs.onRemoved.addListener(tabId => {
    if (Number.isInteger(tabId) && tabId >= 0) {
      fireAndForget(() => invoke('onNavigation', { tabId, navigationKind: 'tabRemoved' }));
    }
  });
  if (typeof chromeApi.action?.onClicked?.addListener === 'function'
      && typeof chromeApi.runtime?.openOptionsPage === 'function') {
    chromeApi.action.onClicked.addListener(() => {
      fireAndForget(() => chromeApi.runtime.openOptionsPage());
    });
  }
  chromeApi.alarms.onAlarm.addListener(alarm => {
    const kind = ALARM_KIND[alarm.name];
    if (kind) {
      let failOpenFirst = Promise.resolve();
      if (kind === 'policyExpiry') {
        failOpenEpoch += 1;
        failOpenFirst = Promise.all([
          clearProtectionBestEffort(),
          publishDegradedBestEffort(),
        ]);
      }
      fireAndForget(() => enqueueInvocation(failOpenFirst, 'onAlarm', { kind }));
    }
  });
  chromeApi.runtime.onStartup.addListener(() => fireAndForget(initialize));
  chromeApi.runtime.onInstalled.addListener(() => fireAndForget(initialize));

  beginNavigationApprovalLoad();

  return {
    start: initialize,
  };
}

function matchPatterns(domain) {
  return [
    `http://${domain}/*`,
    `https://${domain}/*`,
    `http://*.${domain}/*`,
    `https://*.${domain}/*`,
  ];
}

export function createChromeEffects(chromeApi, options = {}) {
  const monotonicEpochClock = options.monotonicEpochClock
    ?? (() => performance.timeOrigin + performance.now());
  if (typeof monotonicEpochClock !== 'function') throw new TypeError('invalid monotonic epoch clock');
  let replacementEpoch = 0;
  let replacementTail = Promise.resolve();
  let protectionArmed = true;
  let permissionGeneration = 0;
  const scheduledGrantPauses = new Map();
  const requireCurrentPermissionGeneration = generation => {
    if (!Number.isSafeInteger(generation) || generation !== permissionGeneration) {
      const error = new Error('Site permission generation changed');
      error.failOpen = true;
      throw error;
    }
  };
  const grantPauseKey = (grant, gateId) => JSON.stringify([
    grant.tabId, grant.documentId, grant.mediaToken, grant.sourceGeneration, gateId,
  ]);
  const validateLease = lease => {
    if (!lease || typeof lease !== 'object' || Array.isArray(lease)
        || Object.keys(lease).sort().join(',') !== 'leaseDeadlineMonotonicMs,leaseMs'
        || !Number.isFinite(lease.leaseMs) || lease.leaseMs <= 0 || lease.leaseMs > MAX_POLICY_LEASE_MS
        || !Number.isFinite(lease.leaseDeadlineMonotonicMs)) {
      throw new TypeError('invalid policy lease');
    }
  };
  const clampLeaseAtDispatch = lease => {
    validateLease(lease);
    const nowMs = monotonicEpochClock();
    if (!Number.isFinite(nowMs)) throw new TypeError('invalid monotonic epoch clock');
    const deadlineMs = Math.min(lease.leaseDeadlineMonotonicMs, nowMs + lease.leaseMs);
    const remainingMs = deadlineMs - nowMs;
    return remainingMs > 0
      ? { leaseMs: remainingMs, leaseDeadlineMonotonicMs: deadlineMs }
      : null;
  };
  const sendControl = async (target, message, lease = undefined) => {
    if (!Number.isInteger(target?.tabId) || target.tabId < 0
        || typeof target.documentId !== 'string' || !target.documentId) return;
    const boundedLease = lease === undefined ? null : clampLeaseAtDispatch(lease);
    if (lease !== undefined && !boundedLease) return;
    try {
      await chromeApi.tabs.sendMessage(target.tabId, {
        ...message,
        ...(boundedLease ?? {}),
      }, { documentId: target.documentId });
    } catch {
      // A replaced/closed document is already unable to continue the old grant.
    }
  };
  const replaceDnr = () => {
    const epoch = ++replacementEpoch;
    const operation = replacementTail.then(async () => {
      const existing = await chromeApi.declarativeNetRequest.getSessionRules();
      if (epoch !== replacementEpoch) return;
      const removeRuleIds = [...new Set(existing.map(rule => rule.id))].sort((a, b) => a - b);
      await chromeApi.declarativeNetRequest.updateSessionRules({ removeRuleIds, addRules: [] });
    });
    replacementTail = operation.catch(() => {});
    return operation;
  };
  return {
    async clearDnr() {
      permissionGeneration += 1;
      protectionArmed = false;
      const pending = [...scheduledGrantPauses.values()];
      scheduledGrantPauses.clear();
      await Promise.all([
        replaceDnr([]),
        ...pending.map(({ grant, gateId }) => sendControl(grant, {
          type: 'nightGateControl', command: 'cancelPause', gateId,
          mediaToken: grant.mediaToken, sourceGeneration: grant.sourceGeneration,
        })),
      ]);
    },
    armDnr(generation) {
      requireCurrentPermissionGeneration(generation);
      const wasArmed = protectionArmed;
      protectionArmed = true;
      return wasArmed;
    },
    disarmDnr() {
      permissionGeneration += 1;
      protectionArmed = false;
    },
    async loadSession() {
      const value = await chromeApi.storage.session.get(SESSION_KEY);
      return value?.[SESSION_KEY] ?? null;
    },
    async saveSession(value) {
      await chromeApi.storage.session.set({ [SESSION_KEY]: value });
    },
    async replaceDnr(addRules) {
      if (!Array.isArray(addRules)) throw new TypeError('invalid DNR replacement');
      if (addRules.length) throw new TypeError('persistent DNR restrictions are not supported');
      await replaceDnr();
    },
    async preflightSiteRules(siteRules) {
      const generation = permissionGeneration;
      const sorted = [...siteRules].sort((left, right) =>
        left.domain.localeCompare(right.domain) || left.ruleId.localeCompare(right.ruleId));
      if (!sorted.length) {
        requireCurrentPermissionGeneration(generation);
        return generation;
      }
      const settings = await chromeApi.storage.local.get('approvedDomains');
      const approved = new Set(Array.isArray(settings.approvedDomains) ? settings.approvedDomains : []);
      if (sorted.some(rule => !approved.has(rule.domain))) {
        const error = new Error('Policy site was not approved by the user');
        error.failOpen = true;
        throw error;
      }
      const permissions = await Promise.all(sorted.map(rule =>
        chromeApi.permissions.contains({ origins: matchPatterns(rule.domain) })));
      if (permissions.some(granted => !granted)) {
        const error = new Error('Optional site permission is missing');
        error.failOpen = true;
        throw error;
      }
      requireCurrentPermissionGeneration(generation);
      return generation;
    },
    async applySiteRules(siteRules, generation) {
      requireCurrentPermissionGeneration(generation);
      const current = await chromeApi.scripting.getRegisteredContentScripts();
      requireCurrentPermissionGeneration(generation);
      const ours = current.map(script => script.id).filter(id => id.startsWith(SCRIPT_PREFIX));
      if (ours.length) {
        await chromeApi.scripting.unregisterContentScripts({ ids: ours });
        requireCurrentPermissionGeneration(generation);
      }
      const sorted = [...siteRules].sort((left, right) =>
        left.domain.localeCompare(right.domain) || left.ruleId.localeCompare(right.ruleId));
      if (!sorted.length) return;
      await chromeApi.scripting.registerContentScripts(sorted.map((rule, index) => ({
        id: `${SCRIPT_PREFIX}${String(index).padStart(3, '0')}`,
        js: ['lib/content-observer.js', 'content-script.js'],
        matches: matchPatterns(rule.domain),
        runAt: 'document_start',
        allFrames: false,
        persistAcrossSessions: false,
      })));
      requireCurrentPermissionGeneration(generation);
      const matches = sorted.flatMap(rule => matchPatterns(rule.domain));
      const openTabs = await chromeApi.tabs.query({ url: matches });
      requireCurrentPermissionGeneration(generation);
      const tabIds = [...new Set(openTabs
        .map(tab => tab?.id)
        .filter(tabId => Number.isInteger(tabId) && tabId >= 0))]
        .sort((left, right) => left - right);
      const injections = await Promise.allSettled(tabIds.map(tabId =>
        chromeApi.scripting.executeScript({
          target: { tabId, frameIds: [0] },
          files: ['lib/content-observer.js', 'content-script.js'],
        })));
      const failedTabIds = injections
        .map((result, index) => result.status === 'rejected' ? tabIds[index] : null)
        .filter(tabId => tabId !== null);
      if (failedTabIds.length) {
        requireCurrentPermissionGeneration(generation);
        const stillProtectedTabs = await chromeApi.tabs.query({ url: matches });
        requireCurrentPermissionGeneration(generation);
        const stillProtectedIds = new Set(stillProtectedTabs
          .map(tab => tab?.id)
          .filter(tabId => Number.isInteger(tabId) && tabId >= 0));
        const persistentFailureIndex = injections.findIndex((result, index) =>
          result.status === 'rejected' && stillProtectedIds.has(tabIds[index]));
        if (persistentFailureIndex >= 0) {
          throw injections[persistentFailureIndex].reason;
        }
      }
      requireCurrentPermissionGeneration(generation);
    },
    async setStatus(text) {
      await setVisibleStatus(chromeApi, text);
    },
    async schedulePolicyWakeups(value) {
      const delays = [value?.expiryDelayMs, value?.lastStartDelayMs, value?.lockDelayMs];
      if (delays.some(delay => delay !== null && (!Number.isFinite(delay) || delay < 0))) {
        throw new TypeError('invalid policy wakeup');
      }
      for (const name of ONE_SHOT_ALARMS) await chromeApi.alarms.clear(name);
      for (const [index, name] of ONE_SHOT_ALARMS.entries()) {
        const delayMs = delays[index];
        if (delayMs === null) continue;
        await chromeApi.alarms.create(name, { delayInMinutes: Math.max(0.5, delayMs / 60_000) });
      }
    },
    async clearPolicyWakeups() {
      for (const name of ONE_SHOT_ALARMS) await chromeApi.alarms.clear(name);
    },
    async isIncognitoAllowed() {
      return chromeApi.extension.isAllowedIncognitoAccess();
    },
    async scheduleGrantPause(grant, gateId, delayMs, lease) {
      if (!protectionArmed) return;
      validateLease(lease);
      scheduledGrantPauses.set(grantPauseKey(grant, gateId), {
        grant: structuredClone(grant), gateId,
      });
      await sendControl(grant, {
        type: 'nightGateControl',
        command: 'pauseAt',
        gateId,
        mediaToken: grant.mediaToken,
        sourceGeneration: grant.sourceGeneration,
        delayMs,
      }, lease);
    },
    async cancelGrantPause(grant, gateId) {
      scheduledGrantPauses.delete(grantPauseKey(grant, gateId));
      await sendControl(grant, {
        type: 'nightGateControl', command: 'cancelPause', gateId,
        mediaToken: grant.mediaToken, sourceGeneration: grant.sourceGeneration,
      });
    },
    async pauseMediaTargets(targets, gateId, lease) {
      if (!protectionArmed) return;
      validateLease(lease);
      await Promise.all(targets.map(target => sendControl(target, {
        type: 'nightGateControl',
        command: 'pauseAt',
        gateId,
        mediaToken: target.mediaToken,
        sourceGeneration: target.sourceGeneration,
        delayMs: 0,
      }, lease)));
    },
    async showLocalPage(target, page, lease) {
      if (page !== 'blocked' && page !== 'finished') throw new TypeError('invalid local page');
      if (!protectionArmed) return;
      validateLease(lease);
      await sendControl(target, { type: 'nightGateControl', command: 'showLocalPage', page }, lease);
    },
  };
}
