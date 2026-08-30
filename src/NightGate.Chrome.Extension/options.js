import { SITE_CATALOG, normalizeOptionsSelection, permissionOrigins } from './lib/options-model.mjs';

const list = document.querySelector('#site-list');
const form = document.querySelector('#site-form');
const result = document.querySelector('#save-result');
const status = document.querySelector('#status');
const incognito = document.querySelector('#incognito');

function siteRow(site, selected) {
  const label = document.createElement('label');
  label.className = 'site-row';
  const input = document.createElement('input');
  input.type = 'checkbox';
  input.name = 'site';
  input.value = site.domain;
  input.checked = selected.includes(site.domain);
  const text = document.createElement('span');
  text.textContent = `${site.label} · ${site.domain}`;
  label.append(input, text);
  return label;
}

function storedSelection(value) {
  try {
    return normalizeOptionsSelection(Array.isArray(value) ? value : []);
  } catch {
    return [];
  }
}

async function render() {
  const stored = await chrome.storage.local.get(['approvedDomains', 'protectionStatus']);
  const selected = storedSelection(stored.approvedDomains);
  list.replaceChildren();
  for (const site of SITE_CATALOG) list.append(siteRow(site, selected));
  status.textContent = stored.protectionStatus || '网页保护已降级';
  const allowed = await chrome.extension.isAllowedIncognitoAccess();
  incognito.textContent = allowed ? '隐身窗口已由用户允许保护' : '隐身模式未受保护';
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  result.textContent = '';
  try {
    const stored = await chrome.storage.local.get('approvedDomains');
    const previous = storedSelection(stored.approvedDomains);
    const selected = normalizeOptionsSelection(
      [...form.querySelectorAll('input[name="site"]:checked')].map(input => input.value),
    );
    const granted = selected.length === 0
      || await chrome.permissions.request({ origins: permissionOrigins(selected) });
    if (!granted) {
      result.textContent = '没有获得网站权限，网页保护保持降级。';
      return;
    }
    const removed = previous.filter(domain => !selected.includes(domain));
    if (removed.length) await chrome.permissions.remove({ origins: permissionOrigins(removed) });
    await chrome.storage.local.set({ approvedDomains: selected });
    await chrome.runtime.sendMessage({ type: 'nightGateSitePermissionsChanged' });
    await render();
    result.textContent = '已保存并授权。网页保护已立即重新检查。';
  } catch {
    result.textContent = '设置没有保存，请重新选择。';
  }
});

void render();
