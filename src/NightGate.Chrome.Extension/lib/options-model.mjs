import { normalizeDomain } from './domain.mjs';

export const SITE_CATALOG = Object.freeze([
  Object.freeze({ domain: 'bilibili.com', label: '哔哩哔哩' }),
  Object.freeze({ domain: 'iqiyi.com', label: '爱奇艺' }),
  Object.freeze({ domain: 'netflix.com', label: 'Netflix' }),
  Object.freeze({ domain: 'v.qq.com', label: '腾讯视频' }),
  Object.freeze({ domain: 'youtube.com', label: 'YouTube' }),
]);

const CATALOG_DOMAINS = new Set(SITE_CATALOG.map(site => site.domain));

export function normalizeOptionsSelection(values) {
  if (!Array.isArray(values) || values.length > SITE_CATALOG.length) throw new TypeError('invalid site selection');
  const normalized = values.map(normalizeDomain);
  if (normalized.some(domain => !CATALOG_DOMAINS.has(domain))) throw new TypeError('site is not available in this build');
  return [...new Set(normalized)].sort();
}

export function permissionOrigins(values) {
  return normalizeOptionsSelection(values).flatMap(domain => [
    `http://${domain}/*`,
    `https://${domain}/*`,
    `http://*.${domain}/*`,
    `https://*.${domain}/*`,
  ]);
}
