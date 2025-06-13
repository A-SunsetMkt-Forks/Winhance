using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Winhance.Core.Features.Common.Interfaces;
using Winhance.Core.Features.Common.Models;
using Winhance.Core.Features.Common.Enums;
using Winhance.Core.Features.Optimize.Interfaces;
using Winhance.Core.Features.Optimize.Models;
using Winhance.WPF.Features.Common.Interfaces;
using Winhance.WPF.Features.Common.Models;
using Winhance.WPF.Features.Common.ViewModels;
using Winhance.Core.Features.Common.Extensions;
using Winhance.Infrastructure.Features.Common.Registry;

namespace Winhance.WPF.Features.Optimize.ViewModels
{
    /// <summary>
    /// ViewModel for power optimizations.
    /// </summary>
    public partial class PowerOptimizationsViewModel : BaseSettingsViewModel<ApplicationSettingItem>
    {
        private readonly IPowerPlanService _powerPlanService;
        private readonly IViewModelLocator? _viewModelLocator;
        private readonly ISettingsRegistry? _settingsRegistry;

        [ObservableProperty]
        private int _powerPlanValue;

        [ObservableProperty]
        private bool _isApplyingPowerPlan;

        [ObservableProperty]
        private string _statusText = "Power settings";

        [ObservableProperty]
        private ObservableCollection<string> _powerPlanLabels = new()
        {
            "Balanced",
            "High Performance",
            "Ultimate Performance"
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="PowerOptimizationsViewModel"/> class.
        /// </summary>
        /// <param name="progressService">The task progress service.</param>
        /// <param name="registryService">The registry service.</param>
        /// <param name="logService">The log service.</param>
        /// <param name="powerPlanService">The power plan service.</param>
        /// <param name="viewModelLocator">The view model locator.</param>
        /// <param name="settingsRegistry">The settings registry.</param>
        public PowerOptimizationsViewModel(
            ITaskProgressService progressService,
            IRegistryService registryService,
            ILogService logService,
            IPowerPlanService powerPlanService,
            IViewModelLocator? viewModelLocator = null,
            ISettingsRegistry? settingsRegistry = null)
            : base(progressService, registryService, logService)
        {
            _powerPlanService = powerPlanService ?? throw new ArgumentNullException(nameof(powerPlanService));
            _viewModelLocator = viewModelLocator;
            _settingsRegistry = settingsRegistry;
        }

        /// <summary>
        /// Loads the power settings.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public override async Task LoadSettingsAsync()
        {
            try
            {
                IsLoading = true;
                _logService.Log(LogLevel.Info, "Loading power settings");

                // Initialize Power settings from PowerOptimizations.GetPowerOptimizations()
                Settings.Clear();

                // The Power Plan ComboBox is already defined in the XAML, so we don't need to add it to the Settings collection
                _logService.Log(LogLevel.Info, "Power Plan ComboBox is defined in XAML");

                // Get power optimizations from the new method
                var powerOptimizations = PowerOptimizations.GetPowerOptimizations();
                
                // Add items for each optimization setting
                foreach (var setting in powerOptimizations.Settings)
                {
                    // Skip settings that use PowerCfg commands
                    if (setting.CustomProperties != null &&
                        setting.CustomProperties.ContainsKey("PowerCfgSettings"))
                    {
                        _logService.Log(LogLevel.Info, $"Skipping PowerCfg setting: {setting.Name} - hiding from UI");
                        continue; // Skip this setting
                    }
                    
                    // Create a view model for each setting
                    var settingItem = new ApplicationSettingItem(_registryService, null, _logService)
                    {
                        Id = setting.Id,
                        Name = setting.Name,
                        Description = setting.Description,
                        IsUpdatingFromCode = true, // Set this to true to allow RefreshStatus to set the correct state
                        GroupName = setting.GroupName,
                        Dependencies = setting.Dependencies,
                        ControlType = setting.ControlType
                    };
                    
                    // Set up the registry settings
                    if (setting.RegistrySettings.Count == 1)
                    {
                        // Single registry setting
                        settingItem.RegistrySetting = setting.RegistrySettings[0];
                        _logService.Log(LogLevel.Info, $"Setting up single registry setting for {setting.Name}: {setting.RegistrySettings[0].Hive}\\{setting.RegistrySettings[0].SubKey}\\{setting.RegistrySettings[0].Name}");
                    }
                    else if (setting.RegistrySettings.Count > 1)
                    {
                        // Linked registry settings
                        settingItem.LinkedRegistrySettings = setting.CreateLinkedRegistrySettings();
                        _logService.Log(LogLevel.Info, $"Setting up linked registry settings for {setting.Name} with {setting.RegistrySettings.Count} entries and logic {setting.LinkedSettingsLogic}");
                        
                        // Log details about each registry entry for debugging
                        foreach (var regSetting in setting.RegistrySettings)
                        {
                            _logService.Log(LogLevel.Info, $"Linked registry entry: {regSetting.Hive}\\{regSetting.SubKey}\\{regSetting.Name}, IsPrimary={regSetting.IsPrimary}");
                        }
                    }
                    else
                    {
                        _logService.Log(LogLevel.Warning, $"No registry settings found for {setting.Name}");
                    }

                    // Register the setting in the settings registry if available
                    if (_settingsRegistry != null && !string.IsNullOrEmpty(settingItem.Id))
                    {
                        _settingsRegistry.RegisterSetting(settingItem);
                        _logService.Log(LogLevel.Info, $"Registered setting {settingItem.Id} in settings registry during creation");
                    }

                    Settings.Add(settingItem);
                }

                // Refresh status for all settings to populate LinkedRegistrySettingsWithValues
                foreach (var setting in Settings)
                {
                    await setting.RefreshStatus();
                }

                // Set up power plan ComboBox
                await LoadCurrentPowerPlanAsync();
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error loading power settings: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Loads the current power plan and sets the ComboBox value accordingly.
        /// </summary>
        private async Task LoadCurrentPowerPlanAsync()
        {
            // Use a cancellation token with a timeout to prevent hanging
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancellationToken = cancellationTokenSource.Token;
        
            try
            {
                _logService.Log(LogLevel.Info, "Starting to load current power plan");
                
                // Get the current active power plan GUID using the service with timeout
                var getPlanTask = _powerPlanService.GetActivePowerPlanGuidAsync();
                await Task.WhenAny(getPlanTask, Task.Delay(5000, cancellationToken));
                
                string activePlanGuid;
                if (getPlanTask.IsCompleted)
                {
                    activePlanGuid = await getPlanTask;
                    _logService.Log(LogLevel.Info, $"Active power plan GUID: {activePlanGuid}");
                }
                else
                {
                    _logService.Log(LogLevel.Warning, "GetActivePowerPlanGuidAsync timed out, defaulting to Balanced");
                    activePlanGuid = PowerOptimizations.PowerPlans.Balanced.Guid;
                    cancellationTokenSource.Cancel();
                }
        
                // Get the Ultimate Performance GUID from the service
                string ultimatePerformanceGuid;
                var field = typeof(Winhance.Infrastructure.Features.Optimize.Services.PowerPlanService)
                    .GetField("ULTIMATE_PERFORMANCE_PLAN_GUID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                
                if (field != null)
                {
                    ultimatePerformanceGuid = (string)field.GetValue(null);
                    _logService.Log(LogLevel.Info, $"Ultimate Performance GUID from service: {ultimatePerformanceGuid}");
                }
                else
                {
                    ultimatePerformanceGuid = PowerOptimizations.PowerPlans.UltimatePerformance.Guid;
                    _logService.Log(LogLevel.Warning, $"Could not get Ultimate Performance GUID from service, using value from PowerOptimizations: {ultimatePerformanceGuid}");
                }
        
                // Set the slider value based on the active plan
                if (activePlanGuid == PowerOptimizations.PowerPlans.Balanced.Guid)
                {
                    // Use IsApplyingPowerPlan to prevent triggering ApplyPowerPlanAsync
                    bool wasApplying = IsApplyingPowerPlan;
                    IsApplyingPowerPlan = true;
                    PowerPlanValue = 0; // Balanced
                    IsApplyingPowerPlan = wasApplying;
                    _logService.Log(LogLevel.Info, "Detected Balanced power plan");
                }
                else if (activePlanGuid == PowerOptimizations.PowerPlans.HighPerformance.Guid)
                {
                    bool wasApplying = IsApplyingPowerPlan;
                    IsApplyingPowerPlan = true;
                    PowerPlanValue = 1; // High Performance
                    IsApplyingPowerPlan = wasApplying;
                    _logService.Log(LogLevel.Info, "Detected High Performance power plan");
                }
                else if (activePlanGuid == ultimatePerformanceGuid)
                {
                    bool wasApplying = IsApplyingPowerPlan;
                    IsApplyingPowerPlan = true;
                    PowerPlanValue = 2; // Ultimate Performance
                    IsApplyingPowerPlan = wasApplying;
                    _logService.Log(LogLevel.Info, "Detected Ultimate Performance power plan");
                }
                else
                {
                    // Check if the active plan name contains "Ultimate Performance"
                    var getPlansTask = _powerPlanService.GetAvailablePowerPlansAsync();
                    await Task.WhenAny(getPlansTask, Task.Delay(5000, cancellationToken));
                    
                    if (getPlansTask.IsCompleted)
                    {
                        var allPlans = await getPlansTask;
                        var activePlan = allPlans.FirstOrDefault(p => p.Guid == activePlanGuid);
                        
                        if (activePlan != null && activePlan.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase))
                        {
                            bool wasApplying = IsApplyingPowerPlan;
                            IsApplyingPowerPlan = true;
                            PowerPlanValue = 2; // Ultimate Performance
                            IsApplyingPowerPlan = wasApplying;
                            _logService.Log(LogLevel.Info, $"Detected Ultimate Performance power plan by name: {activePlan.Name}");
                            
                            // Update the static GUID for future reference
                            if (field != null)
                            {
                                field.SetValue(null, activePlanGuid);
                                _logService.Log(LogLevel.Info, $"Updated Ultimate Performance GUID to: {activePlanGuid}");
                            }
                        }
                        else
                        {
                            bool wasApplying = IsApplyingPowerPlan;
                            IsApplyingPowerPlan = true;
                            PowerPlanValue = 0; // Default to Balanced if unknown
                            IsApplyingPowerPlan = wasApplying;
                            _logService.Log(LogLevel.Warning, $"Unknown power plan GUID: {activePlanGuid}, defaulting to Balanced");
                        }
                    }
                    else
                    {
                        _logService.Log(LogLevel.Warning, "GetAvailablePowerPlansAsync timed out, defaulting to Balanced");
                        bool wasApplying = IsApplyingPowerPlan;
                        IsApplyingPowerPlan = true;
                        PowerPlanValue = 0; // Default to Balanced
                        IsApplyingPowerPlan = wasApplying;
                        cancellationTokenSource.Cancel();
                    }
                }
        
                _logService.Log(LogLevel.Info, $"Current power plan: {PowerPlanLabels[PowerPlanValue]} (Value: {PowerPlanValue})");
            }
            catch (TaskCanceledException)
            {
                _logService.Log(LogLevel.Warning, "Power plan loading was canceled due to timeout");
                bool wasApplying = IsApplyingPowerPlan;
                IsApplyingPowerPlan = true;
                PowerPlanValue = 0; // Default to Balanced on timeout
                IsApplyingPowerPlan = wasApplying;
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error loading current power plan: {ex.Message}");
                _logService.Log(LogLevel.Debug, $"Exception details: {ex}");
                bool wasApplying = IsApplyingPowerPlan;
                IsApplyingPowerPlan = true;
                PowerPlanValue = 0; // Default to Balanced on error
                IsApplyingPowerPlan = wasApplying;
            }
        }

        /// <summary>
        /// Called when the PowerPlanValue property changes.
        /// </summary>
        /// <param name="value">The new value.</param>
        partial void OnPowerPlanValueChanged(int value)
        {
            // Only apply the power plan when not in the middle of applying a power plan
            // This prevents recursive calls and allows for importing without applying
            if (!IsApplyingPowerPlan)
            {
                _logService.Log(LogLevel.Info, $"PowerPlanValue changed to {value}, applying power plan");
                try
                {
                    // Use ConfigureAwait(false) to avoid deadlocks
                    ApplyPowerPlanAsync(value).ConfigureAwait(false);
                    _logService.Log(LogLevel.Info, $"Successfully initiated power plan change to {value}");
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Error, $"Error initiating power plan change: {ex.Message}");
                    _logService.Log(LogLevel.Debug, $"Exception details: {ex}");
                }
            }
            else
            {
                _logService.Log(LogLevel.Info, $"PowerPlanValue changed to {value}, but not applying because IsApplyingPowerPlan is true");
                _logService.Log(LogLevel.Debug, $"Stack trace at PowerPlanValue change: {Environment.StackTrace}");
            }
        }

        /// <summary>
        /// Applies the selected power plan.
        /// </summary>
        /// <param name="planIndex">The power plan index (0=Balanced, 1=High Performance, 2=Ultimate Performance).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [RelayCommand]
        private async Task ApplyPowerPlanAsync(int planIndex)
        {
            // Double-check to prevent recursive calls
            if (IsApplyingPowerPlan)
            {
                _logService.Log(LogLevel.Warning, $"ApplyPowerPlanAsync called while IsApplyingPowerPlan is true, ignoring");
                return;
            }
        
            // Use a cancellation token with a timeout to prevent hanging
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var cancellationToken = cancellationTokenSource.Token;
        
            try
            {
                IsApplyingPowerPlan = true;
                StatusText = $"Applying {PowerPlanLabels[planIndex]} power plan...";
        
                // Get the appropriate GUID based on the selected plan
                string planGuid;
                
                // Special handling for Ultimate Performance plan
                if (planIndex == 2) // Ultimate Performance
                {
                    // Get the GUID from the service directly
                    // This ensures we're using the most up-to-date GUID that might have been created dynamically
                    var field = typeof(Winhance.Infrastructure.Features.Optimize.Services.PowerPlanService)
                        .GetField("ULTIMATE_PERFORMANCE_PLAN_GUID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    
                    if (field != null)
                    {
                        planGuid = (string)field.GetValue(null);
                        _logService.Log(LogLevel.Info, $"Using Ultimate Performance GUID from service: {planGuid}");
                    }
                    else
                    {
                        // Fallback to the value from PowerOptimizations
                        planGuid = PowerOptimizations.PowerPlans.UltimatePerformance.Guid;
                        _logService.Log(LogLevel.Warning, $"Could not get GUID from service, using value from PowerOptimizations: {planGuid}");
                    }
                }
                else
                {
                    // For other plans, use the standard GUIDs
                    planGuid = planIndex switch
                    {
                        0 => PowerOptimizations.PowerPlans.Balanced.Guid,
                        1 => PowerOptimizations.PowerPlans.HighPerformance.Guid,
                        _ => PowerOptimizations.PowerPlans.Balanced.Guid // Default to Balanced
                    };
                }
        
                _logService.Log(LogLevel.Info, $"Applying power plan: {PowerPlanLabels[planIndex]} with GUID: {planGuid}");
        
                // Use the service to set the power plan with timeout
                var setPlanTask = _powerPlanService.SetPowerPlanAsync(planGuid);
                var timeoutTask = Task.Delay(10000, cancellationToken); // 10 second timeout
                
                await Task.WhenAny(setPlanTask, timeoutTask);
                
                if (setPlanTask.IsCompleted)
                {
                    bool success = await setPlanTask;
                    
                    if (success)
                    {
                        _logService.Log(LogLevel.Info, $"Power plan set to {PowerPlanLabels[planIndex]} ({planGuid})");
                        StatusText = $"{PowerPlanLabels[planIndex]} power plan applied";
                        
                        // Refresh the current power plan to ensure the UI is updated correctly
                        var loadTask = LoadCurrentPowerPlanAsync();
                        await Task.WhenAny(loadTask, Task.Delay(5000, cancellationToken));
                        
                        if (!loadTask.IsCompleted)
                        {
                            _logService.Log(LogLevel.Warning, "LoadCurrentPowerPlanAsync timed out, continuing anyway");
                        }
                        
                        // Refresh all PowerCfg settings to reflect the new power plan
                        _logService.Log(LogLevel.Info, "Refreshing PowerCfg settings after power plan change");
                        var checkTask = CheckSettingStatusesAsync();
                        await Task.WhenAny(checkTask, Task.Delay(5000, cancellationToken));
                        
                        if (!checkTask.IsCompleted)
                        {
                            _logService.Log(LogLevel.Warning, "CheckSettingStatusesAsync timed out, continuing anyway");
                        }
                    }
                    else
                    {
                        _logService.Log(LogLevel.Warning, $"Failed to set power plan to {PowerPlanLabels[planIndex]} ({planGuid})");
                        StatusText = $"Failed to apply {PowerPlanLabels[planIndex]} power plan";
                    }
                }
                else
                {
                    _logService.Log(LogLevel.Warning, $"Setting power plan timed out after 10 seconds");
                    StatusText = $"Timeout while applying {PowerPlanLabels[planIndex]} power plan";
                    
                    // Cancel the operation
                    cancellationTokenSource.Cancel();
                }
            }
            catch (TaskCanceledException)
            {
                _logService.Log(LogLevel.Warning, "Power plan application was canceled due to timeout");
                StatusText = "Power plan application timed out";
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error applying power plan: {ex.Message}");
                _logService.Log(LogLevel.Debug, $"Exception details: {ex}");
                StatusText = $"Error applying power plan: {ex.Message}";
            }
            finally
            {
                // Make sure we always reset IsApplyingPowerPlan to false when done
                IsApplyingPowerPlan = false;
                _logService.Log(LogLevel.Info, "Reset IsApplyingPowerPlan to false in ApplyPowerPlanAsync");
            }
        }

        /// <summary>
        /// Checks the status of all power settings.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public override async Task CheckSettingStatusesAsync()
        {
            try
            {
                IsLoading = true;
                _logService.Log(LogLevel.Info, $"Checking status for {Settings.Count} power settings");

                foreach (var setting in Settings)
                {
                    try
                    {
                        // Check if this is a command-based setting with PowerCfg settings
                        bool isCommandBasedSetting = setting.CustomProperties != null &&
                                                    setting.CustomProperties.ContainsKey("PowerCfgSettings") &&
                                                    setting.CustomProperties["PowerCfgSettings"] is List<PowerCfgSetting>;

                        if (isCommandBasedSetting)
                        {
                            _logService.Log(LogLevel.Info, $"Checking command-based setting status for: {setting.Name}");
                            
                            // Get PowerCfg settings from CustomProperties
                            var powerCfgSettings = setting.CustomProperties["PowerCfgSettings"] as List<PowerCfgSetting>;
                            
                            try
                            {
                                // Special handling for Desktop Slideshow setting
                                bool isDesktopSlideshowSetting = setting.Name.Contains("Desktop Slideshow", StringComparison.OrdinalIgnoreCase);
                                
                                // Check if all PowerCfg settings are applied
                                bool allApplied = await _powerPlanService.AreAllPowerCfgSettingsAppliedAsync(powerCfgSettings);
                                
                                // Update setting status based on PowerCfg settings
                                setting.Status = allApplied ? RegistrySettingStatus.Applied : RegistrySettingStatus.NotApplied;
                                
                                // Set a more descriptive current value
                                if (isDesktopSlideshowSetting)
                                {
                                    // For Desktop Slideshow, the value is counter-intuitive
                                    // When enabled (IsSelected=true), the slideshow is "Available" (value=0)
                                    // When disabled (IsSelected=false), the slideshow is "Paused" (value=1)
                                    setting.CurrentValue = allApplied ? "Available" : "Paused";
                                }
                                else
                                {
                                    setting.CurrentValue = allApplied ? "Enabled" : "Disabled";
                                }
                                
                                // Set status message with more detailed information
                                setting.StatusMessage = allApplied
                                    ? "This setting is currently applied."
                                    : "This setting is not currently applied.";
                                
                                _logService.Log(LogLevel.Info, $"Command-based setting {setting.Name} status: {setting.Status}, isApplied={allApplied}, currentValue={setting.CurrentValue}");
                                
                                // Update IsSelected to match the detected state
                                setting.IsUpdatingFromCode = true;
                                try
                                {
                                    setting.IsSelected = allApplied;
                                }
                                finally
                                {
                                    setting.IsUpdatingFromCode = false;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logService.Log(LogLevel.Error, $"Error checking PowerCfg setting status for {setting.Name}: {ex.Message}");
                                
                                // On error, assume the setting is in its default state
                                setting.Status = RegistrySettingStatus.NotApplied;
                                setting.CurrentValue = "Unknown";
                                setting.StatusMessage = "Could not determine the current status of this setting.";
                            }
                        }
                        else if (setting.RegistrySetting != null)
                        {
                            _logService.Log(LogLevel.Info, $"Checking registry-based setting status for: {setting.Name}");

                            // Get the status
                            var status = await _registryService.GetSettingStatusAsync(setting.RegistrySetting);
                            _logService.Log(LogLevel.Info, $"Status for {setting.Name}: {status}");
                            setting.Status = status;

                            // Get the current value
                            var currentValue = await _registryService.GetCurrentValueAsync(setting.RegistrySetting);
                            _logService.Log(LogLevel.Info, $"Current value for {setting.Name}: {currentValue ?? "null"}");
                            setting.CurrentValue = currentValue;

                            setting.LinkedRegistrySettingsWithValues.Clear();
                            setting.LinkedRegistrySettingsWithValues.Add(new Winhance.WPF.Features.Common.Models.LinkedRegistrySettingWithValue(setting.RegistrySetting, currentValue));

                            // Set status message
                            setting.StatusMessage = GetStatusMessage(setting);

                            // Update IsSelected based on status
                            bool shouldBeSelected = status == RegistrySettingStatus.Applied;

                            // Set the checkbox state to match the registry state
                            _logService.Log(LogLevel.Info, $"Setting {setting.Name} status is {status}, setting IsSelected to {shouldBeSelected}");
                            setting.IsUpdatingFromCode = true;
                            try
                            {
                                setting.IsSelected = shouldBeSelected;
                            }
                            finally
                            {
                                setting.IsUpdatingFromCode = false;
                            }
                        }
                        else if (setting.LinkedRegistrySettings != null && setting.LinkedRegistrySettings.Settings.Count > 0)
                        {
                            _logService.Log(LogLevel.Info, $"Checking linked registry settings status for: {setting.Name} with {setting.LinkedRegistrySettings.Settings.Count} registry entries");

                            // Log details about each registry entry for debugging
                            foreach (var regSetting in setting.LinkedRegistrySettings.Settings)
                            {
                                string hiveString = RegistryExtensions.GetRegistryHiveString(regSetting.Hive);
                                string fullPath = $"{hiveString}\\{regSetting.SubKey}";
                                _logService.Log(LogLevel.Info, $"Registry entry: {fullPath}\\{regSetting.Name}, EnabledValue={regSetting.EnabledValue}, DisabledValue={regSetting.DisabledValue}");

                                // Check if the key exists
                                bool keyExists = _registryService.KeyExists(fullPath);
                                _logService.Log(LogLevel.Info, $"Key exists: {keyExists}");

                                if (keyExists)
                                {
                                    // Check if the value exists and get its current value
                                    var currentValue = await _registryService.GetCurrentValueAsync(regSetting);
                                    _logService.Log(LogLevel.Info, $"Current value: {currentValue ?? "null"}");
                                }
                            }

                            // Get the combined status of all linked settings
                            var status = await _registryService.GetLinkedSettingsStatusAsync(setting.LinkedRegistrySettings);
                            _logService.Log(LogLevel.Info, $"Combined status for {setting.Name}: {status}");
                            setting.Status = status;

                            // For current value display, use the first setting's value
                            if (setting.LinkedRegistrySettings.Settings.Count > 0)
                            {
                                var firstSetting = setting.LinkedRegistrySettings.Settings[0];
                                var currentValue = await _registryService.GetCurrentValueAsync(firstSetting);
                                _logService.Log(LogLevel.Info, $"Current value for {setting.Name} (first entry): {currentValue ?? "null"}");
                                setting.CurrentValue = currentValue;

                                // Check for null registry values
                                bool anyNull = false;

                                // Populate the LinkedRegistrySettingsWithValues collection for tooltip display
                                setting.LinkedRegistrySettingsWithValues.Clear();
                                foreach (var regSetting in setting.LinkedRegistrySettings.Settings)
                                {
                                    var regCurrentValue = await _registryService.GetCurrentValueAsync(regSetting);
                                    _logService.Log(LogLevel.Info, $"Current value for linked setting {regSetting.Name}: {regCurrentValue ?? "null"}");
                                    
                                    if (regCurrentValue == null)
                                    {
                                        anyNull = true;
                                    }
                                    
                                    setting.LinkedRegistrySettingsWithValues.Add(new Winhance.WPF.Features.Common.Models.LinkedRegistrySettingWithValue(regSetting, regCurrentValue));
                                }
                                
                                // Set IsRegistryValueNull for linked settings
                                setting.IsRegistryValueNull = anyNull;
                            }

                            // Set status message
                            setting.StatusMessage = GetStatusMessage(setting);

                            // Update IsSelected based on status
                            bool shouldBeSelected = status == RegistrySettingStatus.Applied;

                            // Set the checkbox state to match the registry state
                            _logService.Log(LogLevel.Info, $"Setting {setting.Name} status is {status}, setting IsSelected to {shouldBeSelected}");
                            setting.IsUpdatingFromCode = true;
                            try
                            {
                                setting.IsSelected = shouldBeSelected;
                            }
                            finally
                            {
                                setting.IsUpdatingFromCode = false;
                            }
                        }
                        else
                        {
                            _logService.Log(LogLevel.Warning, $"No registry setting or command-based setting found for {setting.Name}");
                            setting.Status = RegistrySettingStatus.Unknown;
                            setting.StatusMessage = "Setting information is missing";
                        }

                        // If this is a grouped setting, update child settings too
                        if (setting.IsGroupedSetting && setting.ChildSettings.Count > 0)
                        {
                            _logService.Log(LogLevel.Info, $"Updating {setting.ChildSettings.Count} child settings for {setting.Name}");
                            foreach (var childSetting in setting.ChildSettings)
                            {
                                // Check if this is a command-based child setting
                                bool isCommandBasedChildSetting = childSetting.CustomProperties != null &&
                                                                childSetting.CustomProperties.ContainsKey("PowerCfgSettings") &&
                                                                childSetting.CustomProperties["PowerCfgSettings"] is List<PowerCfgSetting>;

                                if (isCommandBasedChildSetting)
                                {
                                    try
                                    {
                                        var powerCfgSettings = childSetting.CustomProperties["PowerCfgSettings"] as List<PowerCfgSetting>;
                                        
                                        // Special handling for Desktop Slideshow setting
                                        bool isDesktopSlideshowSetting = childSetting.Name.Contains("Desktop Slideshow", StringComparison.OrdinalIgnoreCase);
                                        
                                        bool allApplied = await _powerPlanService.AreAllPowerCfgSettingsAppliedAsync(powerCfgSettings);
                                        
                                        childSetting.Status = allApplied ? RegistrySettingStatus.Applied : RegistrySettingStatus.NotApplied;
                                        
                                        // Set a more descriptive current value
                                        if (isDesktopSlideshowSetting)
                                        {
                                            childSetting.CurrentValue = allApplied ? "Available" : "Paused";
                                        }
                                        else
                                        {
                                            childSetting.CurrentValue = allApplied ? "Enabled" : "Disabled";
                                        }
                                        
                                        childSetting.StatusMessage = allApplied
                                            ? "This setting is currently applied."
                                            : "This setting is not currently applied.";
                                        
                                        // Update IsSelected based on status
                                        childSetting.IsUpdatingFromCode = true;
                                        try
                                        {
                                            childSetting.IsSelected = allApplied;
                                        }
                                        finally
                                        {
                                            childSetting.IsUpdatingFromCode = false;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logService.Log(LogLevel.Error, $"Error checking PowerCfg setting status for child setting {childSetting.Name}: {ex.Message}");
                                        
                                        // On error, assume the setting is in its default state
                                        childSetting.Status = RegistrySettingStatus.NotApplied;
                                        childSetting.CurrentValue = "Unknown";
                                        childSetting.StatusMessage = "Could not determine the current status of this setting.";
                                    }
                                }
                                else if (childSetting.RegistrySetting != null)
                                {
                                    var status = await _registryService.GetSettingStatusAsync(childSetting.RegistrySetting);
                                    childSetting.Status = status;

                                    var currentValue = await _registryService.GetCurrentValueAsync(childSetting.RegistrySetting);
                                    childSetting.CurrentValue = currentValue;

                                    childSetting.StatusMessage = GetStatusMessage(childSetting);

                                    // Update IsSelected based on status
                                    bool shouldBeSelected = status == RegistrySettingStatus.Applied;

                                    // Set the checkbox state to match the registry state
                                    _logService.Log(LogLevel.Info, $"Child setting {childSetting.Name} status is {status}, setting IsSelected to {shouldBeSelected}");
                                    childSetting.IsUpdatingFromCode = true;
                                    try
                                    {
                                        childSetting.IsSelected = shouldBeSelected;
                                    }
                                    finally
                                    {
                                        childSetting.IsUpdatingFromCode = false;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Error, $"Error updating status for setting {setting.Name}: {ex.Message}");
                        setting.Status = RegistrySettingStatus.Error;
                        setting.StatusMessage = $"Error: {ex.Message}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error checking power setting statuses: {ex.Message}");
                throw;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Updates the IsSelected state based on individual selections.
        /// </summary>
        private void UpdateIsSelectedState()
        {
            if (Settings.Count == 0)
                return;

            var selectedCount = Settings.Count(setting => setting.IsSelected);
            IsSelected = selectedCount == Settings.Count;
        }

        /// <summary>
        /// Applies a registry setting with the specified value.
        /// </summary>
        /// <param name="registrySetting">The registry setting to apply.</param>
        /// <param name="value">The value to set.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ApplyRegistrySetting(RegistrySetting registrySetting, object value)
        {
            string hiveString = registrySetting.Hive.ToString();
            if (hiveString == "LocalMachine") hiveString = "HKLM";
            else if (hiveString == "CurrentUser") hiveString = "HKCU";
            else if (hiveString == "ClassesRoot") hiveString = "HKCR";
            else if (hiveString == "Users") hiveString = "HKU";
            else if (hiveString == "CurrentConfig") hiveString = "HKCC";

            string fullPath = $"{hiveString}\\{registrySetting.SubKey}";
            
            if (value == null)
            {
                await _registryService.DeleteValue(registrySetting.Hive, registrySetting.SubKey, registrySetting.Name);
            }
            else
            {
                _registryService.SetValue(fullPath, registrySetting.Name, value, registrySetting.ValueType);
            }
        }

        /// <summary>
        /// Gets a user-friendly status message for a setting.
        /// </summary>
        /// <param name="setting">The setting to get the status message for.</param>
        /// <returns>A user-friendly status message.</returns>
        private string GetStatusMessage(ApplicationSettingItem setting)
        {
            return setting.Status switch
            {
                RegistrySettingStatus.Applied =>
                    "This setting is already applied with the recommended value.",

                RegistrySettingStatus.NotApplied =>
                    setting.CurrentValue == null
                        ? "This setting is not applied (registry value does not exist)."
                        : "This setting is using the default value.",

                RegistrySettingStatus.Modified =>
                    "This setting has a custom value that differs from the recommended value.",

                RegistrySettingStatus.Error =>
                    "An error occurred while checking this setting's status.",

                _ => "The status of this setting is unknown."
            };
        }

        /// <summary>
        /// Converts a tuple to a RegistrySetting object.
        /// </summary>
        /// <param name="key">The key for the setting.</param>
        /// <param name="settingTuple">The tuple containing the setting information.</param>
        /// <returns>A RegistrySetting object.</returns>
        private RegistrySetting ConvertToRegistrySetting(string key, (string Path, string Name, object Value, Microsoft.Win32.RegistryValueKind ValueKind) settingTuple)
        {
            // Determine the registry hive from the path
            RegistryHive hive = RegistryHive.LocalMachine; // Default to HKLM
            if (settingTuple.Path.StartsWith("HKEY_CURRENT_USER") || settingTuple.Path.StartsWith("HKCU") || settingTuple.Path.StartsWith("Software\\"))
            {
                hive = RegistryHive.CurrentUser;
            }
            else if (settingTuple.Path.StartsWith("HKEY_LOCAL_MACHINE") || settingTuple.Path.StartsWith("HKLM"))
            {
                hive = RegistryHive.LocalMachine;
            }
            else if (settingTuple.Path.StartsWith("HKEY_CLASSES_ROOT") || settingTuple.Path.StartsWith("HKCR"))
            {
                hive = RegistryHive.ClassesRoot;
            }
            else if (settingTuple.Path.StartsWith("HKEY_USERS") || settingTuple.Path.StartsWith("HKU"))
            {
                hive = RegistryHive.Users;
            }
            else if (settingTuple.Path.StartsWith("HKEY_CURRENT_CONFIG") || settingTuple.Path.StartsWith("HKCC"))
            {
                hive = RegistryHive.CurrentConfig;
            }

            // Clean up the path to remove any hive prefix
            string subKey = settingTuple.Path;
            if (subKey.StartsWith("HKEY_CURRENT_USER\\") || subKey.StartsWith("HKCU\\"))
            {
                subKey = subKey.Replace("HKEY_CURRENT_USER\\", "").Replace("HKCU\\", "");
            }
            else if (subKey.StartsWith("HKEY_LOCAL_MACHINE\\") || subKey.StartsWith("HKLM\\"))
            {
                subKey = subKey.Replace("HKEY_LOCAL_MACHINE\\", "").Replace("HKLM\\", "");
            }
            else if (subKey.StartsWith("HKEY_CLASSES_ROOT\\") || subKey.StartsWith("HKCR\\"))
            {
                subKey = subKey.Replace("HKEY_CLASSES_ROOT\\", "").Replace("HKCR\\", "");
            }
            else if (subKey.StartsWith("HKEY_USERS\\") || subKey.StartsWith("HKU\\"))
            {
                subKey = subKey.Replace("HKEY_USERS\\", "").Replace("HKU\\", "");
            }
            else if (subKey.StartsWith("HKEY_CURRENT_CONFIG\\") || subKey.StartsWith("HKCC\\"))
            {
                subKey = subKey.Replace("HKEY_CURRENT_CONFIG\\", "").Replace("HKCC\\", "");
            }

            // Create and return the RegistrySetting object
            var registrySetting = new RegistrySetting
            {
                Category = "Power",
                Hive = hive,
                SubKey = subKey,
                Name = settingTuple.Name,
                EnabledValue = settingTuple.Value,  // Recommended value becomes EnabledValue
                DisabledValue = settingTuple.Value,
                ValueType = settingTuple.ValueKind,
                // Keep these for backward compatibility
                RecommendedValue = settingTuple.Value,
                DefaultValue = settingTuple.Value,
                Description = $"Power setting: {key}"
            };

            return registrySetting;
        }

        /// <summary>
        /// Gets a friendly name for a power setting.
        /// </summary>
        /// <param name="key">The power setting key.</param>
        /// <returns>A user-friendly name for the power setting.</returns>
        private string GetFriendlyNameForPowerSetting(string key)
        {
            return key switch
            {
                "HibernateEnabled" => "Disable Hibernate",
                "HibernateEnabledDefault" => "Disable Hibernate by Default",
                "VideoQuality" => "High Video Quality on Battery",
                "LockOption" => "Hide Lock Option",
                "FastBoot" => "Disable Fast Boot",
                "CpuUnpark" => "CPU Core Unparking",
                "PowerThrottling" => "Disable Power Throttling",
                _ => key
            };
        }
    }
}
