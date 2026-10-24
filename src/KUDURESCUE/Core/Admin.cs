using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Kudurescue.Core
{
    public static class Admin
    {
        private static bool? _isAdmin;

        public static bool IsAdmin
        {
            get
            {
                if (_isAdmin == null)
                {
                    try
                    {
                        using (var identity = WindowsIdentity.GetCurrent())
                            _isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                    catch
                    {
                        _isAdmin = false;
                    }
                }
                return _isAdmin.Value;
            }
        }

        /// <summary>Starts a new copy of KUDURESCUE as administrator. Returns false if the user said no.</summary>
        public static bool RestartElevated(IEnumerable<string> args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppInfo.ExePath, Args.Join(args))
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception)
            {
                return false; // The Windows prompt was cancelled.
            }
        }
    }
}
