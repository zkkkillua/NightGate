import test from 'node:test';
import assert from 'node:assert/strict';

import { planDnrUpdate, planSessionRules } from '../../src/NightGate.Chrome.Extension/lib/dnr-planner.mjs';

const rules = [
  { ruleId: 'z-video', category: 'video', domain: 'video.example' },
  { ruleId: 'a-social', category: 'social', domain: 'social.example' },
];
const policy = (mode, overrides = {}) => ({ mode, siteRules: rules, overrideKind: null, ...overrides });

test('unrestricted, fullOverride, and failOpen modes clear all DNR blocking state', () => {
  for (const mode of ['unrestricted', 'fullOverride', 'failOpen']) {
    assert.deepEqual(planSessionRules(policy(mode), null), []);
  }
});

test('blocked mode generates deterministic collision-free main-frame redirects only', () => {
  const first = planSessionRules(policy('blocked'), null);
  const second = planSessionRules(policy('blocked', { siteRules: [...rules].reverse() }), null);
  assert.deepEqual(first, second);
  assert.equal(new Set(first.map(rule => rule.id)).size, rules.length);
  for (const rule of first) {
    assert.deepEqual(rule.condition.resourceTypes, ['main_frame']);
    assert.equal(rule.action.type, 'redirect');
    assert.deepEqual(rule.action.redirect, { extensionPath: '/blocked.html' });
    assert.equal('excludedTabIds' in rule.condition, false);
    assert.equal(JSON.stringify(rule).includes('xmlhttprequest'), false);
    assert.equal(JSON.stringify(rule).includes('media'), false);
  }
});

test('grandfather mode keeps main-frame redirects on every tab while current subresources remain untouched', () => {
  const grant = { tabId: 8, key: '8|doc|media|0|r' };
  const granted = planSessionRules(policy('grandfatherOneMedia'), grant);
  assert.ok(granted.every(rule => !('excludedTabIds' in rule.condition)));
  assert.ok(granted.every(rule => JSON.stringify(rule.condition.resourceTypes) === '["main_frame"]'));
});

test('TeamRescue projection is blocked even if service mode says grandfather', () => {
  const planned = planSessionRules(policy('grandfatherOneMedia', { overrideKind: 'teamRescue' }), { tabId: 8 });
  assert.ok(planned.length > 0);
  assert.ok(planned.every(rule => !('excludedTabIds' in rule.condition)));
});

test('TeamRescue keeps DNR blocking even when the service mode says fullOverride', () => {
  const planned = planSessionRules(policy('fullOverride', { overrideKind: 'teamRescue' }), null);
  assert.equal(planned.length, rules.length);
  assert.ok(planned.every(rule => rule.action.type === 'redirect'));
});

test('Entertainment cooling-off projection blocks until mode becomes fullOverride', () => {
  const cooling = planSessionRules(policy('grandfatherOneMedia', { overrideKind: 'entertainment' }), { tabId: 8 });
  assert.equal(cooling.length, rules.length);
  const active = planSessionRules(policy('fullOverride', { overrideKind: 'entertainment' }), null);
  assert.deepEqual(active, []);
});

test('planDnrUpdate atomically removes every prior rule and adds the new plan', () => {
  const update = planDnrUpdate([991, 44, 991], policy('blocked'), null);
  assert.deepEqual(update.removeRuleIds, [44, 991]);
  assert.deepEqual(update.addRules, planSessionRules(policy('blocked'), null));

  const cleanup = planDnrUpdate(update.addRules.map(rule => rule.id), policy('failOpen'), null);
  assert.deepEqual(cleanup.addRules, []);
  assert.deepEqual(cleanup.removeRuleIds, update.addRules.map(rule => rule.id).sort((a, b) => a - b));
});
