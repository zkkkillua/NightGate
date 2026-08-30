import { effectivePolicyMode } from './effective-mode.mjs';

const PLAYBACK = new Set(['playing', 'paused', 'ended']);
const NAVIGATION_KINDS = new Set(['documentReplaced', 'spaNavigation', 'tabRemoved', 'topNavigation']);

function addConsumed(state, gateId) {
  if (!gateId || state.consumedGateIds.includes(gateId)) return state.consumedGateIds;
  return [...state.consumedGateIds.slice(-31), gateId];
}

function authoritativePolicyJson(policy) {
  const {
    localLastStartArmed, localLastStartDeadlineMs, localLockDeadlineMs,
    evaluatedAtUtc, ...authoritative
  } = policy;
  return JSON.stringify(authoritative);
}

export function classifyPolicyEvaluation(currentPolicy, nextPolicy) {
  if (!currentPolicy) return 'new';
  if (!Number.isSafeInteger(currentPolicy.revision)
      || typeof currentPolicy.evaluatedAtUtc !== 'string') return 'new';
  const currentEvaluatedAtMs = Date.parse(currentPolicy.evaluatedAtUtc);
  const nextEvaluatedAtMs = Date.parse(nextPolicy?.evaluatedAtUtc);
  if (!Number.isSafeInteger(nextPolicy?.revision)
      || !Number.isFinite(currentEvaluatedAtMs) || !Number.isFinite(nextEvaluatedAtMs)) {
    throw new TypeError('invalid policy evaluation');
  }
  if (nextPolicy.revision < currentPolicy.revision || nextEvaluatedAtMs < currentEvaluatedAtMs) {
    throw new TypeError('backward policy evaluation');
  }
  if (nextPolicy.revision === currentPolicy.revision) {
    if (authoritativePolicyJson(nextPolicy) !== authoritativePolicyJson(currentPolicy)) {
      throw new TypeError('conflicting policy evaluation');
    }
    return nextPolicy.evaluatedAtUtc === currentPolicy.evaluatedAtUtc
      ? 'repeat'
      : 'renewal';
  }
  return 'new';
}

function consumeGrant(state, gateId = state.policy?.gateId) {
  return { ...state, grant: null, consumedGateIds: addConsumed(state, gateId) };
}

export function createMediaState() {
  return { candidates: {}, policy: null, grant: null, consumedGateIds: [] };
}

export function mediaKey(value) {
  if (!Number.isInteger(value.tabId) || value.tabId < 0
      || typeof value.documentId !== 'string' || !value.documentId
      || typeof value.mediaToken !== 'string' || !value.mediaToken
      || !Number.isInteger(value.sourceGeneration) || value.sourceGeneration < 0
      || typeof value.ruleId !== 'string' || !value.ruleId) {
    throw new TypeError('invalid media identity');
  }
  return JSON.stringify([value.tabId, value.documentId, value.mediaToken, value.sourceGeneration, value.ruleId]);
}

function chooseCandidate(candidates, cutoffMs) {
  return Object.values(candidates)
    .filter(candidate => candidate.playback === 'playing' && candidate.lastPlayingMonotonicMs <= cutoffMs)
    .sort((left, right) =>
      right.lastPlayingMonotonicMs - left.lastPlayingMonotonicMs
      || left.tabId - right.tabId
      || left.key.localeCompare(right.key))[0] ?? null;
}

function applyMedia(state, action) {
  if (!PLAYBACK.has(action.playback) || !Number.isFinite(action.receivedMonotonicMs)) {
    throw new TypeError('invalid media event');
  }
  const key = mediaKey(action);
  const prior = state.candidates[key];
  const candidates = Object.fromEntries(Object.entries(state.candidates).filter(([, existing]) =>
    existing.tabId !== action.tabId
    || existing.documentId !== action.documentId
    || existing.mediaToken !== action.mediaToken
    || existing.sourceGeneration === action.sourceGeneration));
  const candidate = {
    key,
    tabId: action.tabId,
    documentId: action.documentId,
    mediaToken: action.mediaToken,
    sourceGeneration: action.sourceGeneration,
    ruleId: action.ruleId,
    playback: action.playback,
    lastPlayingMonotonicMs: action.playback === 'playing'
      ? action.receivedMonotonicMs
      : prior?.lastPlayingMonotonicMs ?? Number.NEGATIVE_INFINITY,
  };
  let next = { ...state, candidates: { ...candidates, [key]: candidate } };
  if (state.grant
      && state.grant.tabId === action.tabId
      && state.grant.documentId === action.documentId
      && state.grant.mediaToken === action.mediaToken
      && state.grant.sourceGeneration !== action.sourceGeneration) {
    next = consumeGrant(next);
  }
  if (action.playback === 'ended' && state.grant?.key === key) next = consumeGrant(next);
  return next;
}

function applyPolicy(state, action) {
  const nextPolicy = action.policy;
  if (!nextPolicy || !Number.isFinite(action.receivedMonotonicMs)) throw new TypeError('invalid policy action');
  if (classifyPolicyEvaluation(state.policy, nextPolicy) === 'repeat') return state;
  const evaluatedAtMs = Date.parse(nextPolicy.evaluatedAtUtc);
  const lastStartAtMs = Date.parse(nextPolicy.lastStartAtUtc);
  const lockAtMs = Date.parse(nextPolicy.lockAtUtc);
  if (![evaluatedAtMs, lastStartAtMs, lockAtMs].every(Number.isFinite)) throw new TypeError('invalid policy dates');
  const projectedPolicy = {
    ...nextPolicy,
    localLastStartDeadlineMs: action.receivedMonotonicMs + lastStartAtMs - evaluatedAtMs,
    localLockDeadlineMs: action.receivedMonotonicMs + lockAtMs - evaluatedAtMs,
  };
  projectedPolicy.localLastStartArmed = effectivePolicyMode(nextPolicy) === 'unrestricted'
    && projectedPolicy.localLastStartDeadlineMs > action.receivedMonotonicMs;
  let next = { ...state, policy: projectedPolicy };
  const mode = effectivePolicyMode(nextPolicy);
  if (mode === 'unrestricted' || mode === 'failOpen') return { ...next, grant: null };
  if (mode === 'blocked' || mode === 'fullOverride') return consumeGrant(next, nextPolicy.gateId);

  if (action.receivedMonotonicMs >= projectedPolicy.localLockDeadlineMs) return consumeGrant(next, nextPolicy.gateId);
  if (state.consumedGateIds.includes(nextPolicy.gateId)) return { ...next, grant: null };
  if (state.grant?.gateId === nextPolicy.gateId) return next;

  const candidate = chooseCandidate(state.candidates, projectedPolicy.localLastStartDeadlineMs);
  return {
    ...next,
    grant: candidate ? {
      gateId: nextPolicy.gateId,
      key: candidate.key,
      tabId: candidate.tabId,
      documentId: candidate.documentId,
      mediaToken: candidate.mediaToken,
      sourceGeneration: candidate.sourceGeneration,
      ruleId: candidate.ruleId,
    } : null,
  };
}

function applyLastStart(state, action) {
  if (!Number.isFinite(action.nowMonotonicMs)) throw new TypeError('invalid last-start action');
  const policy = state.policy;
  if (!policy?.localLastStartArmed
      || effectivePolicyMode(policy) !== 'unrestricted'
      || action.nowMonotonicMs < policy.localLastStartDeadlineMs) {
    return state;
  }

  const projectedPolicy = {
    ...policy,
    mode: 'grandfatherOneMedia',
    localLastStartArmed: false,
  };
  const next = { ...state, policy: projectedPolicy };
  if (action.nowMonotonicMs >= projectedPolicy.localLockDeadlineMs
      || state.consumedGateIds.includes(projectedPolicy.gateId)) {
    return consumeGrant(next, projectedPolicy.gateId);
  }
  const candidate = chooseCandidate(
    state.candidates,
    projectedPolicy.localLastStartDeadlineMs,
  );
  return {
    ...next,
    grant: candidate ? {
      gateId: projectedPolicy.gateId,
      key: candidate.key,
      tabId: candidate.tabId,
      documentId: candidate.documentId,
      mediaToken: candidate.mediaToken,
      sourceGeneration: candidate.sourceGeneration,
      ruleId: candidate.ruleId,
    } : null,
  };
}

function applyNavigation(state, action) {
  if (!NAVIGATION_KINDS.has(action.kind) || !Number.isInteger(action.tabId)) {
    throw new TypeError('invalid navigation event');
  }
  const candidates = Object.fromEntries(Object.entries(state.candidates)
    .filter(([, candidate]) => candidate.tabId !== action.tabId));
  const next = { ...state, candidates };
  if (state.grant?.tabId !== action.tabId) return next;
  return consumeGrant(next);
}

export function mediaReducer(state, action) {
  if (!state || !action || typeof action !== 'object') throw new TypeError('invalid reducer input');
  switch (action.type) {
    case 'media': return applyMedia(state, action);
    case 'policy': return applyPolicy(state, action);
    case 'lastStart': return applyLastStart(state, action);
    case 'navigation': return applyNavigation(state, action);
    case 'tick': {
      if (!Number.isFinite(action.nowMonotonicMs)) throw new TypeError('invalid tick');
      if (state.policy && Number.isFinite(state.policy.localLockDeadlineMs)
          && action.nowMonotonicMs >= state.policy.localLockDeadlineMs
          && !['unrestricted', 'failOpen'].includes(effectivePolicyMode(state.policy))) {
        return consumeGrant(state);
      }
      return state;
    }
    default: throw new TypeError('unknown reducer action');
  }
}

export function mediaDecision(state, media, nowMs) {
  const mode = effectivePolicyMode(state.policy);
  if (mode === 'unrestricted' || mode === 'fullOverride' || mode === 'failOpen') return 'allow';
  if (!Number.isFinite(state.policy?.localLockDeadlineMs)) return 'allow';
  if (mode !== 'grandfatherOneMedia' || nowMs >= state.policy.localLockDeadlineMs) return 'pause';
  return state.grant?.key === mediaKey(media) && !state.consumedGateIds.includes(state.policy.gateId)
    ? 'allow'
    : 'pause';
}

export function projectMediaStorage(state, options = {}) {
  const maxCandidates = options.maxCandidates ?? 32;
  const maxAgeMs = options.maxAgeMs ?? 6 * 60 * 60_000;
  const playing = Object.values(state.candidates)
    .filter(candidate => candidate.playback === 'playing' && Number.isFinite(candidate.lastPlayingMonotonicMs));
  const nowMs = options.nowMonotonicMs ?? Math.max(0, ...playing.map(candidate => candidate.lastPlayingMonotonicMs));
  if (!Number.isInteger(maxCandidates) || maxCandidates < 1 || maxCandidates > 64
      || !Number.isFinite(maxAgeMs) || maxAgeMs < 1 || !Number.isFinite(nowMs)) {
    throw new TypeError('invalid candidate storage bounds');
  }
  const latestPlayingCandidates = playing
    .filter(candidate => nowMs - candidate.lastPlayingMonotonicMs <= maxAgeMs && candidate.lastPlayingMonotonicMs <= nowMs)
    .sort((left, right) => right.lastPlayingMonotonicMs - left.lastPlayingMonotonicMs || left.key.localeCompare(right.key))
    .slice(0, maxCandidates)
    .map(candidate => ({
      key: candidate.key,
      tabId: candidate.tabId,
      documentId: candidate.documentId,
      mediaToken: candidate.mediaToken,
      sourceGeneration: candidate.sourceGeneration,
      ruleId: candidate.ruleId,
      playback: 'playing',
      lastPlayingMonotonicMs: candidate.lastPlayingMonotonicMs,
    }));
  const policy = state.policy ? {
    gateId: state.policy.gateId,
    mode: state.policy.mode,
    overrideKind: state.policy.overrideKind ?? null,
    evaluatedAtUtc: state.policy.evaluatedAtUtc,
    lastStartAtUtc: state.policy.lastStartAtUtc,
    lockAtUtc: state.policy.lockAtUtc,
  } : null;
  return structuredClone({
    capturedAtMonotonicMs: nowMs,
    policy,
    grant: state.grant,
    consumedGateIds: state.consumedGateIds.slice(-32),
    latestPlayingCandidates,
  });
}

export function restoreMediaStorage(value, options = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return createMediaState();
  const hasCurrentClock = Object.hasOwn(options, 'nowMonotonicMs');
  if (hasCurrentClock && !Number.isFinite(options.nowMonotonicMs)) {
    throw new TypeError('invalid restore clock');
  }
  const capturedAtMonotonicMs = Number.isFinite(value.capturedAtMonotonicMs)
    ? value.capturedAtMonotonicMs
    : null;
  const clockOffsetMs = hasCurrentClock && capturedAtMonotonicMs !== null
    ? options.nowMonotonicMs - capturedAtMonotonicMs
    : 0;
  const candidateList = (!hasCurrentClock || capturedAtMonotonicMs !== null)
    && Array.isArray(value.latestPlayingCandidates)
    ? value.latestPlayingCandidates.slice(0, 32)
    : [];
  const candidates = {};
  for (const candidate of candidateList) {
    try {
      const key = mediaKey(candidate);
      if (candidate.key !== key || candidate.playback !== 'playing' || !Number.isFinite(candidate.lastPlayingMonotonicMs)) continue;
      candidates[key] = {
        ...structuredClone(candidate),
        lastPlayingMonotonicMs: candidate.lastPlayingMonotonicMs + clockOffsetMs,
      };
    } catch {
      // Corrupt session entries fail open and are ignored independently.
    }
  }
  return {
    candidates,
    policy: value.policy ? structuredClone(value.policy) : null,
    grant: value.grant ? structuredClone(value.grant) : null,
    consumedGateIds: Array.isArray(value.consumedGateIds) ? [...value.consumedGateIds].slice(-32) : [],
  };
}
