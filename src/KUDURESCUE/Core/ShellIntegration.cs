using System;
using Microsoft.Win32;

namespace Kudurescue.Core
{
    /// <summary>
    /// "Recover deleted files with KUDURESCUE" on a drive's right-click menu in File Explorer. Registered for the current
    /// user only (HKEY_CURRENT_USER), so no administrator rights are needed. On Windows 11 it's under "Show more options".
    /// </summary>
    public static class ShellIntegration
    {
        /// <summary>Where the right-click menu entry for drives is kept.</summary>
        private const string Key = @"Software\Classes\Drive\shell\KUDURESCUE";

        /// <summary>Adds the right-click menu entry for drives, for this user only.</summary>
        public static void Register()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                {
                    key.SetValue(string.Empty, "Recover deleted files with KUDURESCUE");
                    key.SetValue("Icon", "\"" + AppInfo.ExePath + "\",0");
                    using (var command = key.CreateSubKey("command"))
                        command.SetValue(string.Empty, $"\"{AppInfo.ExePath}\" --drive \"%1\"");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        /// <summary>Takes the menu entry away again.</summary>
        public static void Remove()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(Key, false); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        public static bool IsRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(Key + @"\command")) return key != null; }
                catch { return false; }
            }
        }

        /// <summary>Keeps the menu in step with the setting, and pointing at this copy of KUDURESCUE.</summary>
        public static void Refresh()
        {
            if (Settings.ShellMenu) Register();
            else if (IsRegistered) Remove();
        }
    }
}
