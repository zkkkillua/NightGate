import { normalizeDomain } from './domain.mjs';
import { buildPrivacyEvent } from './privacy.mjs';

export const MAX_NATIVE_MESSAGE_BYTES = 65_536;
const REQUEST_TYPES = new Set(['getPolicy', 'heartbeat', 'mediaState', 'navigationAttempt']);
const RESPONSE_TYPE = Object.freeze({
  getPolicy: 'getPolicyResult',
  heartbeat: 'heartbeatResult',
  mediaState: 'mediaStateResult',
  navigationAttempt: 'navigationAttemptResult',
});
const MODES = new Set(['unrestricted', 'grandfatherOneMedia', 'blocked', 'fullOverride', 'failOpen']);
const OVERRIDES = new Set(['teamRescue', 'emergency', 'entertainment']);
const CATEGORIES = new Set(['gaming', 'video', 'social', 'other']);
const PRINTABLE_ID = /^[\x20-\x7e]{1,64}$/;
const PROFILE_TOKEN = /^[A-Za-z0-9_-]{43}$/;
const UTC_DATE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const UTF8 = new TextEncoder();

export function isExtensionVersion(value) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 32) return false;
  const segments = value.split('.');
  return segments.length >= 1 && segments.length <= 4
    && segments.every(segment => /^[0-9]+$/u.test(segment) && Number(segment) <= 65_535);
}

function fail(message) {
  throw new TypeError(message);
}

function exactKeys(value, required, optional = []) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail('expected object');
  const keys = Object.keys(value);
  const allowed = new Set([...required, ...optional]);
  if (keys.some(key => !allowed.has(key)) || required.some(key => !Object.hasOwn(value, key))) {
    fail('object fields do not match schema');
  }
}

function validId(value, label = 'identifier') {
  if (typeof value !== 'string' || !PRINTABLE_ID.test(value)) fail(`invalid ${label}`);
  return value;
}

function validUtcDate(value) {
  if (typeof value !== 'string' || !UTC_DATE.test(value)) fail('invalid UTC date');
  const milliseconds = Date.parse(value);
  if (!Number.isFinite(milliseconds)) fail('invalid UTC date');
  const canonical = new Date(milliseconds).toISOString();
  const supplied = value.includes('.') ? value : value.replace('Z', '.000Z');
  if (canonical !== supplied) fail('invalid UTC date');
  return value;
}

function deepFreeze(value) {
  if (value && typeof value === 'object' && !Object.isFrozen(value)) {
    for (const child of Object.values(value)) deepFreeze(child);
    Object.freeze(value);
  }
  return value;
}

function validateRequestPayload(type, payload) {
  if (type === 'getPolicy') {
    exactKeys(payload, []);
    return;
  }
  if (type === 'heartbeat') {
    exactKeys(payload, ['revision', 'extensionVersion', 'incognitoAllowed', 'protectionReady']);
    if (!Number.isSafeInteger(payload.revision) || payload.revision < -1) fail('invalid heartbeat revision');
    if (!isExtensionVersion(payload.extensionVersion)) fail('invalid extension version');
    if (typeof payload.incognitoAllowed !== 'boolean') fail('invalid incognito status');
    if (typeof payload.protectionReady !== 'boolean') fail('invalid protection readiness');
    return;
  }
  exactKeys(payload, ['timestamp', 'eventType', 'ruleId', 'category']);
  const event = buildPrivacyEvent(payload);
  if (type === 'navigationAttempt' && event.eventType !== 'navigationBlocked') fail('invalid navigation event');
  if (type === 'mediaState' && !['mediaPlaying', 'mediaPaused', 'mediaEnded'].includes(event.eventType)) {
    fail('invalid media event');
  }
}

function parseJsonWithoutDuplicateFields(text) {
  if (typeof text !== 'string' || UTF8.encode(text).byteLength > MAX_NATIVE_MESSAGE_BYTES) {
    fail('invalid native message size');
  }
  let index = 0;
  const whitespace = () => { while (/\s/u.test(text[index] ?? '')) index += 1; };
  const stringToken = () => {
    if (text[index] !== '"') fail('invalid JSON string');
    const start = index++;
    while (index < text.length) {
      if (text[index] === '\\') index += 2;
      else if (text[index++] === '"') return JSON.parse(text.slice(start, index));
    }
    fail('unterminated JSON string');
  };
  const value = () => {
    whitespace();
    if (text[index] === '{') return object();
    if (text[index] === '[') return array();
    if (text[index] === '"') { stringToken(); return; }
    const start = index;
    while (index < text.length && !/[\s,}\]]/u.test(text[index])) index += 1;
    if (start === index) fail('invalid JSON value');
  };
  const object = () => {
    index += 1;
    whitespace();
    const keys = new Set();
    if (text[index] === '}') { index += 1; return; }
    while (index < text.length) {
      whitespace();
      const key = stringToken();
      if (keys.has(key)) fail('duplicate JSON field');
      keys.add(key);
      whitespace();
      if (text[index++] !== ':') fail('invalid JSON object');
      value();
      whitespace();
      if (text[index] === '}') { index += 1; return; }
      if (text[index++] !== ',') fail('invalid JSON object');
    }
    fail('unterminated JSON object');
  };
  const array = () => {
    index += 1;
    whitespace();
    if (text[index] === ']') { index += 1; return; }
    while (index < text.length) {
      value();
      whitespace();
      if (text[index] === ']') { index += 1; return; }
      if (text[index++] !== ',') fail('invalid JSON array');
    }
    fail('unterminated JSON array');
  };

  value();
  whitespace();
  if (index !== text.length) fail('trailing JSON content');
  try {
    return JSON.parse(text);
  } catch {
    fail('invalid JSON');
  }
}

export function parsePolicy(value, { minimumRevision = -1 } = {}) {
  exactKeys(value,
    ['revision', 'gateId', 'evaluatedAtUtc', 'lastStartAtUtc', 'ttlMs', 'mode', 'lockAtUtc', 'wakeAtUtc', 'siteRules'],
    ['overrideKind']);
  if (!Number.isSafeInteger(value.revision) || value.revision < 0 || value.revision < minimumRevision) fail('invalid or stale policy revision');
  validId(value.gateId, 'gate ID');
  validUtcDate(value.evaluatedAtUtc);
  validUtcDate(value.lastStartAtUtc);
  validUtcDate(value.lockAtUtc);
  validUtcDate(value.wakeAtUtc);
  const lastStartAtMs = Date.parse(value.lastStartAtUtc);
  const lockAtMs = Date.parse(value.lockAtUtc);
  const wakeAtMs = Date.parse(value.wakeAtUtc);
  if (lastStartAtMs >= lockAtMs || lockAtMs >= wakeAtMs) fail('invalid policy schedule order');
  if (!Number.isInteger(value.ttlMs) || value.ttlMs < 1 || value.ttlMs > 120_000) fail('invalid TTL');
  if (typeof value.mode !== 'string' || !MODES.has(value.mode)) fail('invalid policy mode');
  if (value.overrideKind !== undefined && value.overrideKind !== null
      && (typeof value.overrideKind !== 'string' || !OVERRIDES.has(value.overrideKind))) fail('invalid override kind');
  if (!Array.isArray(value.siteRules) || value.siteRules.length > 100) fail('invalid site rules');

  const ids = new Set();
  const domains = new Set();
  const siteRules = value.siteRules.map(rule => {
    exactKeys(rule, ['ruleId', 'category', 'domain']);
    validId(rule.ruleId, 'rule ID');
    if (ids.has(rule.ruleId)) fail('duplicate rule ID');
    ids.add(rule.ruleId);
    if (typeof rule.category !== 'string' || !CATEGORIES.has(rule.category)) fail('invalid category');
    const domain = normalizeDomain(rule.domain);
    if (domains.has(domain)) fail('duplicate site domain');
    domains.add(domain);
    return { ruleId: rule.ruleId, category: rule.category, domain };
  });

  return deepFreeze({
    revision: value.revision,
    gateId: value.gateId,
    evaluatedAtUtc: value.evaluatedAtUtc,
    lastStartAtUtc: value.lastStartAtUtc,
    ttlMs: value.ttlMs,
    mode: value.mode,
    lockAtUtc: value.lockAtUtc,
    wakeAtUtc: value.wakeAtUtc,
    overrideKind: value.overrideKind ?? null,
    siteRules,
  });
}

export function encodeNativeRequest(value) {
  exactKeys(value, ['type', 'requestId', 'profileToken', 'payload']);
  if (typeof value.type !== 'string' || !REQUEST_TYPES.has(value.type)) fail('invalid request type');
  validId(value.requestId, 'request ID');
  if (typeof value.profileToken !== 'string' || !PROFILE_TOKEN.test(value.profileToken)) fail('invalid profile token');
  if (!value.payload || typeof value.payload !== 'object' || Array.isArray(value.payload)) fail('invalid payload');
  validateRequestPayload(value.type, value.payload);
  const encoded = JSON.stringify({
    version: 1,
    type: value.type,
    requestId: value.requestId,
    profileToken: value.profileToken,
    payload: value.payload,
  });
  if (UTF8.encode(encoded).byteLength > MAX_NATIVE_MESSAGE_BYTES) fail('native message too large');
  return encoded;
}

export function decodeNativeResponse(text, expected) {
  exactKeys(expected, ['requestType', 'requestId', 'profileToken']);
  if (!REQUEST_TYPES.has(expected.requestType)) fail('invalid expected request type');
  const envelope = parseJsonWithoutDuplicateFields(text);
  exactKeys(envelope, ['version', 'type', 'requestId', 'profileToken', 'payload']);
  if (envelope.version !== 1 || envelope.type !== RESPONSE_TYPE[expected.requestType]) fail('wrong response version or type');
  if (envelope.requestId !== expected.requestId || envelope.profileToken !== expected.profileToken) fail('wrong response correlation');
  validId(envelope.requestId, 'request ID');
  if (!PROFILE_TOKEN.test(envelope.profileToken)) fail('invalid profile token');
  if (expected.requestType === 'getPolicy') envelope.payload = parsePolicy(envelope.payload);
  else {
    exactKeys(envelope.payload, ['accepted']);
    if (typeof envelope.payload.accepted !== 'boolean') fail('invalid acknowledgement');
  }
  return deepFreeze(envelope);
}
