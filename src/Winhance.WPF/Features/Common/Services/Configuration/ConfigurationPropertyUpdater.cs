using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Winhance.Core.Features.Common.Enums;
using Winhance.Core.Features.Common.Interfaces;
using Winhance.Core.Features.Common.Models;
using Winhance.WPF.Features.Common.Models;

namespace Winhance.WPF.Features.Common.Services.Configuration
{
    /// <summary>
    /// Service for updating properties of configuration items.
    /// </summary>
    public class ConfigurationPropertyUpdater : IConfigurationPropertyUpdater
    {
        private readonly ILogService _logService;
        private readonly IRegistryService _registryService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationPropertyUpdater"/> class.
        /// </summary>
        /// <param name="logService">The log service.</param>
        /// <param name="registryService">The registry service.</param>
        public ConfigurationPropertyUpdater(
            ILogService logService,
            IRegistryService registryService
        )
        {
            _logService = logService;
            _registryService = registryService;
        }

        /// <summary>
        /// Updates properties of items based on the configuration.
        /// </summary>
        /// <param name="items">The items to update.</param>
        /// <param name="configFile">The configuration file containing the updates.</param>
        /// <returns>The number of items that were updated.</returns>
        public async Task<int> UpdateItemsAsync(
            IEnumerable<object> items,
            ConfigurationFile configFile
        )
        {
            int updatedCount = 0;

            // Create dictionaries of config items by name and ID for faster lookup
            var configItemsByName = new Dictionary<string, ConfigurationItem>(
                StringComparer.OrdinalIgnoreCase
            );
            var configItemsById = new Dictionary<string, ConfigurationItem>(
                StringComparer.OrdinalIgnoreCase
            );

            if (configFile.Items != null)
            {
                foreach (var item in configFile.Items)
                {
                    if (
                        !string.IsNullOrEmpty(item.Name)
                        && !configItemsByName.ContainsKey(item.Name)
                    )
                    {
                        configItemsByName.Add(item.Name, item);
                    }

                    if (
                        item.CustomProperties.TryGetValue("Id", out var id)
                        && id != null
                        && !string.IsNullOrEmpty(id.ToString())
                        && !configItemsById.ContainsKey(id.ToString())
                    )
                    {
                        configItemsById.Add(id.ToString(), item);
                    }
                }
            }

            // Update the items
            foreach (var item in items)
            {
                try
                {
                    var nameProperty = item.GetType().GetProperty("Name");
                    var idProperty = item.GetType().GetProperty("Id");
                    var isSelectedProperty = item.GetType().GetProperty("IsSelected");

                    if (nameProperty != null && isSelectedProperty != null)
                    {
                        var name = nameProperty.GetValue(item)?.ToString();
                        var id = idProperty?.GetValue(item)?.ToString();

                        ConfigurationItem configItem = null;

                        // Try to match by ID first
                        if (
                            !string.IsNullOrEmpty(id)
                            && configItemsById.TryGetValue(id, out var itemById)
                        )
                        {
                            configItem = itemById;
                        }
                        // Then try to match by name
                        else if (
                            !string.IsNullOrEmpty(name)
                            && configItemsByName.TryGetValue(name, out var itemByName)
                        )
                        {
                            configItem = itemByName;
                        }

                        if (configItem != null)
                        {
                            bool anyPropertyUpdated = false;

                            // Update IsSelected - Always apply regardless of current state
                            bool currentIsSelected = (bool)(
                                isSelectedProperty.GetValue(item) ?? false
                            );

                            // Always set the value and apply registry settings, even if it hasn't changed
                            isSelectedProperty.SetValue(item, configItem.IsSelected);
                            anyPropertyUpdated = true;
                            _logService.Log(
                                LogLevel.Info,
                                $"Updated IsSelected for item {name} (ID: {id}) to {configItem.IsSelected}"
                            );

                            // Always apply registry settings for this item during import
                            await ApplyRegistrySettingsAsync(item, configItem.IsSelected);

                            // Update additional properties
                            if (UpdateAdditionalProperties(item, configItem))
                            {
                                anyPropertyUpdated = true;
                            }

                            if (anyPropertyUpdated)
                            {
                                updatedCount++;
                                TriggerPropertyChangedIfPossible(item);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue processing other items
                    _logService.Log(
                        LogLevel.Debug,
                        $"Error applying configuration to item: {ex.Message}"
                    );
                }
            }

            return updatedCount;
        }

        /// <summary>
        /// Updates additional properties of an item based on the configuration.
        /// </summary>
        /// <param name="item">The item to update.</param>
        /// <param name="configItem">The configuration item containing the updates.</param>
        /// <returns>True if any properties were updated, false otherwise.</returns>
        public bool UpdateAdditionalProperties(object item, ConfigurationItem configItem)
        {
            bool anyPropertyUpdated = false;

            try
            {
                // Get the item type to access its properties
                var itemType = item.GetType();
                var itemName =
                    itemType.GetProperty("Name")?.GetValue(item)?.ToString() ?? "Unknown";
                var itemId = itemType.GetProperty("Id")?.GetValue(item)?.ToString() ?? "";

                // Check if this is a special item that needs special handling
                bool isUacSlider =
                    itemName.Contains("User Account Control") || itemId == "UACSlider";
                bool isPowerPlan = itemName.Contains("Power Plan") || itemId == "PowerPlanComboBox";
                bool isThemeSelector =
                    itemName.Contains("Windows Theme")
                    || itemName.Contains("Theme Selector")
                    || itemName.Contains("Choose Your Mode");

                // Special handling for Windows Theme Selector
                if (isThemeSelector)
                {
                    // For Windows Theme Selector, prioritize using SelectedValue or SelectedTheme
                    var selectedValueProperty = itemType.GetProperty("SelectedValue");
                    if (selectedValueProperty != null)
                    {
                        try
                        {
                            string currentSelectedValue = selectedValueProperty
                                .GetValue(item)
                                ?.ToString();
                            string newSelectedValue = null;

                            // First try to get the value from CustomProperties.SelectedTheme (preferred method)
                            if (
                                configItem.CustomProperties.TryGetValue(
                                    "SelectedTheme",
                                    out var selectedTheme
                                )
                                && selectedTheme != null
                            )
                            {
                                newSelectedValue = selectedTheme.ToString();
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Found SelectedTheme in CustomProperties: {newSelectedValue} for item {itemName}"
                                );
                            }
                            // Then try to get the value directly from configItem.SelectedValue
                            else if (!string.IsNullOrEmpty(configItem.SelectedValue))
                            {
                                newSelectedValue = configItem.SelectedValue;
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Found SelectedValue in config: {newSelectedValue} for item {itemName}"
                                );
                            }
                            // As a last resort, try to derive it from SliderValue (for backward compatibility)
                            else if (
                                configItem.CustomProperties.TryGetValue(
                                    "SliderValue",
                                    out var sliderValue
                                )
                            )
                            {
                                int sliderValueInt = Convert.ToInt32(sliderValue);
                                newSelectedValue = sliderValueInt == 1 ? "Dark Mode" : "Light Mode";
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Derived SelectedValue from SliderValue {sliderValueInt}: {newSelectedValue} for item {itemName}"
                                );
                            }

                            if (
                                !string.IsNullOrEmpty(newSelectedValue)
                                && currentSelectedValue != newSelectedValue
                            )
                            {
                                selectedValueProperty.SetValue(item, newSelectedValue);
                                anyPropertyUpdated = true;
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Updated SelectedValue for item {itemName} from {currentSelectedValue} to {newSelectedValue}"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.Log(
                                LogLevel.Debug,
                                $"Error updating SelectedValue for item {itemName}: {ex.Message}"
                            );
                        }
                    }

                    // Also update SelectedTheme property if it exists
                    var selectedThemeProperty = itemType.GetProperty("SelectedTheme");
                    if (selectedThemeProperty != null)
                    {
                        try
                        {
                            string currentSelectedTheme = selectedThemeProperty
                                .GetValue(item)
                                ?.ToString();
                            string newSelectedTheme = null;

                            // Try to get SelectedTheme from CustomProperties first (preferred method)
                            if (
                                configItem.CustomProperties.TryGetValue(
                                    "SelectedTheme",
                                    out var selectedTheme
                                )
                                && selectedTheme != null
                            )
                            {
                                newSelectedTheme = selectedTheme.ToString();
                            }
                            // Then try to use SelectedValue directly
                            else if (!string.IsNullOrEmpty(configItem.SelectedValue))
                            {
                                newSelectedTheme = configItem.SelectedValue;
                            }
                            // As a last resort, try to derive it from SliderValue (for backward compatibility)
                            else if (
                                configItem.CustomProperties.TryGetValue(
                                    "SliderValue",
                                    out var themeSliderValue
                                )
                            )
                            {
                                int sliderValueInt = Convert.ToInt32(themeSliderValue);
                                newSelectedTheme = sliderValueInt == 1 ? "Dark Mode" : "Light Mode";
                            }

                            if (
                                !string.IsNullOrEmpty(newSelectedTheme)
                                && currentSelectedTheme != newSelectedTheme
                            )
                            {
                                selectedThemeProperty.SetValue(item, newSelectedTheme);
                                anyPropertyUpdated = true;
                                _logService.Log(
                                    LogLevel.Debug,
                                    $"Updated SelectedTheme for item {itemName} to {newSelectedTheme}"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.Log(
                                LogLevel.Debug,
                                $"Error updating SelectedTheme for item {itemName}: {ex.Message}"
                            );
                        }
                    }
                }
                // For non-theme items, handle SliderValue for ThreeStateSlider or ComboBox
                else if (
                    configItem.CustomProperties.TryGetValue("SliderValue", out var sliderValue)
                )
                {
                    var sliderValueProperty = itemType.GetProperty("SliderValue");
                    if (sliderValueProperty != null)
                    {
                        try
                        {
                            int newSliderValue = Convert.ToInt32(sliderValue);
                            int currentSliderValue = (int)(sliderValueProperty.GetValue(item) ?? 0);

                            if (currentSliderValue != newSliderValue)
                            {
                                _logService.Log(
                                    LogLevel.Info,
                                    $"About to update SliderValue for item {itemName} from {currentSliderValue} to {newSliderValue}"
                                );
                                sliderValueProperty.SetValue(item, newSliderValue);
                                anyPropertyUpdated = true;
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Updated SliderValue for item {itemName} to {newSliderValue}"
                                );

                                // Get the control type
                                var controlTypeProperty = itemType.GetProperty("ControlType");
                                if (controlTypeProperty != null)
                                {
                                    var controlType = controlTypeProperty.GetValue(item);
                                    _logService.Log(
                                        LogLevel.Info,
                                        $"Item {itemName} has ControlType: {controlType}"
                                    );

                                    // If this is a ComboBox, also update the SelectedValue property if available
                                    if (controlType?.ToString() == "ComboBox")
                                    {
                                        _logService.Log(
                                            LogLevel.Info,
                                            $"Item {itemName} is a ComboBox, checking for SelectedValue property"
                                        );
                                        var comboBoxSelectedValueProperty = itemType.GetProperty(
                                            "SelectedValue"
                                        );
                                        if (comboBoxSelectedValueProperty != null)
                                        {
                                            // First try to get the value directly from configItem.SelectedValue
                                            if (!string.IsNullOrEmpty(configItem.SelectedValue))
                                            {
                                                _logService.Log(
                                                    LogLevel.Info,
                                                    $"Setting SelectedValue to {configItem.SelectedValue} from config for ComboBox {itemName}"
                                                );
                                                comboBoxSelectedValueProperty.SetValue(
                                                    item,
                                                    configItem.SelectedValue
                                                );
                                                anyPropertyUpdated = true;
                                            }
                                            // Then try to get the value from CustomProperties.SelectedTheme
                                            else if (
                                                configItem.CustomProperties.TryGetValue(
                                                    "SelectedTheme",
                                                    out var selectedTheme
                                                )
                                                && selectedTheme != null
                                            )
                                            {
                                                _logService.Log(
                                                    LogLevel.Info,
                                                    $"Setting SelectedValue to {selectedTheme} from CustomProperties.SelectedTheme for ComboBox {itemName}"
                                                );
                                                comboBoxSelectedValueProperty.SetValue(
                                                    item,
                                                    selectedTheme.ToString()
                                                );
                                                anyPropertyUpdated = true;
                                            }
                                            // Finally try to map from SliderValue using SliderLabels
                                            else
                                            {
                                                // Try to get the SliderLabels collection to map the index to a value
                                                var sliderLabelsProperty = itemType.GetProperty(
                                                    "SliderLabels"
                                                );
                                                if (sliderLabelsProperty != null)
                                                {
                                                    var sliderLabels =
                                                        sliderLabelsProperty.GetValue(item)
                                                        as System.Collections.IList;
                                                    if (
                                                        sliderLabels != null
                                                        && newSliderValue < sliderLabels.Count
                                                    )
                                                    {
                                                        var selectedValue = sliderLabels[
                                                            newSliderValue
                                                        ]
                                                            ?.ToString();
                                                        if (!string.IsNullOrEmpty(selectedValue))
                                                        {
                                                            _logService.Log(
                                                                LogLevel.Info,
                                                                $"Setting SelectedValue to {selectedValue} from SliderLabels for ComboBox {itemName}"
                                                            );
                                                            comboBoxSelectedValueProperty.SetValue(
                                                                item,
                                                                selectedValue
                                                            );
                                                            anyPropertyUpdated = true;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                // Also update specific properties based on the item identification
                                if (isPowerPlan)
                                {
                                    // Update PowerPlanValue property
                                    var powerPlanValueProperty = itemType.GetProperty(
                                        "PowerPlanValue"
                                    );
                                    if (powerPlanValueProperty != null)
                                    {
                                        int currentPowerPlanValue = (int)(
                                            powerPlanValueProperty.GetValue(item) ?? 0
                                        );
                                        if (currentPowerPlanValue != newSliderValue)
                                        {
                                            powerPlanValueProperty.SetValue(item, newSliderValue);
                                            anyPropertyUpdated = true;
                                            _logService.Log(
                                                LogLevel.Info,
                                                $"Updated PowerPlanValue for item {itemName} from {currentPowerPlanValue} to {newSliderValue}"
                                            );

                                            // Try to call OnPowerPlanValueChanged method to apply the power plan
                                            try
                                            {
                                                var onPowerPlanValueChangedMethod =
                                                    itemType.GetMethod(
                                                        "OnPowerPlanValueChanged",
                                                        System.Reflection.BindingFlags.NonPublic
                                                            | System
                                                                .Reflection
                                                                .BindingFlags
                                                                .Instance
                                                    );

                                                if (onPowerPlanValueChangedMethod != null)
                                                {
                                                    _logService.Log(
                                                        LogLevel.Info,
                                                        $"Calling OnPowerPlanValueChanged method with value {newSliderValue}"
                                                    );
                                                    onPowerPlanValueChangedMethod.Invoke(
                                                        item,
                                                        new object[] { newSliderValue }
                                                    );
                                                }
                                                else
                                                {
                                                    _logService.Log(
                                                        LogLevel.Debug,
                                                        "OnPowerPlanValueChanged method not found"
                                                    );

                                                    // Try to find ApplyPowerPlanCommand
                                                    var applyPowerPlanCommandProperty =
                                                        itemType.GetProperty(
                                                            "ApplyPowerPlanCommand"
                                                        );
                                                    if (applyPowerPlanCommandProperty != null)
                                                    {
                                                        var command =
                                                            applyPowerPlanCommandProperty.GetValue(
                                                                item
                                                            ) as System.Windows.Input.ICommand;
                                                        if (
                                                            command != null
                                                            && command.CanExecute(newSliderValue)
                                                        )
                                                        {
                                                            _logService.Log(
                                                                LogLevel.Info,
                                                                $"Executing ApplyPowerPlanCommand with value {newSliderValue}"
                                                            );
                                                            command.Execute(newSliderValue);
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                _logService.Log(
                                                    LogLevel.Warning,
                                                    $"Error calling power plan methods: {ex.Message}"
                                                );
                                            }
                                        }
                                    }

                                    // Also update any ComboBox in the Settings collection
                                    try
                                    {
                                        var settingsProperty = itemType.GetProperty("Settings");
                                        if (settingsProperty != null)
                                        {
                                            var settings =
                                                settingsProperty.GetValue(item)
                                                as System.Collections.IEnumerable;
                                            if (settings != null)
                                            {
                                                foreach (var setting in settings)
                                                {
                                                    var settingType = setting.GetType();
                                                    var settingIdProperty = settingType.GetProperty(
                                                        "Id"
                                                    );
                                                    var settingNameProperty =
                                                        settingType.GetProperty("Name");

                                                    string settingId = settingIdProperty
                                                        ?.GetValue(setting)
                                                        ?.ToString();
                                                    string settingName = settingNameProperty
                                                        ?.GetValue(setting)
                                                        ?.ToString();

                                                    bool isPowerPlanSetting =
                                                        settingId == "PowerPlanComboBox"
                                                        || (
                                                            settingName != null
                                                            && settingName.Contains("Power Plan")
                                                        );

                                                    if (isPowerPlanSetting)
                                                    {
                                                        var settingSliderValueProperty =
                                                            settingType.GetProperty("SliderValue");
                                                        if (settingSliderValueProperty != null)
                                                        {
                                                            _logService.Log(
                                                                LogLevel.Info,
                                                                $"Updating SliderValue for Power Plan setting to {newSliderValue}"
                                                            );
                                                            settingSliderValueProperty.SetValue(
                                                                setting,
                                                                newSliderValue
                                                            );
                                                        }

                                                        var selectedValueProperty =
                                                            settingType.GetProperty(
                                                                "SelectedValue"
                                                            );
                                                        if (selectedValueProperty != null)
                                                        {
                                                            // First try to get the value from PowerPlanOptions in the configItem
                                                            if (
                                                                configItem.CustomProperties.TryGetValue(
                                                                    "PowerPlanOptions",
                                                                    out var powerPlanOptions
                                                                )
                                                            )
                                                            {
                                                                // Handle different types of PowerPlanOptions
                                                                if (
                                                                    powerPlanOptions
                                                                        is List<string> options
                                                                    && newSliderValue >= 0
                                                                    && newSliderValue
                                                                        < options.Count
                                                                )
                                                                {
                                                                    string powerPlanLabel = options[
                                                                        newSliderValue
                                                                    ];
                                                                    _logService.Log(
                                                                        LogLevel.Info,
                                                                        $"Updating SelectedValue for Power Plan setting to {powerPlanLabel} from configItem.PowerPlanOptions"
                                                                    );
                                                                    selectedValueProperty.SetValue(
                                                                        setting,
                                                                        powerPlanLabel
                                                                    );
                                                                }
                                                                else if (
                                                                    powerPlanOptions
                                                                        is Newtonsoft.Json.Linq.JArray jArray
                                                                    && newSliderValue >= 0
                                                                    && newSliderValue < jArray.Count
                                                                )
                                                                {
                                                                    string powerPlanLabel = jArray[
                                                                        newSliderValue
                                                                    ]
                                                                        ?.ToString();
                                                                    _logService.Log(
                                                                        LogLevel.Info,
                                                                        $"Updating SelectedValue for Power Plan setting to {powerPlanLabel} from configItem.PowerPlanOptions (JArray)"
                                                                    );
                                                                    selectedValueProperty.SetValue(
                                                                        setting,
                                                                        powerPlanLabel
                                                                    );
                                                                }
                                                                else
                                                                {
                                                                    // Fall back to SliderLabels
                                                                    TryUpdateFromSliderLabels(
                                                                        settingType,
                                                                        setting,
                                                                        selectedValueProperty,
                                                                        newSliderValue
                                                                    );
                                                                }
                                                            }
                                                            // Then try to get it from the setting's SliderLabels
                                                            else
                                                            {
                                                                TryUpdateFromSliderLabels(
                                                                    settingType,
                                                                    setting,
                                                                    selectedValueProperty,
                                                                    newSliderValue
                                                                );
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logService.Log(
                                            LogLevel.Debug,
                                            $"Error updating Power Plan settings: {ex.Message}"
                                        );
                                    }
                                }
                                else if (isUacSlider)
                                {
                                    var uacLevelProperty = itemType.GetProperty("SelectedUacLevel");
                                    if (uacLevelProperty != null)
                                    {
                                        uacLevelProperty.SetValue(item, newSliderValue);
                                        anyPropertyUpdated = true;
                                        _logService.Log(
                                            LogLevel.Debug,
                                            $"Updated SelectedUacLevel for item {itemName} to {newSliderValue}"
                                        );

                                        // Force the correct ControlType
                                        if (controlTypeProperty != null)
                                        {
                                            controlTypeProperty.SetValue(
                                                item,
                                                ControlType.ThreeStateSlider
                                            );
                                            _logService.Log(
                                                LogLevel.Debug,
                                                $"Forced ControlType to ThreeStateSlider for item {itemName}"
                                            );
                                        }
                                    }

                                    // Also try to update the parent view model's UacLevel property
                                    try
                                    {
                                        // Try to get the parent view model
                                        var viewModelProperty = itemType.GetProperty("ViewModel");
                                        if (viewModelProperty != null)
                                        {
                                            var viewModel = viewModelProperty.GetValue(item);
                                            if (viewModel != null)
                                            {
                                                var viewModelType = viewModel.GetType();
                                                var viewModelUacLevelProperty =
                                                    viewModelType.GetProperty("SelectedUacLevel");
                                                if (viewModelUacLevelProperty != null)
                                                {
                                                    viewModelUacLevelProperty.SetValue(
                                                        viewModel,
                                                        newSliderValue
                                                    );
                                                    anyPropertyUpdated = true;
                                                    _logService.Log(
                                                        LogLevel.Debug,
                                                        $"Updated SelectedUacLevel on parent view model to {newSliderValue}"
                                                    );
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logService.Log(
                                            LogLevel.Debug,
                                            $"Error updating parent view model UacLevel: {ex.Message}"
                                        );
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.Log(
                                LogLevel.Debug,
                                $"Error updating SliderValue for item {itemName}: {ex.Message}"
                            );
                        }
                    }
                }

                // For non-theme items, also check for SelectedValue property
                if (!isThemeSelector)
                {
                    var selectedValueProperty = itemType.GetProperty("SelectedValue");
                    if (selectedValueProperty != null)
                    {
                        try
                        {
                            string currentSelectedValue = selectedValueProperty
                                .GetValue(item)
                                ?.ToString();
                            string newSelectedValue = null;

                            // First try to get the value directly from configItem.SelectedValue
                            if (!string.IsNullOrEmpty(configItem.SelectedValue))
                            {
                                newSelectedValue = configItem.SelectedValue;
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Found SelectedValue in config: {newSelectedValue} for item {itemName}"
                                );
                            }
                            // Then try to get the value from CustomProperties.SelectedTheme
                            else if (
                                configItem.CustomProperties.TryGetValue(
                                    "SelectedTheme",
                                    out var selectedTheme
                                )
                                && selectedTheme != null
                            )
                            {
                                newSelectedValue = selectedTheme.ToString();
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Found SelectedTheme in CustomProperties: {newSelectedValue} for item {itemName}"
                                );
                            }
                            // Finally try to map from SliderValue using SliderLabels
                            else if (
                                configItem.CustomProperties.TryGetValue(
                                    "SliderValue",
                                    out var configSliderValueForMapping
                                )
                            )
                            {
                                var sliderLabelsProperty = itemType.GetProperty("SliderLabels");
                                if (sliderLabelsProperty != null)
                                {
                                    var sliderLabels =
                                        sliderLabelsProperty.GetValue(item)
                                        as System.Collections.IList;
                                    int sliderValueInt = Convert.ToInt32(
                                        configSliderValueForMapping
                                    );
                                    if (sliderLabels != null && sliderValueInt < sliderLabels.Count)
                                    {
                                        newSelectedValue = sliderLabels[sliderValueInt]?.ToString();
                                        _logService.Log(
                                            LogLevel.Info,
                                            $"Mapped SliderValue {sliderValueInt} to SelectedValue {newSelectedValue} for item {itemName}"
                                        );
                                    }
                                }
                            }

                            if (
                                !string.IsNullOrEmpty(newSelectedValue)
                                && currentSelectedValue != newSelectedValue
                            )
                            {
                                selectedValueProperty.SetValue(item, newSelectedValue);
                                anyPropertyUpdated = true;
                                _logService.Log(
                                    LogLevel.Info,
                                    $"Updated SelectedValue for item {itemName} from {currentSelectedValue} to {newSelectedValue}"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.Log(
                                LogLevel.Debug,
                                $"Error updating SelectedValue for item {itemName}: {ex.Message}"
                            );
                        }
                    }
                }

                // For toggle switches, ensure IsChecked is synchronized with IsSelected
                var isCheckedProperty = itemType.GetProperty("IsChecked");
                if (isCheckedProperty != null)
                {
                    try
                    {
                        bool currentIsChecked = (bool)(isCheckedProperty.GetValue(item) ?? false);

                        if (currentIsChecked != configItem.IsSelected)
                        {
                            isCheckedProperty.SetValue(item, configItem.IsSelected);
                            anyPropertyUpdated = true;
                            _logService.Log(
                                LogLevel.Debug,
                                $"Updated IsChecked for item {itemName} to {configItem.IsSelected}"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(
                            LogLevel.Debug,
                            $"Error updating IsChecked for item {itemName}: {ex.Message}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Log(
                    LogLevel.Debug,
                    $"Error in UpdateAdditionalProperties: {ex.Message}"
                );
            }

            return anyPropertyUpdated;
        }

        /// <summary>
        /// Helper method to try updating SelectedValue from SliderLabels.
        /// </summary>
        /// <param name="settingType">The type of the setting.</param>
        /// <param name="setting">The setting object.</param>
        /// <param name="selectedValueProperty">The SelectedValue property.</param>
        /// <param name="newSliderValue">The new slider value.</param>
        private void TryUpdateFromSliderLabels(
            Type settingType,
            object setting,
            System.Reflection.PropertyInfo selectedValueProperty,
            int newSliderValue
        )
        {
            // Try to get the power plan label from SliderLabels
            var sliderLabelsProperty = settingType.GetProperty("SliderLabels");
            if (sliderLabelsProperty != null)
            {
                var sliderLabels =
                    sliderLabelsProperty.GetValue(setting) as System.Collections.IList;
                if (
                    sliderLabels != null
                    && newSliderValue >= 0
                    && newSliderValue < sliderLabels.Count
                )
                {
                    string powerPlanLabel = sliderLabels[newSliderValue]?.ToString();
                    if (!string.IsNullOrEmpty(powerPlanLabel))
                    {
                        _logService.Log(
                            LogLevel.Info,
                            $"Updating SelectedValue for Power Plan setting to {powerPlanLabel} from SliderLabels"
                        );
                        selectedValueProperty.SetValue(setting, powerPlanLabel);
                    }
                }
                else
                {
                    // Fall back to default values
                    string[] defaultLabels =
                    {
                        "Balanced",
                        "High Performance",
                        "Ultimate Performance",
                    };
                    if (newSliderValue >= 0 && newSliderValue < defaultLabels.Length)
                    {
                        _logService.Log(
                            LogLevel.Info,
                            $"Updating SelectedValue for Power Plan setting to {defaultLabels[newSliderValue]} from default labels"
                        );
                        selectedValueProperty.SetValue(setting, defaultLabels[newSliderValue]);
                    }
                }
            }
        }

        private void TriggerPropertyChangedIfPossible(object item)
        {
            try
            {
                // Check if the item implements INotifyPropertyChanged
                if (item is System.ComponentModel.INotifyPropertyChanged)
                {
                    try
                    {
                        // Use a safer approach to find property changed methods
                        var methods = item.GetType()
                            .GetMethods(
                                System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance
                            )
                            .Where(m =>
                                (m.Name == "RaisePropertyChanged" || m.Name == "OnPropertyChanged")
                                && m.GetParameters().Length == 1
                                && m.GetParameters()[0].ParameterType == typeof(string)
                            )
                            .ToList();

                        if (methods.Any())
                        {
                            var method = methods.First();
                            // Invoke the method with null string to refresh all properties
                            method.Invoke(item, new object[] { null });
                            return;
                        }

                        // If no method with string parameter is found, try to find a method that takes PropertyChangedEventArgs
                        var eventArgsMethods = item.GetType()
                            .GetMethods(
                                System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance
                            )
                            .Where(m =>
                                (m.Name == "OnPropertyChanged")
                                && m.GetParameters().Length == 1
                                && m.GetParameters()[0].ParameterType.Name
                                    == "PropertyChangedEventArgs"
                            )
                            .ToList();

                        if (eventArgsMethods.Any())
                        {
                            // Create PropertyChangedEventArgs with null property name to refresh all properties
                            var eventArgsType = eventArgsMethods
                                .First()
                                .GetParameters()[0]
                                .ParameterType;
                            var eventArgs = Activator.CreateInstance(
                                eventArgsType,
                                new object[] { null }
                            );
                            eventArgsMethods.First().Invoke(item, new[] { eventArgs });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(
                            LogLevel.Debug,
                            $"Error finding property changed method: {ex.Message}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Debug, $"Error triggering property changed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies registry settings for an item based on its IsSelected state.
        /// </summary>
        /// <param name="item">The item to apply registry settings for.</param>
        /// <param name="isSelected">Whether the setting is selected (enabled) or not.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ApplyRegistrySettingsAsync(object item, bool isSelected)
        {
            try
            {
                // Check if this is an ApplicationSettingItem
                if (item is ApplicationSettingItem settingItem)
                {
                    _logService.Log(
                        LogLevel.Debug,
                        $"Applying registry settings for {settingItem.Name}, IsSelected={isSelected}"
                    );

                    // Apply registry setting if present
                    if (settingItem.RegistrySetting != null)
                    {
                        string hiveString = GetRegistryHiveString(settingItem.RegistrySetting.Hive);
                        object valueToApply = isSelected
                            ? settingItem.RegistrySetting.EnabledValue
                                ?? settingItem.RegistrySetting.RecommendedValue
                            : settingItem.RegistrySetting.DisabledValue
                                ?? settingItem.RegistrySetting.DefaultValue;

                        // Ensure the key exists and set the value
                        string keyPath = $"{hiveString}\\{settingItem.RegistrySetting.SubKey}";

                        // First ensure the key exists
                        bool keyExists = _registryService.KeyExists(keyPath);
                        if (!keyExists)
                        {
                            _logService.Log(LogLevel.Info, $"Creating registry key: {keyPath}");
                            bool keyCreated = _registryService.CreateKeyIfNotExists(keyPath);
                            if (!keyCreated)
                            {
                                _logService.Log(
                                    LogLevel.Warning,
                                    $"Failed to create registry key: {keyPath}, trying direct registry access"
                                );

                                // Try direct registry access to create the key
                                try
                                {
                                    RegistryKey rootKey = GetRegistryRootKey(
                                        settingItem.RegistrySetting.Hive
                                    );
                                    if (rootKey != null)
                                    {
                                        rootKey.CreateSubKey(
                                            settingItem.RegistrySetting.SubKey,
                                            true
                                        );
                                        _logService.Log(
                                            LogLevel.Success,
                                            $"Successfully created registry key using direct access: {keyPath}"
                                        );
                                        keyCreated = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logService.Log(
                                        LogLevel.Warning,
                                        $"Failed to create registry key using direct access: {keyPath}, Error: {ex.Message}"
                                    );
                                }

                                // If direct access failed, try using PowerShell
                                if (!keyCreated)
                                {
                                    try
                                    {
                                        string psCommand =
                                            $"$ErrorActionPreference = 'Stop'; try {{ if (-not (Test-Path -Path '{keyPath.Replace("HKLM", "HKLM:")}')) {{ New-Item -Path '{keyPath.Replace("HKLM", "HKLM:")}' -Force | Out-Null; Write-Output 'Key created successfully' }} }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";

                                        _logService.Log(
                                            LogLevel.Info,
                                            $"Attempting to create registry key using PowerShell: {keyPath}"
                                        );

                                        // Execute PowerShell command
                                        var process = new System.Diagnostics.Process
                                        {
                                            StartInfo = new System.Diagnostics.ProcessStartInfo
                                            {
                                                FileName = "powershell.exe",
                                                Arguments = $"-Command \"{psCommand}\"",
                                                UseShellExecute = false,
                                                RedirectStandardOutput = true,
                                                RedirectStandardError = true,
                                                CreateNoWindow = true,
                                            },
                                        };

                                        process.Start();
                                        string output = process.StandardOutput.ReadToEnd();
                                        string error = process.StandardError.ReadToEnd();
                                        process.WaitForExit();

                                        if (process.ExitCode == 0)
                                        {
                                            _logService.Log(
                                                LogLevel.Success,
                                                $"Successfully created registry key using PowerShell: {keyPath}"
                                            );
                                            keyCreated = true;
                                        }
                                        else
                                        {
                                            _logService.Log(
                                                LogLevel.Error,
                                                $"Failed to create registry key using PowerShell: {keyPath}, Error: {error}"
                                            );
                                        }
                                    }
                                    catch (Exception psEx)
                                    {
                                        _logService.Log(
                                            LogLevel.Error,
                                            $"PowerShell execution failed: {psEx.Message}"
                                        );
                                    }
                                }
                            }
                        }

                        // Now set the value - always attempt to set the value even if key creation failed
                        // This is important for configuration import to ensure values are applied
                        bool success = false;

                        // First try using the registry service
                        success = _registryService.SetValue(
                            keyPath,
                            settingItem.RegistrySetting.Name,
                            valueToApply,
                            settingItem.RegistrySetting.ValueType
                        );

                        if (success)
                        {
                            _logService.Log(
                                LogLevel.Info,
                                $"Applied registry setting for {settingItem.Name}: {(isSelected ? "Enabled" : "Disabled")}"
                            );
                        }
                        else
                        {
                            _logService.Log(
                                LogLevel.Warning,
                                $"Failed to apply registry setting for {settingItem.Name}, trying direct registry access"
                            );

                            // Try direct registry access as a fallback
                            try
                            {
                                RegistryKey rootKey = GetRegistryRootKey(
                                    settingItem.RegistrySetting.Hive
                                );
                                if (rootKey != null)
                                {
                                    using (
                                        RegistryKey regKey = rootKey.CreateSubKey(
                                            settingItem.RegistrySetting.SubKey,
                                            true
                                        )
                                    )
                                    {
                                        if (regKey != null)
                                        {
                                            regKey.SetValue(
                                                settingItem.RegistrySetting.Name,
                                                valueToApply,
                                                (RegistryValueKind)
                                                    settingItem.RegistrySetting.ValueType
                                            );
                                            _logService.Log(
                                                LogLevel.Success,
                                                $"Successfully applied registry setting using direct access: {keyPath}\\{settingItem.RegistrySetting.Name}"
                                            );
                                            success = true;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logService.Log(
                                    LogLevel.Error,
                                    $"Failed to apply registry setting using direct access: {ex.Message}"
                                );
                            }

                            // If direct access failed, try using PowerShell
                            if (!success)
                            {
                                try
                                {
                                    string psCommand =
                                        $"$ErrorActionPreference = 'Stop'; try {{ if (-not (Test-Path -Path '{keyPath.Replace("HKLM", "HKLM:")}')) {{ New-Item -Path '{keyPath.Replace("HKLM", "HKLM:")}' -Force | Out-Null; }} Set-ItemProperty -Path '{keyPath.Replace("HKLM", "HKLM:")}' -Name '{settingItem.RegistrySetting.Name}' -Value {valueToApply} -Type {settingItem.RegistrySetting.ValueType} -Force; }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";

                                    _logService.Log(
                                        LogLevel.Info,
                                        $"Attempting to set registry value using PowerShell: {keyPath}\\{settingItem.RegistrySetting.Name}"
                                    );

                                    // Execute PowerShell command
                                    var process = new System.Diagnostics.Process
                                    {
                                        StartInfo = new System.Diagnostics.ProcessStartInfo
                                        {
                                            FileName = "powershell.exe",
                                            Arguments = $"-Command \"{psCommand}\"",
                                            UseShellExecute = false,
                                            RedirectStandardOutput = true,
                                            RedirectStandardError = true,
                                            CreateNoWindow = true,
                                        },
                                    };

                                    process.Start();
                                    string output = process.StandardOutput.ReadToEnd();
                                    string error = process.StandardError.ReadToEnd();
                                    process.WaitForExit();

                                    if (process.ExitCode == 0)
                                    {
                                        _logService.Log(
                                            LogLevel.Success,
                                            $"Successfully set registry value using PowerShell: {keyPath}\\{settingItem.RegistrySetting.Name}"
                                        );
                                    }
                                    else
                                    {
                                        _logService.Log(
                                            LogLevel.Error,
                                            $"Failed to set registry value using PowerShell: {keyPath}\\{settingItem.RegistrySetting.Name}, Error: {error}"
                                        );
                                    }
                                }
                                catch (Exception psEx)
                                {
                                    _logService.Log(
                                        LogLevel.Error,
                                        $"PowerShell execution failed: {psEx.Message}"
                                    );
                                }
                            }
                        }
                    }
                    // Apply linked registry settings if present
                    else if (
                        settingItem.LinkedRegistrySettings != null
                        && settingItem.LinkedRegistrySettings.Settings.Count > 0
                    )
                    {
                        _logService.Log(
                            LogLevel.Info,
                            $"Applying linked registry settings for {settingItem.Name}, IsSelected={isSelected}"
                        );

                        // Use the registry service to apply linked settings
                        bool success = await _registryService.ApplyLinkedSettingsAsync(
                            settingItem.LinkedRegistrySettings,
                            isSelected
                        );

                        if (success)
                        {
                            _logService.Log(
                                LogLevel.Success,
                                $"Successfully applied all linked registry settings for {settingItem.Name}"
                            );
                        }
                        else
                        {
                            _logService.Log(
                                LogLevel.Warning,
                                $"Some linked registry settings for {settingItem.Name} failed to apply"
                            );
                        }
                    }

                    // Refresh the status of the setting after applying changes
                    await settingItem.RefreshStatus();
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error applying registry settings: {ex.Message}");
                _logService.Log(LogLevel.Debug, $"Exception details: {ex}");
            }
        }

        /// <summary>
        /// Gets the registry root key for a given hive.
        /// </summary>
        /// <param name="hive">The registry hive.</param>
        /// <returns>The registry root key.</returns>
        private RegistryKey GetRegistryRootKey(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.ClassesRoot:
                    return Registry.ClassesRoot;
                case RegistryHive.CurrentUser:
                    return Registry.CurrentUser;
                case RegistryHive.LocalMachine:
                    return Registry.LocalMachine;
                case RegistryHive.Users:
                    return Registry.Users;
                case RegistryHive.CurrentConfig:
                    return Registry.CurrentConfig;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets the registry hive string.
        /// </summary>
        /// <param name="hive">The registry hive.</param>
        /// <returns>The registry hive string.</returns>
        private string GetRegistryHiveString(RegistryHive hive)
        {
            return hive switch
            {
                RegistryHive.ClassesRoot => "HKCR",
                RegistryHive.CurrentUser => "HKCU",
                RegistryHive.LocalMachine => "HKLM",
                RegistryHive.Users => "HKU",
                RegistryHive.CurrentConfig => "HKCC",
                _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
            };
        }
    }
}
