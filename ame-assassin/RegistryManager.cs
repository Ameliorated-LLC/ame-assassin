using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ame_assassin
{
    public class RegistryManager
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegSaveKey(IntPtr hKey, string lpFile, uint securityAttrPtr = 0);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern IntPtr RtlAdjustPrivilege(int Privilege, bool bEnablePrivilege, bool IsThreadPrivilege, out bool PreviousValue);

        [DllImport("advapi32.dll")]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, ref UInt64 lpLuid);

        [DllImport("advapi32.dll")]
        static extern bool LookupPrivilegeValue(IntPtr lpSystemName, string lpName, ref UInt64 lpLuid);

        public static void LoadFromFile(string path, bool classHive = false)
        {
            var parentKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            string name;

            if (path.Contains("Users\\Default\\")) name = classHive ? "AME_UserHive_Default_Classes" : "AME_UserHive_Default";
            else name = classHive ? "AME_UserHive_" + (HivesLoaded) + "_Classes" : "AME_UserHive_" + (HivesLoaded + 1);

            IntPtr parentHandle = parentKey.Handle.DangerousGetHandle();
            RegLoadKey(parentHandle, name, path);
            if (!path.Contains("Users\\Default\\"))
                HivesLoaded++;
        }
        public static void LoadFromFile(string path, string name)
        {
            var parentKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);

            IntPtr parentHandle = parentKey.Handle.DangerousGetHandle();
            RegLoadKey(parentHandle, name, path);
            HivesLoaded++;
        }

        private static void AcquirePrivileges()
        {
            ulong luid = 0;
            bool throwaway;
            LookupPrivilegeValue(IntPtr.Zero, "SeRestorePrivilege", ref luid);
            RtlAdjustPrivilege((int)luid, true, false, out throwaway);
            LookupPrivilegeValue(IntPtr.Zero, "SeBackupPrivilege", ref luid);
            RtlAdjustPrivilege((int)luid, true, false, out throwaway);
        }

        private static void ReturnPrivileges()
        {
            ulong luid = 0;
            bool throwaway;
            LookupPrivilegeValue(IntPtr.Zero, "SeRestorePrivilege", ref luid);
            RtlAdjustPrivilege((int)luid, false, false, out throwaway);
            LookupPrivilegeValue(IntPtr.Zero, "SeBackupPrivilege", ref luid);
            RtlAdjustPrivilege((int)luid, false, false, out throwaway);
        }

        private static bool HivesHooked;
        private static int HivesLoaded;

        private static bool ComponentsHiveHooked = false;

        public static void HookComponentsHive()
        {
            if (ComponentsHiveHooked || RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default).GetSubKeyNames().Any(x => x.StartsWith("AME_ComponentsHive"))) return;
            ComponentsHiveHooked = true;
            try
            {
                if (!File.Exists(Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\config\COMPONENTS")))
                {
                    Console.WriteLine("\r\nError: Error attempting to load components registry hive.\r\nException: " + $"COMPONENTS file not found in config foler.");
                    return;
                }
                AcquirePrivileges();
                LoadFromFile(Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\config\COMPONENTS"), "AME_ComponentsHive");
                ReturnPrivileges();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to mount components hive.\r\nException: " + e.Message);
            }
        }
        
        public static void UnhookComponentsHive()
        {
            if (!ComponentsHiveHooked) return;
            try
            {
                var usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                var ComponentsHive = usersKey.GetSubKeyNames().Where(x => x.Equals("AME_ComponentsHive")).FirstOrDefault();

                if (ComponentsHive != null)
                {
                    AcquirePrivileges();
                    RegUnLoadKey(usersKey.Handle.DangerousGetHandle(), ComponentsHive);
                    ReturnPrivileges();
                }

                usersKey.Close();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to unmount components hive.\r\nException: " + e.Message);
            }
        }
        
        private static bool DriversHiveHooked = false;

        public static void HookDriversHive()
        {
            if (DriversHiveHooked || RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default).GetSubKeyNames().Any(x => x.StartsWith("AME_DriversHive"))) return;
            DriversHiveHooked = true;
            try
            {
                if (!File.Exists(Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\config\DRIVERS")))
                {
                    Console.WriteLine("\r\nError: Error attempting to load drivers registry hive.\r\nException: " + $"DRIVERS file not found in config foler.");
                    return;
                }

                AcquirePrivileges();
                LoadFromFile(Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\config\DRIVERS"), "AME_DriversHive");
                ReturnPrivileges();
                Console.ReadLine();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to mount drivers hive.\r\nException: " + e.Message);
            }
        }

        public static void UnhookDriversHive()
        {
            if (!DriversHiveHooked) return;
            try
            {
                var usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                var DriversHive = usersKey.GetSubKeyNames().Where(x => x.Equals("AME_DriversHive")).FirstOrDefault();

                if (DriversHive != null)
                {
                    AcquirePrivileges();
                    RegUnLoadKey(usersKey.Handle.DangerousGetHandle(), DriversHive);
                    ReturnPrivileges();
                }

                usersKey.Close();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to unmount drivers hive.\r\nException: " + e.Message);
            }
        }

        public static void HookUserHives()
        {
            try
            {
                if (HivesHooked || RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default).GetSubKeyNames().Any(x => x.StartsWith("AME_UserHive_"))) return;
                HivesHooked = true;

                var usersDir = Environment.GetEnvironmentVariable("SYSTEMDRIVE") + "\\Users";

                var ignoreList = new List<string>()
                { "Default User", "Public", "All Users" };
                var userDirs = Directory.GetDirectories(usersDir).Where(x => !ignoreList.Contains(x.Split('\\').Last())).ToList();

                if (userDirs.Any()) AcquirePrivileges();
                foreach (var userDir in userDirs)
                {
                    if (!File.Exists($"{userDir}\\NTUSER.DAT"))
                    {
                        Console.WriteLine("\r\nError: Error attempting to load user registry hive.\r\nException: " + $"NTUSER.DAT file not found in user folder '{userDir}'.");
                        continue;
                    }

                    LoadFromFile($"{userDir}\\NTUSER.DAT");

                    if (userDir.EndsWith("\\Default"))
                    {
                        continue;
                    }

                    if (!File.Exists($@"{userDir}\AppData\Local\Microsoft\Windows\UsrClass.dat"))
                    {
                        Console.WriteLine($"\r\nError: Error attempting to load user classes registry hive.\r\nUsrClass.dat file not found in user appdata folder '{userDir}\\AppData\\Local\\Microsoft\\Windows'.");
                        continue;
                    }

                    LoadFromFile($@"{userDir}\AppData\Local\Microsoft\Windows\UsrClass.dat", true);
                }

                if (userDirs.Any()) ReturnPrivileges();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to mount user hives.\r\nException: " + e.Message);
            }
        }

        public static void UnhookUserHives()
        {
            try
            {
                if (!HivesHooked) return;

                var usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                var userHives = usersKey.GetSubKeyNames().Where(x => x.StartsWith("AME_UserHive_")).ToList();

                if (userHives.Any()) AcquirePrivileges();
                foreach (var userHive in userHives)
                {
                    RegUnLoadKey(usersKey.Handle.DangerousGetHandle(), userHive);
                }

                if (userHives.Any()) ReturnPrivileges();

                usersKey.Close();
            } catch (Exception e)
            {
                Console.WriteLine("\r\nError: Critical error while attempting to unmount user hives.\r\nException: " + e.Message);
            }
        }
    }
}