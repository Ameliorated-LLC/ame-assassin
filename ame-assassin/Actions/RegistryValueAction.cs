#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ame_assassin;
using Microsoft.Win32;
using TrustedUninstaller.Shared.Tasks;


namespace TrustedUninstaller.Shared.Actions
{
    internal enum RegistryValueOperation
    {
        Delete = 0,
        Add = 1,
        // This indicates to skip the action if the specified value does not already exist
        Set = 2
    }

    internal enum RegistryValueType
    {
        REG_SZ = RegistryValueKind.String,
        REG_MULTI_SZ = RegistryValueKind.MultiString,
        REG_EXPAND_SZ = RegistryValueKind.ExpandString,
        REG_DWORD = RegistryValueKind.DWord,
        REG_QWORD = RegistryValueKind.QWord,
        REG_BINARY = RegistryValueKind.Binary,
        REG_NONE = RegistryValueKind.None,
        REG_UNKNOWN = RegistryValueKind.Unknown
    }

    class RegistryValueAction : ITaskAction
    {
        public string KeyName { get; set; }

        public string Value { get; set; } = "";
        
        public object? Data { get; set; }
        
        public RegistryValueType Type { get; set; }
        
        public Scope Scope { get; set; } = Scope.AllUsers;

        public RegistryValueOperation Operation { get; set; } = RegistryValueOperation.Add;
        
        public int ProgressWeight { get; set; } = 0;
        public int GetProgressWeight()
        {
            int roots;
            try
            {
                roots = GetRoots().Count;
            }
            catch (Exception)
            {
                roots = 1;
            }

            return ProgressWeight + roots;
        }
        
        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;
        
        public string ErrorString() => $"RegistryValueAction failed to {Operation.ToString().ToLower()} value '{Value}' in key '{KeyName}'";
        
        private List<RegistryKey> GetRoots()
        {
            var hive = KeyName.Split('\\').GetValue(0).ToString().ToUpper();
            var list = new List<RegistryKey>();

            if (hive.Equals("HKCU") || hive.Equals("HKEY_CURRENT_USER"))
            {
                RegistryKey usersKey;
                List<string> userKeys;

                switch (Scope)
                {
                    case Scope.AllUsers:
                        RegistryManager.HookUserHives();
                    
                        usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                        userKeys = usersKey.GetSubKeyNames().
                            Where(x => x.StartsWith("S-") && 
                                usersKey.OpenSubKey(x).GetSubKeyNames().Any(y => y.Equals("Volatile Environment"))).ToList();
                    
                        userKeys.AddRange(usersKey.GetSubKeyNames().Where(x => x.StartsWith("AME_UserHive_")).ToList());
                    
                        userKeys.ForEach(x => list.Add(usersKey.OpenSubKey(x, true)));
                        return list;
                    case Scope.ActiveUsers:
                        usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                        userKeys = usersKey.GetSubKeyNames().
                            Where(x => x.StartsWith("S-") && 
                                usersKey.OpenSubKey(x).GetSubKeyNames().Any(y => y.Equals("Volatile Environment"))).ToList();

                        userKeys.ForEach(x => list.Add(usersKey.OpenSubKey(x, true)));
                        return list;
                    case Scope.DefaultUser:
                        usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                        userKeys = usersKey.GetSubKeyNames().Where(x => x.Equals("AME_UserHive_Default")).ToList();
                        
                        userKeys.ForEach(x => list.Add(usersKey.OpenSubKey(x, true)));
                        return list;
                }
            }
            list.Add(hive switch
            {
                "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
                "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
                "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default),
                "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default),
                "HKCR" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default),
                "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default),
                "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default),
                "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default),
                _ => throw new ArgumentException($"Key '{KeyName}' does not specify a valid registry hive.")
            });
            return list;
        }

        public string GetSubKey() => KeyName.Substring(KeyName.IndexOf("\\") + 1);

        private RegistryKey? OpenSubKey(RegistryKey root)
        {
            var subKeyPath = GetSubKey();
            
            if (subKeyPath == null) throw new ArgumentException($"Key '{KeyName}' is invalid.");
            
            return root.OpenSubKey(subKeyPath, true);
        }

        public object? GetCurrentValue(RegistryKey root)
        {
            var subkey = GetSubKey();
            return Registry.GetValue(root.Name + "\\" + subkey, Value, null);
        }

        public UninstallTaskStatus GetStatus()
        {
            var roots = GetRoots();

            foreach (var root in roots)
            {
                try
                {
                    var subKey = root.OpenSubKey(KeyName);
                    
                    if (subKey == null && Operation != RegistryValueOperation.Set) return UninstallTaskStatus.ToDo;
                    
                    if (Operation == RegistryValueOperation.Delete && subKey.GetValue(Value) != null)
                    {
                        return UninstallTaskStatus.ToDo;
                    }
                    
                    if (Operation == RegistryValueOperation.Add && Data.ToString() != subKey.GetValue(Value).ToString())
                    {
                        return UninstallTaskStatus.ToDo;
                    }
                }
                catch (SecurityException)
                {
                    return UninstallTaskStatus.ToDo;
                }
            }
            return UninstallTaskStatus.Completed;
        }

        public void RunTask()
        {
            Console.WriteLine($"{Operation.ToString().TrimEnd('e')}ing value '{Value}' in key '{KeyName}'...");
            
            var roots = GetRoots();

            foreach (var _root in roots)
            {
                var root = _root;
                var subKey = GetSubKey();

                if (root.Name.StartsWith("AME_UserHive_") && subKey.StartsWith("Software\\Classes", StringComparison.CurrentCultureIgnoreCase))
                {
                    var usersKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);

                    root = usersKey.OpenSubKey(root.Name + "_Classes", true);
                    subKey = Regex.Replace(subKey, @"Software\\Classes", "", RegexOptions.IgnoreCase);

                    if (root == null)
                    {
                        Console.WriteLine($"\r\nError: User classes hive not found for hive {_root.Name}.");
                        continue;
                    }
                }
                
                if (GetCurrentValue(root) == Data) continue;

                if (root.OpenSubKey(subKey) == null && Operation == RegistryValueOperation.Set) continue;
                if (root.OpenSubKey(subKey) == null && Operation == RegistryValueOperation.Add) root.CreateSubKey(subKey);

                if (Operation == RegistryValueOperation.Delete)
                {
                    var key = root.OpenSubKey(subKey, true);
                    key?.DeleteValue(Value);
                    continue;
                }

                if (Type == RegistryValueType.REG_BINARY)
                {
                    Data = Data.ToString().Split(' ').Select(s => Convert.ToByte(s, 16)).ToArray();

                    Registry.SetValue(root.Name + "\\" + subKey, Value, Data, (RegistryValueKind)Type);
                }
                else
                {
                    Registry.SetValue(root.Name + "\\" + subKey, Value, Data, (RegistryValueKind)Type);
                }
            }
            return;
        }
    }
}
