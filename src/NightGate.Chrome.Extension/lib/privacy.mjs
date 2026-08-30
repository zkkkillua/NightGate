const EVENT_TYPES = new Set(['mediaPlaying', 'mediaPaused', 'mediaEnded', 'navigationBlocked']);
const CATEGORIES = new Set(['gaming', 'video', 'social', 'other']);
const SAFE_ID = /^[\x20-\x7e]{1,64}$/;
const UTC_DATE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;
const FORBIDDEN_KEYS = ['url', 'host', 'path', 'query', 'fragment', 'referrer', 'title', 'source', 'currentsrc', 'pagetext'];

function fail(message) {
  throw new TypeError(message);
}

export function buildPrivacyEvent(value) {
  if (!value || typeof value !== 'object') fail('invalid privacy event');
  if (typeof value.timestamp !== 'string' || !UTC_DATE.test(value.timestamp)
      || new Date(value.timestamp).toISOString() !== value.timestamp) fail('invalid event timestamp');
  if (typeof value.eventType !== 'string' || !EVENT_TYPES.has(value.eventType)) fail('invalid event type');
  if (typeof value.ruleId !== 'string' || !SAFE_ID.test(value.ruleId)) fail('invalid rule ID');
  if (typeof value.category !== 'string' || !CATEGORIES.has(value.category)) fail('invalid category');
  return Object.freeze({
    timestamp: value.timestamp,
    eventType: value.eventType,
    ruleId: value.ruleId,
    category: value.category,
  });
}

function forbiddenKey(key) {
  const normalized = key.toLowerCase();
  if (normalized === 'sourcegeneration') return false;
  return FORBIDDEN_KEYS.some(item => normalized === item || normalized.endsWith(item));
}

export function assertPrivacySafe(value, seen = new Set()) {
  if (!value || typeof value !== 'object') return value;
  if (seen.has(value)) fail('cyclic privacy payload');
  seen.add(value);
  if (Array.isArray(value)) {
    for (const item of value) assertPrivacySafe(item, seen);
  } else {
    for (const [key, child] of Object.entries(value)) {
      if (forbiddenKey(key)) fail(`forbidden privacy field: ${key}`);
      assertPrivacySafe(child, seen);
    }
  }
  seen.delete(value);
  return value;
}

export function localRedirect(page) {
  if (page !== 'blocked' && page !== 'finished') fail('unknown local redirect');
  return `/${page}.html`;
}
