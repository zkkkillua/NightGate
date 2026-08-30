import { effectivePolicyMode } from './effective-mode.mjs';

const RULE_ID_BASE = 200_000;

export function planSessionRules(policy, grant) {
  if (!policy || !Array.isArray(policy.siteRules)) throw new TypeError('invalid policy');
  const mode = effectivePolicyMode(policy);
  if (mode === 'unrestricted' || mode === 'fullOverride' || mode === 'failOpen') return [];
  const sorted = [...policy.siteRules].sort((left, right) =>
    left.ruleId.localeCompare(right.ruleId) || left.domain.localeCompare(right.domain));
  if (sorted.length > 100) throw new TypeError('too many site rules');

  return sorted.map((site, index) => {
    const condition = {
      urlFilter: `||${site.domain}^`,
      resourceTypes: ['main_frame'],
    };
    return {
      id: RULE_ID_BASE + index,
      priority: 1,
      action: { type: 'redirect', redirect: { extensionPath: '/blocked.html' } },
      condition,
    };
  });
}

export function planDnrUpdate(existingRuleIds, policy, grant) {
  if (!Array.isArray(existingRuleIds) || existingRuleIds.some(id => !Number.isInteger(id))) {
    throw new TypeError('invalid existing DNR IDs');
  }
  return {
    removeRuleIds: [...new Set(existingRuleIds)].sort((left, right) => left - right),
    addRules: planSessionRules(policy, grant),
  };
}
