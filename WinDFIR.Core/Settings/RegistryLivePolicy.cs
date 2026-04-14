namespace WinDFIR.Core.Settings;

/// <summary>
/// Single gate for Live Registry (RegistrySearchProvider): explicit dual opt-in only.
/// Non-forensic (API-hookable); forensic work should use Offline Hive. Do not duplicate this predicate elsewhere.
/// </summary>
public static class RegistryLivePolicy
{
    /// <summary>
    /// True only when offline-only is off <em>and</em> experimental Live Registry is on.
    /// </summary>
    public static bool IsLiveRegistryEnabled(HostWitnessSettings? settings) =>
        IsLiveRegistryEnabled(settings?.Ui);

    /// <summary>
    /// True only when offline-only is off <em>and</em> experimental Live Registry is on.
    /// </summary>
    public static bool IsLiveRegistryEnabled(UiSettings? ui)
    {
        ui ??= new UiSettings();
        return ui.RegistryUseOfflineOnly != true && ui.EnableLiveRegistryExperimental == true;
    }
}
