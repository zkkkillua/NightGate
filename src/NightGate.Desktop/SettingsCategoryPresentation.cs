namespace NightGate.Desktop;

public enum DesktopSettingsCategory
{
    Schedule,
    Rules,
    Chrome,
    IPhone,
    Privacy,
}

public sealed record DesktopSettingsCategoryPresentation(
    DesktopSettingsCategory Id,
    string Title,
    string Description);
