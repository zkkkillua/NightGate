namespace NightGate.Core;

public sealed record IPhoneStepConfirmation(
    bool HealthSleepScheduleConfigured,
    bool SleepFocusConfigured,
    bool DowntimeConfigured,
    bool BlockAtDowntimeEnabled,
    bool RequiredAppsAllowed,
    bool SafariNotAllowlisted,
    bool DistinctRecoverableScreenTimePasscodeAcknowledged,
    bool OldAlarmsChecked,
    bool PhonePlacementPlanned,
    bool EntertainmentCategoriesRestricted = false)
{
    public bool IsComplete =>
        HealthSleepScheduleConfigured
        && SleepFocusConfigured
        && DowntimeConfigured
        && BlockAtDowntimeEnabled
        && EntertainmentCategoriesRestricted
        && RequiredAppsAllowed
        && SafariNotAllowlisted
        && DistinctRecoverableScreenTimePasscodeAcknowledged
        && OldAlarmsChecked
        && PhonePlacementPlanned;
}
