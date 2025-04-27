using System.Threading.Tasks;

namespace Winhance.Core.Features.Customize.Interfaces
{
    /// <summary>
    /// Interface for theme-related operations.
    /// </summary>
    public interface IThemeService
    {
        /// <summary>
        /// Checks if dark mode is enabled.
        /// </summary>
        /// <returns>True if dark mode is enabled; otherwise, false.</returns>
        bool IsDarkModeEnabled();

        /// <summary>
        /// Sets the theme mode.
        /// </summary>
        /// <param name="isDarkMode">True to enable dark mode; false to enable light mode.</param>
        /// <returns>True if the operation succeeded; otherwise, false.</returns>
        bool SetThemeMode(bool isDarkMode);

        /// <summary>
        /// Sets the theme mode and optionally changes the wallpaper.
        /// </summary>
        /// <param name="isDarkMode">True to enable dark mode; false to enable light mode.</param>
        /// <param name="changeWallpaper">True to change the wallpaper; otherwise, false.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task<bool> ApplyThemeAsync(bool isDarkMode, bool changeWallpaper);

        /// <summary>
        /// Gets the name of the current theme.
        /// </summary>
        /// <returns>The name of the current theme ("Light Mode" or "Dark Mode").</returns>
        string GetCurrentThemeName();

        /// <summary>
        /// Refreshes the Windows GUI to apply theme changes.
        /// </summary>
        /// <param name="restartExplorer">True to restart Explorer; otherwise, false.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task<bool> RefreshGUIAsync(bool restartExplorer);
    }
}