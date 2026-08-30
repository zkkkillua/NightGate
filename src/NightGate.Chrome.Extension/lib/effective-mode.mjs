export function effectivePolicyMode(policy) {
  if (!policy) return 'failOpen';
  if (policy.overrideKind === 'teamRescue') return 'blocked';
  if (policy.mode === 'fullOverride') return 'fullOverride';
  if (policy.overrideKind === 'entertainment') return 'blocked';
  return policy.mode;
}
