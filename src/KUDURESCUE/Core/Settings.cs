// KUDURESCUE's settings, kept in the registry for the current user.
using System;
using Microsoft.Win32;

namespace Kudurescue.Core
{
    /// <summary>How the app looks: like Windows, always light, or always dark.</summary>
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        /// <summary>Where the settings are kept, under HKEY_CURRENT_USER.</summary>
        private const string KeyPath = @"Software\KUDURESCUE";

        /// <summary>A setting as text, or fallback when it isn't there or can't be read.</summary>
        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Keeps a setting (quietly does nothing when the registry can't be written).</summary>
        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        /// <summary>A yes/no setting (kept as "1" or "0").</summary>
        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        /// <summary>Keeps a yes/no setting.</summary>
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        /// <summary>A setting that is one of a list of choices (an enum).</summary>
        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>The last folder recovered files were saved to (its parent is suggested next time).</summary>
        public static string LastDestination
        {
            get => Get("LastDestination", string.Empty);
            set => Set("LastDestination", value);
        }

        public static bool KeepFolders
        {
            get => GetBool("KeepFolders", true);
            set => SetBool("KeepFolders", value);
        }

        /// <summary>"Recover deleted files with KUDURESCUE" on drives' right-click menu. Turned on by the installer when chosen.</summary>
        public static bool ShellMenu
        {
            get => GetBool("ShellMenu", false);
            set => SetBool("ShellMenu", value);
        }

        public static bool RecoverPartial
        {
            get => GetBool("RecoverPartial", true);
            set => SetBool("RecoverPartial", value);
        }
    }
}
