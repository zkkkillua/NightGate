const ASCII_LABEL = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
const IPV4_SHAPE = /^\d+(?:\.\d+){3}$/;

function fail(message) {
  throw new TypeError(message);
}

function rejectIpLiteral(host) {
  if (IPV4_SHAPE.test(host) || host.includes(':')) fail('IP literals are not site rules');
}

export function normalizeDomain(value) {
  if (typeof value !== 'string' || value.length === 0 || value.trim() !== value) {
    fail('domain must be a non-empty unpadded string');
  }
  if (/[\/@:*?#[\]\\]/u.test(value)) fail('domain rule contains forbidden syntax');

  const withoutDot = value.endsWith('.') ? value.slice(0, -1) : value;
  if (!withoutDot || withoutDot.endsWith('.') || withoutDot.startsWith('.')) fail('invalid dot placement');

  let host;
  try {
    host = new URL(`http://${withoutDot}/`).hostname.toLowerCase();
  } catch {
    fail('invalid domain');
  }
  rejectIpLiteral(host);
  if (host.length > 253 || host !== new URL(`http://${host}/`).hostname.toLowerCase()) fail('invalid domain');

  const labels = host.split('.');
  if (labels.some(label => !ASCII_LABEL.test(label))) fail('invalid domain label');
  return host;
}

export function normalizeUrlHost(value) {
  if (typeof value !== 'string') fail('URL must be a string');
  let url;
  try {
    url = new URL(value);
  } catch {
    fail('invalid URL');
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') fail('unsupported URL protocol');
  return normalizeDomain(url.hostname);
}

export function domainMatchesHost(host, normalizedDomain) {
  const normalizedHost = normalizeDomain(host);
  const domain = normalizeDomain(normalizedDomain);
  return normalizedHost === domain || normalizedHost.endsWith(`.${domain}`);
}
