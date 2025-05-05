using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winhance.Core.Features.Common.Interfaces;
using Winhance.Core.Features.Common.Models;
using Winhance.Core.Features.Common.Enums;
using Winhance.Core.Features.Common.Extensions;
using Winhance.Core.Features.Customize.Enums;
using Winhance.Core.Features.Customize.Models;
using Winhance.WPF.Features.Common.ViewModels;
using Winhance.WPF.Features.Common.Interfaces;
using Winhance.WPF.Features.Common.Models;
using Microsoft.Win32;
using Winhance.Infrastructure.Features.Common.Registry;
using Winhance.WPF.Features.Common.Extensions;

namespace Winhance.WPF.Features.Customize.ViewModels
{
    /// <summary>
    /// ViewModel for Taskbar customizations.
    /// </summary>
    public partial class TaskbarCustomizationsViewModel : BaseSettingsViewModel<ApplicationSettingItem>
    {
        private readonly ISystemServices _systemServices;
        private bool _isWindows11;

        /// <summary>
        /// Gets the command to clean the taskbar.
        /// </summary>
        public IAsyncRelayCommand CleanTaskbarCommand { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskbarCustomizationsViewModel"/> class.
        /// </summary>
        /// <param name="progressService">The task progress service.</param>
        /// <param name="registryService">The registry service.</param>
        /// <param name="logService">The log service.</param>
        /// <param name="systemServices">The system services.</param>
        public TaskbarCustomizationsViewModel(
            ITaskProgressService progressService,
            IRegistryService registryService,
            ILogService logService,
            ISystemServices systemServices)
            : base(progressService, registryService, logService)
        {
            _systemServices = systemServices ?? throw new ArgumentNullException(nameof(systemServices));
            _isWindows11 = _systemServices.IsWindows11();
            
            // Initialize the CleanTaskbarCommand
            CleanTaskbarCommand = new AsyncRelayCommand(ExecuteCleanTaskbarAsync);
        }

        /// <summary>
        /// Executes the clean taskbar operation.
        /// </summary>
        private async Task ExecuteCleanTaskbarAsync()
        {
            try
            {
                _progressService.StartTask("Cleaning taskbar...");
                _logService.LogInformation("Cleaning taskbar started");
                
                // Call the static method from TaskbarCustomizations class
                await TaskbarCustomizations.CleanTaskbar(_systemServices, _logService);
                
                // Update the status text and complete the task
                _progressService.UpdateProgress(100, "Taskbar cleaned successfully!");
                _progressService.CompleteTask();
                _logService.LogInformation("Taskbar cleaned successfully");
            }
            catch (Exception ex)
            {
                _logService.LogError($"Error cleaning taskbar: {ex.Message}", ex);
                // Update the status text with the error message
                _progressService.UpdateProgress(0, $"Error cleaning taskbar: {ex.Message}");
                _progressService.CancelCurrentTask();
            }
        }

        /// <summary>
        /// Loads the Taskbar customizations.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public override async Task LoadSettingsAsync()
        {
            try
            {
                IsLoading = true;

                // Clear existing settings
                Settings.Clear();

                // Load Taskbar customizations
                var taskbarCustomizations = Core.Features.Customize.Models.TaskbarCustomizations.GetTaskbarCustomizations();
                if (taskbarCustomizations?.Settings != null)
                {
                    // Add settings sorted alphabetically by name
                    foreach (var setting in taskbarCustomizations.Settings.OrderBy(s => s.Name))
                    {
                        // Skip Windows 11 specific settings on Windows 10
                        if (!_isWindows11 && setting.IsWindows11Only)
                        {
                            continue;
                        }

                        // Skip Windows 10 specific settings on Windows 11
                        if (_isWindows11 && setting.IsWindows10Only)
                        {
                            continue;
                        }

                        // Create ApplicationSettingItem directly
                        var settingItem = new ApplicationSettingItem(_registryService, null, _logService)
                        {
                            Id = setting.Id,
                            Name = setting.Name,
                            Description = setting.Description,
                            ControlType = setting.ControlType,
                            IsWindows11Only = setting.IsWindows11Only,
                            IsWindows10Only = setting.IsWindows10Only
                        };

                        // Add any actions
                        var actionsProperty = setting.GetType().GetProperty("Actions");
                        if (actionsProperty != null && 
                            actionsProperty.GetValue(setting) is IEnumerable<object> actions && 
                            actions.Any())
                        {
                            // We need to handle this differently since the Actions property doesn't exist in ApplicationSetting
                            // This is a temporary workaround until we refactor the code properly
                        }

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
                        }
                        else
                        {
                            _logService.Log(LogLevel.Warning, $"No registry settings found for {setting.Name}");
                        }

                        Settings.Add(settingItem);
                    }

                    // Set up property change handlers for settings
                    foreach (var setting in Settings)
                    {
                        setting.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName == nameof(ApplicationSettingItem.IsSelected))
                            {
                                UpdateIsSelectedState();
                            }
                        };
                    }
                }

                // Check setting statuses
                await CheckSettingStatusesAsync();
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
