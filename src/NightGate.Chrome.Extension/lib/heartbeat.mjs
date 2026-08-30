export const DEGRADATION_TEXT = '网页保护降级';
const NORMAL_TEXT = '网页保护正常';
const SILENCE_LIMIT_MS = 45_000;

export function protectionExpiresAtMs(state) {
  if (!Number.isFinite(state?.expiresAtMs) || !Number.isFinite(state?.lastValidAtMs)) return null;
  return Math.min(state.expiresAtMs, state.lastValidAtMs + SILENCE_LIMIT_MS);
}

export function nextBackoffMs(failureIndex) {
  if (!Number.isInteger(failureIndex) || failureIndex < 0) throw new TypeError('invalid failure index');
  return Math.min(30_000, 1_000 * (2 ** Math.min(failureIndex, 10)));
}

export function createHeartbeatState() {
  return {
    revision: -1,
    lastValidAtMs: null,
    expiresAtMs: null,
    failureCount: 0,
    nextAttemptAtMs: 0,
    degraded: true,
    clearProtection: true,
    statusText: DEGRADATION_TEXT,
  };
}

function degrade(state) {
  return {
    ...state,
    degraded: true,
    clearProtection: true,
    statusText: DEGRADATION_TEXT,
  };
}

function isStale(state, nowMs) {
  const deadline = protectionExpiresAtMs(state);
  return deadline === null || nowMs >= deadline;
}

export function heartbeatReducer(state, action) {
  if (!state || !action || typeof action !== 'object' || !Number.isFinite(action.nowMs)) {
    throw new TypeError('invalid heartbeat input');
  }
  switch (action.type) {
    case 'validPolicy': {
      if (!Number.isSafeInteger(action.revision) || action.revision < state.revision
          || !Number.isInteger(action.ttlMs) || action.ttlMs < 1 || action.ttlMs > 120_000) {
        throw new TypeError('invalid policy heartbeat');
      }
      return {
        revision: action.revision,
        lastValidAtMs: action.nowMs,
        expiresAtMs: action.nowMs + action.ttlMs,
        failureCount: 0,
        nextAttemptAtMs: action.nowMs,
        degraded: false,
        clearProtection: false,
        statusText: NORMAL_TEXT,
      };
    }
    case 'transportFailure': {
      const failureCount = state.failureCount + 1;
      const next = {
        ...state,
        failureCount,
        nextAttemptAtMs: action.nowMs + nextBackoffMs(failureCount - 1),
      };
      return failureCount >= 2 || isStale(state, action.nowMs) ? degrade(next) : next;
    }
    case 'tick':
      return isStale(state, action.nowMs) ? degrade(state) : state;
    case 'expire':
      return degrade({ ...state, nextAttemptAtMs: action.nowMs });
    default:
      throw new TypeError('unknown heartbeat action');
  }
}

export function projectHeartbeatStorage(state) {
  return structuredClone(state);
}

export function restoreHeartbeatStorage(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return createHeartbeatState();
  const required = ['revision', 'lastValidAtMs', 'expiresAtMs', 'failureCount', 'nextAttemptAtMs', 'degraded', 'clearProtection', 'statusText'];
  if (Object.keys(value).length !== required.length || required.some(key => !Object.hasOwn(value, key))) {
    return createHeartbeatState();
  }
  return structuredClone(value);
}
