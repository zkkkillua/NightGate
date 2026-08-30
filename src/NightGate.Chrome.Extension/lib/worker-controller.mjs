import { isExtensionVersion, parsePolicy } from './codec.mjs';
import { domainMatchesHost, normalizeUrlHost } from './domain.mjs';
import { effectivePolicyMode } from './effective-mode.mjs';
import {
  createHeartbeatState,
  heartbeatReducer,
  protectionExpiresAtMs,
  projectHeartbeatStorage,
  restoreHeartbeatStorage,
} from './heartbeat.mjs';
import {
  classifyPolicyEvaluation,
  createMediaState,
  mediaDecision,
  mediaReducer,
  projectMediaStorage,
  restoreMediaStorage,
} from './media-reducer.mjs';
import { assertPrivacySafe, buildPrivacyEvent } from './privacy.mjs';

const CONTENT_KEYS = ['type', 'mediaToken', 'sourceGeneration', 'playback'];
const PLAYBACK = new Set(['playing', 'paused', 'ended']);
const TOKEN = /^[\x21-\x7e]{1,64}$/;
const DOCUMENT_ID = /^[\x20-\x7e]{1,128}$/;
const PROFILE_TOKEN = /^[A-Za-z0-9_-]{43}$/;
const EXTENSION_ID = /^[a-p]{32}$/;

function exactKeys(value, keys) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
      || Object.keys(value).length !== keys.length
      || keys.some(key => !Object.hasOwn(value, key))) {
    throw new TypeError('content message fields do not match schema');
  }
}

function findSiteRule(url, policy) {
  if (!policy || !Array.isArray(policy.siteRules)) return null;
  const host = normalizeUrlHost(url);
  return policy.siteRules.find(rule => domainMatchesHost(host, rule.domain)) ?? null;
}

function isTrustedBlockedPage(sender) {
  if (!Number.isInteger(sender?.tab?.id) || sender.tab.id < 0
      || typeof sender.documentId !== 'string' || !DOCUMENT_ID.test(sender.documentId)) return false;
  try {
    const page = new URL(sender.url);
    return page.protocol === 'chrome-extension:'
      && EXTENSION_ID.test(page.hostname)
      && page.pathname === '/blocked.html'
      && page.search === ''
      && page.hash === '';
  } catch {
    return false;
  }
}

export function normalizeContentObservation(message, sender, policy, nowMonotonicMs) {
  exactKeys(message, CONTENT_KEYS);
  if (message.type !== 'mediaObservation'
      || typeof message.mediaToken !== 'string' || !TOKEN.test(message.mediaToken)
      || !Number.isInteger(message.sourceGeneration) || message.sourceGeneration < 0 || message.sourceGeneration > 1_000_000
      || typeof message.playback !== 'string' || !PLAYBACK.has(message.playback)
      || !Number.isFinite(nowMonotonicMs)
      || !Number.isInteger(sender?.tab?.id) || sender.tab.id < 0
      || typeof sender.documentId !== 'string' || !DOCUMENT_ID.test(sender.documentId)) {
    throw new TypeError('invalid content observation');
  }
  const rule = findSiteRule(sender.url, policy);
  if (!rule) throw new TypeError('sender is not on a configured site');
  return {
    type: 'media',
    tabId: sender.tab.id,
    documentId: sender.documentId,
    mediaToken: message.mediaToken,
    sourceGeneration: message.sourceGeneration,
    ruleId: rule.ruleId,
    playback: message.playback,
    receivedMonotonicMs: nowMonotonicMs,
    category: rule.category,
  };
}

function projectWorkerStorage(state, nowMonotonicMs) {
  const value = {
    version: 1,
    policy: state.policy ? structuredClone(state.policy) : null,
    health: projectHeartbeatStorage(state.health),
    media: projectMediaStorage(state.media, { nowMonotonicMs }),
  };
  assertPrivacySafe(value);
  return value;
}

function restoreWorkerStorage(value, nowMonotonicMs) {
  if (!value || typeof value !== 'object' || value.version !== 1) {
    return { policy: null, health: createHeartbeatState(), media: createMediaState() };
  }
  try {
    const policy = value.policy ? parsePolicy(value.policy) : null;
    return {
      policy,
      health: restoreHeartbeatStorage(value.health),
      media: restoreMediaStorage(value.media, { nowMonotonicMs }),
    };
  } catch {
    return { policy: null, health: createHeartbeatState(), media: createMediaState() };
  }
}

function playbackEventType(playback) {
  return {
    playing: 'mediaPlaying',
    paused: 'mediaPaused',
    ended: 'mediaEnded',
  }[playback];
}

function responsePolicyAnchor(parsed, deliveredWallClockMs, deliveredAtMs) {
  const evaluatedAtMs = Date.parse(parsed.evaluatedAtUtc);
  if (![evaluatedAtMs, deliveredWallClockMs, deliveredAtMs].every(Number.isFinite)) {
    throw new TypeError('invalid policy response clock');
  }
  const logicalWallClockMs = Math.max(evaluatedAtMs, deliveredWallClockMs);
  const evaluationAgeMs = logicalWallClockMs - evaluatedAtMs;
  return deliveredAtMs - evaluationAgeMs;
}

export function createWorkerController({
  wallClock, monotonicClock, monotonicEpochClock, transport, effects, profileToken,
  extensionVersion,
}) {
  if (typeof wallClock !== 'function' || typeof monotonicClock !== 'function'
      || typeof monotonicEpochClock !== 'function'
      || !transport || !effects || !PROFILE_TOKEN.test(profileToken)
      || !isExtensionVersion(extensionVersion)) {
    throw new TypeError('invalid worker dependencies');
  }
  let state = { policy: null, health: createHeartbeatState(), media: createMediaState() };
  let incognitoAllowed = true;

  const statusText = () => state.health.statusText
    + (incognitoAllowed ? '' : ' · 隐身模式未受保护');

  const save = async () => {
    await effects.saveSession(projectWorkerStorage(state, monotonicClock()));
  };

  const replaceProtection = async () => {
    if (state.health.degraded || !state.policy) effects.disarmDnr?.();
    await effects.replaceDnr([]);
  };

  const setDegradedMedia = (preservePendingGrant = false) => {
    if (!state.media.policy) return;
    state = {
      ...state,
      media: {
        ...state.media,
        grant: preservePendingGrant ? state.media.grant : null,
        policy: { ...state.media.policy, mode: 'failOpen' },
      },
    };
  };

  const protectionLeaseMs = nowMonotonicMs => {
    const expiresAtMs = protectionExpiresAtMs(state.health);
    if (state.health.degraded || !Number.isFinite(nowMonotonicMs)
        || !Number.isFinite(expiresAtMs) || nowMonotonicMs >= expiresAtMs) return 0;
    return expiresAtMs - nowMonotonicMs;
  };

  const protectionLease = () => {
    const epochNowMs = monotonicEpochClock();
    const policyNowMs = monotonicClock();
    const leaseMs = protectionLeaseMs(policyNowMs);
    if (!Number.isFinite(epochNowMs) || leaseMs <= 0) return null;
    return {
      leaseMs,
      leaseDeadlineMonotonicMs: epochNowMs + leaseMs,
    };
  };

  const restrictiveDecision = async decision => {
    if (await expireStaleProtection(monotonicClock())) return { decision: 'allow' };
    const lease = protectionLease();
    if (!lease) {
      await expireStaleProtection(monotonicClock());
      return { decision: 'allow' };
    }
    return { decision, ...lease };
  };

  const requireFreshDeadline = (expiresAtMs, nowMonotonicMs = monotonicClock()) => {
    if (!Number.isFinite(nowMonotonicMs) || !Number.isFinite(expiresAtMs)
        || nowMonotonicMs >= expiresAtMs) {
      throw new TypeError('policy expired while restrictive effects were prepared');
    }
    return nowMonotonicMs;
  };

  const activeTargets = (excludedKey = null) => Object.values(state.media.candidates)
    .filter(candidate => candidate.playback === 'playing' && candidate.key !== excludedKey)
    .sort((left, right) => left.tabId - right.tabId || left.key.localeCompare(right.key))
    .slice(0, 64)
    .map(candidate => ({
      tabId: candidate.tabId,
      documentId: candidate.documentId,
      mediaToken: candidate.mediaToken,
      sourceGeneration: candidate.sourceGeneration,
    }));

  const cancelGrant = async grant => {
    if (grant) await effects.cancelGrantPause?.(grant, grant.gateId);
  };

  const updateMediaControls = async (priorGrant, enteringGrandfather = false) => {
    const mode = effectivePolicyMode(state.media.policy);
    if (priorGrant && (priorGrant.key !== state.media.grant?.key
        || priorGrant.gateId !== state.media.grant?.gateId)) {
      await cancelGrant(priorGrant);
    }
    if (state.health.degraded || mode === 'unrestricted' || mode === 'fullOverride' || mode === 'failOpen') {
      if (state.media.grant) await effects.cancelGrantPause?.(state.media.grant, state.media.grant.gateId);
      return;
    }
    let effectNowMs = monotonicClock();
    if (mode === 'blocked' || effectNowMs >= state.media.policy.localLockDeadlineMs) {
      const targets = activeTargets();
      const lease = protectionLease();
      if (targets.length && lease) {
        await effects.pauseMediaTargets?.(targets, state.media.policy.gateId, lease);
      }
      return;
    }
    if (enteringGrandfather) {
      const targets = activeTargets(state.media.grant?.key ?? null);
      const lease = protectionLease();
      if (targets.length && lease) {
        await effects.pauseMediaTargets?.(targets, state.media.policy.gateId, lease);
      }
    }
    if (state.media.grant) {
      effectNowMs = monotonicClock();
      const lease = protectionLease();
      if (!lease) return;
      const delayMs = Math.max(0, state.media.policy.localLockDeadlineMs - effectNowMs);
      await effects.scheduleGrantPause?.(state.media.grant, state.media.policy.gateId, delayMs, lease);
    }
  };

  const enterLocalLastStart = async nowMonotonicMs => {
    const priorMedia = state.media;
    const priorGrant = priorMedia.grant;
    const priorMode = effectivePolicyMode(priorMedia.policy);
    const nextMedia = mediaReducer(priorMedia, {
      type: 'lastStart', nowMonotonicMs,
    });
    if (nextMedia === priorMedia) return false;
    state = { ...state, media: nextMedia };
    const enteringGrandfather = priorMode !== 'grandfatherOneMedia'
      && effectivePolicyMode(nextMedia.policy) === 'grandfatherOneMedia';
    await updateMediaControls(priorGrant, enteringGrandfather);
    if (await expireStaleProtection(monotonicClock())) return false;
    await replaceProtection();
    if (await expireStaleProtection(monotonicClock())) return false;
    await schedulePolicyWakeups(nowMonotonicMs);
    if (await expireStaleProtection(monotonicClock())) return false;
    await save();
    if (await expireStaleProtection(monotonicClock())) return false;
    return true;
  };

  const publishStatus = async () => effects.setStatus(statusText());

  const refreshIncognitoStatus = async () => {
    const next = Boolean(await effects.isIncognitoAllowed());
    const changed = next !== incognitoAllowed;
    incognitoAllowed = next;
    return changed;
  };

  const sendHeartbeat = async () => {
    const accepted = await transport.send('heartbeat', {
      revision: state.health.revision,
      extensionVersion,
      incognitoAllowed,
      protectionReady: !state.health.degraded
        && state.policy !== null
        && state.health.revision === state.policy.revision,
    });
    if (accepted !== true) throw new TypeError('native host rejected heartbeat');
  };

  const clearPolicyWakeups = async () => {
    try {
      await effects.clearPolicyWakeups?.();
    } catch {
      // A stale alarm may wake the worker later; its handler still fails open.
    }
  };

  const schedulePolicyWakeups = async nowMonotonicMs => {
    const expiresAtMs = protectionExpiresAtMs(state.health);
    const mode = effectivePolicyMode(state.media.policy);
    const futureDelay = deadline => Number.isFinite(deadline) && deadline > nowMonotonicMs
      ? deadline - nowMonotonicMs
      : null;
    await effects.schedulePolicyWakeups?.({
      expiryDelayMs: futureDelay(expiresAtMs),
      lastStartDelayMs: mode === 'unrestricted'
        ? futureDelay(state.media.policy?.localLastStartDeadlineMs)
        : null,
      lockDelayMs: mode === 'grandfatherOneMedia'
        ? futureDelay(state.media.policy?.localLockDeadlineMs)
        : null,
    });
  };

  const acceptPolicy = async (rawPolicy, timing) => {
    const parsed = parsePolicy(rawPolicy);
    const { deliveredWallClockMs, deliveredAtMs } = timing;
    const policyAnchorMs = responsePolicyAnchor(
      parsed, deliveredWallClockMs, deliveredAtMs,
    );
    const evaluation = classifyPolicyEvaluation(state.policy, parsed);
    if (evaluation === 'repeat') {
      const freshness = heartbeatReducer(state.health, { type: 'tick', nowMs: deliveredAtMs });
      if (state.health.degraded || freshness.degraded) {
        throw new TypeError('replayed policy evaluation is no longer fresh');
      }
      return false;
    }
    const acceptedHealth = heartbeatReducer(state.health, {
      type: 'validPolicy', nowMs: policyAnchorMs, revision: parsed.revision, ttlMs: parsed.ttlMs,
    });
    const expiresAtMs = protectionExpiresAtMs(acceptedHealth);
    requireFreshDeadline(expiresAtMs, deliveredAtMs);
    const permissionLease = await effects.preflightSiteRules(parsed.siteRules);
    let effectNowMs = requireFreshDeadline(expiresAtMs);
    await effects.applySiteRules(parsed.siteRules, permissionLease);
    effectNowMs = requireFreshDeadline(expiresAtMs);
    const priorGrant = state.media.grant;
    const priorPolicy = state.media.policy;
    const priorAuthoritativePolicy = state.policy;
    state = {
      ...state,
      policy: parsed,
      health: acceptedHealth,
    };
    // A local last-start transition may intentionally differ from the last server snapshot.
    // Validate renewal identity against that authoritative snapshot, then re-project local time.
    let nextMedia = mediaReducer({
      ...state.media,
      policy: priorAuthoritativePolicy,
    }, {
      type: 'policy', policy: parsed, receivedMonotonicMs: policyAnchorMs,
    });
    nextMedia = mediaReducer(nextMedia, { type: 'lastStart', nowMonotonicMs: effectNowMs });
    state = { ...state, media: nextMedia };
    const enteringGrandfather = effectivePolicyMode(nextMedia.policy) === 'grandfatherOneMedia'
      && (effectivePolicyMode(priorPolicy) !== 'grandfatherOneMedia' || priorPolicy?.gateId !== parsed.gateId);
    effects.armDnr?.(permissionLease);
    effectNowMs = requireFreshDeadline(expiresAtMs);
    await replaceProtection();
    effectNowMs = requireFreshDeadline(expiresAtMs);
    await updateMediaControls(priorGrant, enteringGrandfather);
    effectNowMs = requireFreshDeadline(expiresAtMs);
    await schedulePolicyWakeups(effectNowMs);
    requireFreshDeadline(expiresAtMs);
    await publishStatus();
    await save();
    requireFreshDeadline(expiresAtMs);
    return true;
  };

  const recordFailure = async (nowMonotonicMs, immediate = false) => {
    state = { ...state, health: heartbeatReducer(state.health, { type: 'transportFailure', nowMs: nowMonotonicMs }) };
    if (immediate && !state.health.degraded) {
      state = { ...state, health: heartbeatReducer(state.health, { type: 'transportFailure', nowMs: nowMonotonicMs }) };
    }
    if (state.health.degraded) {
      const priorGrant = state.media.grant;
      setDegradedMedia();
      await cancelGrant(priorGrant);
      await clearPolicyWakeups();
      await replaceProtection();
      if (state.health.revision >= 0) {
        try {
          await sendHeartbeat();
        } catch {
          // Restrictive state is already cleared; this is best-effort desktop visibility.
        }
      }
    }
    await publishStatus();
    await save();
  };

  const recordPrivacyEvent = async (type, event) => {
    try {
      if (await transport.send(type, event) === true) return true;
    } catch {
      // A rejected or unavailable event sink cannot support restrictive decisions safely.
    }
    await recordFailure(monotonicClock(), true);
    return false;
  };

  const expireStaleProtection = async nowMonotonicMs => {
    if (state.health.degraded) return true;
    const priorGrant = state.media.grant;
    state = {
      ...state,
      health: heartbeatReducer(state.health, { type: 'tick', nowMs: nowMonotonicMs }),
    };
    if (!state.health.degraded) return false;
    setDegradedMedia();
    await cancelGrant(priorGrant);
    await clearPolicyWakeups();
    await replaceProtection();
    await publishStatus();
    await save();
    return true;
  };

  const abandonRestrictiveState = async error => {
    const priorGrant = state.media.grant;
    state = {
      ...state,
      health: heartbeatReducer(state.health, { type: 'expire', nowMs: monotonicClock() }),
    };
    setDegradedMedia();
    for (const cleanup of [
      () => cancelGrant(priorGrant),
      clearPolicyWakeups,
      replaceProtection,
      publishStatus,
      save,
    ]) {
      try {
        await cleanup();
      } catch {
        // Preserve the first internal failure; the listener adapter retries DNR cleanup.
      }
    }
    throw error;
  };

  const refresh = async ({ failOpenOnFailure = false } = {}) => {
    let rawPolicy;
    try {
      rawPolicy = await transport.getPolicy({ minimumRevision: state.health.revision, profileToken });
    } catch (error) {
      await recordFailure(monotonicClock(), failOpenOnFailure || error?.failOpen === true || error instanceof TypeError);
      return false;
    }
    const deliveredWallClockMs = wallClock();
    const deliveredAtMs = monotonicClock();
    try {
      await acceptPolicy(rawPolicy, { deliveredWallClockMs, deliveredAtMs });
      return true;
    } catch {
      await recordFailure(monotonicClock(), true);
      return false;
    }
  };

  const init = async () => {
    try {
      await effects.clearDnr?.();
    } catch {
      // Startup cleanup is best effort; later events retry without installing rules.
    }
    const stored = await effects.loadSession();
    const restored = restoreWorkerStorage(stored, monotonicClock());
    state = {
      ...restored,
      health: { ...createHeartbeatState(), revision: restored.health.revision },
    };
    await refreshIncognitoStatus();
    const priorGrant = state.media.grant;
    setDegradedMedia(true);
    await cancelGrant(priorGrant);
    await replaceProtection();
    await publishStatus();
    try {
      await sendHeartbeat();
    } catch (error) {
      return abandonRestrictiveState(error);
    }
    if (await refresh()) {
      try {
        await sendHeartbeat();
      } catch (error) {
        return abandonRestrictiveState(error);
      }
    }
  };

  const onContentMessage = async (message, sender) => {
    const nowMonotonicMs = monotonicClock();
    try {
      if (await expireStaleProtection(nowMonotonicMs)) return { decision: 'allow' };
      await enterLocalLastStart(nowMonotonicMs);
      if (state.health.degraded || await expireStaleProtection(monotonicClock())) {
        return { decision: 'allow' };
      }
    } catch (error) {
      return abandonRestrictiveState(error);
    }
    if (message?.type === 'blockedPageFreshness') {
      try {
        exactKeys(message, ['type']);
        if (!isTrustedBlockedPage(sender)) throw new TypeError('invalid blocked-page sender');
        if (await expireStaleProtection(monotonicClock())) return { decision: 'allow' };
        let mode = effectivePolicyMode(state.media.policy);
        if (!state.health.degraded && !['unrestricted', 'fullOverride', 'failOpen'].includes(mode)) {
          if (!await refresh({ failOpenOnFailure: true })) return { decision: 'allow' };
          if (await expireStaleProtection(monotonicClock())) return { decision: 'allow' };
          mode = effectivePolicyMode(state.media.policy);
        }
        if (state.health.degraded || ['unrestricted', 'fullOverride', 'failOpen'].includes(mode)) {
          return { decision: 'allow' };
        }
        return await restrictiveDecision('stayBlocked');
      } catch {
        return { decision: 'allow' };
      }
    }

    let action;
    try {
      action = normalizeContentObservation(message, sender, state.policy, nowMonotonicMs);
    } catch {
      return { decision: 'allow' };
    }
    try {
      const priorGrantValue = state.media.grant;
      const priorGrant = priorGrantValue?.key;
      state = { ...state, media: mediaReducer(state.media, action) };
      if (priorGrant !== state.media.grant?.key) {
        await cancelGrant(priorGrantValue);
        await replaceProtection();
      }
      const privacyEvent = buildPrivacyEvent({
        timestamp: new Date(wallClock()).toISOString(),
        eventType: playbackEventType(action.playback),
        ruleId: action.ruleId,
        category: action.category,
      });
      if (!await recordPrivacyEvent('mediaState', privacyEvent)) {
        return { decision: 'allow' };
      }
      if (await expireStaleProtection(monotonicClock())) return { decision: 'allow' };
      await save();
      const decisionAtMs = monotonicClock();
      if (await expireStaleProtection(decisionAtMs)) return { decision: 'allow' };
      const decision = state.health.degraded
        ? 'allow'
        : mediaDecision(state.media, action, decisionAtMs);
      return decision === 'allow'
        ? { decision: 'allow' }
        : await restrictiveDecision(decision);
    } catch (error) {
      return abandonRestrictiveState(error);
    }
  };

  const onNavigationCore = async details => {
    if (!details || !Number.isInteger(details.tabId)
        || (details.navigationKind !== 'tabRemoved' && details.frameId !== 0)) return;
    const nowMonotonicMs = monotonicClock();
    if (await expireStaleProtection(nowMonotonicMs)) return;
    if (details.navigationKind === 'tabRemoved') {
      const priorGrantValue = state.media.grant;
      state = {
        ...state,
        media: mediaReducer(state.media, {
          type: 'navigation', kind: 'tabRemoved', tabId: details.tabId,
          documentId: '', nowMonotonicMs,
        }),
      };
      if (priorGrantValue?.key !== state.media.grant?.key) {
        await cancelGrant(priorGrantValue);
        if (await expireStaleProtection(monotonicClock())) return;
        await replaceProtection();
        if (await expireStaleProtection(monotonicClock())) return;
      }
      await save();
      await expireStaleProtection(monotonicClock());
      return;
    }
    await enterLocalLastStart(nowMonotonicMs);
    if (await expireStaleProtection(monotonicClock())) return;
    let matchedRule = null;
    try {
      matchedRule = findSiteRule(details.url, state.policy);
    } catch {
      // Invalid and extension-local URLs are not configured entertainment sites.
    }
    const cachedMode = effectivePolicyMode(state.media.policy);
    if (matchedRule && !state.health.degraded
        && !['unrestricted', 'fullOverride', 'failOpen'].includes(cachedMode)) {
      if (!await refresh({ failOpenOnFailure: true })) return;
      if (await expireStaleProtection(monotonicClock())) return;
      try {
        matchedRule = findSiteRule(details.url, state.policy);
      } catch {
        matchedRule = null;
      }
    }
    const modeBefore = effectivePolicyMode(state.media.policy);
    const actuallyBlocked = !state.health.degraded
      && !['unrestricted', 'fullOverride', 'failOpen'].includes(modeBefore);
    const localPageTarget = actuallyBlocked && matchedRule
      && ['documentReplaced', 'spaNavigation'].includes(details.navigationKind)
      && typeof details.documentId === 'string' && DOCUMENT_ID.test(details.documentId)
      ? { tabId: details.tabId, documentId: details.documentId }
      : null;
    const priorGrant = state.media.grant?.key;
    const priorGrantValue = state.media.grant;
    const heldGrant = Boolean(details.navigationKind === 'spaNavigation' && localPageTarget
      && priorGrantValue?.tabId === localPageTarget.tabId
      && priorGrantValue.documentId === localPageTarget.documentId);
    state = {
      ...state,
      media: mediaReducer(state.media, {
        type: 'navigation', kind: details.navigationKind ?? 'topNavigation', tabId: details.tabId,
        documentId: details.documentId ?? '', nowMonotonicMs,
      }),
    };
    const grantChanged = priorGrant !== state.media.grant?.key;
    if (grantChanged) await cancelGrant(priorGrantValue);
    try {
      if (matchedRule && actuallyBlocked) {
        const event = buildPrivacyEvent({
          timestamp: new Date(wallClock()).toISOString(),
          eventType: 'navigationBlocked',
          ruleId: matchedRule.ruleId,
          category: matchedRule.category,
        });
        if (!await recordPrivacyEvent('navigationAttempt', event)) return;
      }
    } catch {
      // URL inspection is ephemeral and invalid/non-site URLs are intentionally ignored.
    }
    if (await expireStaleProtection(monotonicClock())) return;
    const currentMode = effectivePolicyMode(state.media.policy);
    const mayRestrict = actuallyBlocked && !state.health.degraded
      && !['unrestricted', 'fullOverride', 'failOpen'].includes(currentMode);
    if (grantChanged && heldGrant && mayRestrict) {
      const lease = protectionLease();
      if (lease) {
        await effects.pauseMediaTargets?.([{
          tabId: priorGrantValue.tabId,
          documentId: priorGrantValue.documentId,
          mediaToken: priorGrantValue.mediaToken,
          sourceGeneration: priorGrantValue.sourceGeneration,
        }], state.media.policy?.gateId ?? priorGrantValue.gateId, lease);
      }
      if (await expireStaleProtection(monotonicClock())) return;
    }
    if (localPageTarget && mayRestrict) {
      const lease = protectionLease();
      if (!lease) {
        await expireStaleProtection(monotonicClock());
        return;
      }
      await effects.showLocalPage?.(localPageTarget, heldGrant ? 'finished' : 'blocked', lease);
      if (await expireStaleProtection(monotonicClock())) return;
    }
    if (grantChanged) {
      await replaceProtection();
      if (await expireStaleProtection(monotonicClock())) return;
    }
    await save();
    await expireStaleProtection(monotonicClock());
  };

  const onNavigation = async details => {
    try {
      return await onNavigationCore(details);
    } catch (error) {
      return abandonRestrictiveState(error);
    }
  };

  const onAlarmCore = async (alarm = {}) => {
    const kind = alarm.kind ?? 'heartbeat';
    if (await refreshIncognitoStatus()) await publishStatus();
    const nowMonotonicMs = monotonicClock();
    if (kind === 'policyExpiry') {
      const currentExpiry = protectionExpiresAtMs(state.health);
      if (Number.isFinite(currentExpiry) && nowMonotonicMs < currentExpiry) {
        const permissionLease = await effects.preflightSiteRules(
          state.policy?.siteRules ?? [],
        );
        if (await expireStaleProtection(monotonicClock())) return;
        const wasArmed = effects.armDnr?.(permissionLease);
        if (wasArmed === false) {
          await replaceProtection();
          if (await expireStaleProtection(monotonicClock())) return;
        }
        if (await expireStaleProtection(monotonicClock())) return;
        await updateMediaControls(null);
        if (await expireStaleProtection(monotonicClock())) return;
        await schedulePolicyWakeups(monotonicClock());
        if (await expireStaleProtection(monotonicClock())) return;
        await publishStatus();
        await save();
        await expireStaleProtection(monotonicClock());
        return;
      }
      const priorGrant = state.media.grant;
      state = { ...state, health: heartbeatReducer(state.health, { type: 'expire', nowMs: nowMonotonicMs }) };
      setDegradedMedia();
      await cancelGrant(priorGrant);
      await clearPolicyWakeups();
      await replaceProtection();
      await publishStatus();
      await save();
      await refresh({ failOpenOnFailure: true });
      return;
    }
    if (kind === 'lastStart') {
      if (await expireStaleProtection(nowMonotonicMs)) {
        await refresh({ failOpenOnFailure: true });
        return;
      }
      if (effectivePolicyMode(state.policy) !== 'unrestricted') return;
      const currentLastStart = state.media.policy?.localLastStartDeadlineMs;
      if (Number.isFinite(currentLastStart) && nowMonotonicMs < currentLastStart) {
        await schedulePolicyWakeups(nowMonotonicMs);
        return;
      }
      await refresh({ failOpenOnFailure: true });
      return;
    }
    const wasDegraded = state.health.degraded;
    const priorGrant = state.media.grant;
    state = { ...state, health: heartbeatReducer(state.health, { type: 'tick', nowMs: nowMonotonicMs }) };
    state = { ...state, media: mediaReducer(state.media, { type: 'tick', nowMonotonicMs }) };
    if (!wasDegraded && state.health.degraded) {
      setDegradedMedia();
      await clearPolicyWakeups();
    }
    await updateMediaControls(priorGrant);
    if (await expireStaleProtection(monotonicClock())) {
      if (monotonicClock() >= state.health.nextAttemptAtMs) await refresh();
      return;
    }
    if (state.health.degraded || state.media.grant === null) await replaceProtection();
    if (await expireStaleProtection(monotonicClock())) {
      if (monotonicClock() >= state.health.nextAttemptAtMs) await refresh();
      return;
    }
    await publishStatus();
    await save();
    if (nowMonotonicMs >= state.health.nextAttemptAtMs) await refresh();
  };

  const onAlarm = async alarm => {
    try {
      const result = await onAlarmCore(alarm);
      if ((alarm?.kind ?? 'heartbeat') === 'heartbeat') await sendHeartbeat();
      return result;
    } catch (error) {
      return abandonRestrictiveState(error);
    }
  };

  return {
    init,
    refresh,
    onAlarm,
    onContentMessage,
    onNavigation,
    getState: () => structuredClone(state),
  };
}
