using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using TrustedUninstaller.Shared.Actions;
using TrustedUninstaller.Shared.Tasks;

namespace ame_assassin
{
    internal class RegistryValue
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }
    
    public static partial class SystemPackage
    {
        internal enum Architecture
        {
            All = 0,
            amd64 = 1,
            wow64 = 2,
            x86 = 3,
            msil = 4
        }

        private static void FetchWCP()
        {
            Console.WriteLine($@"Fetching required dll...");
            List<string> wcpFiles = new List<string>();

            var serviceDirs = Directory.EnumerateDirectories(Environment.ExpandEnvironmentVariables("%WINDIR%\\WinSxS"), "amd64_*servicingstack*");
            serviceDirs.ToList().ForEach(x => wcpFiles.AddRange(Directory.GetFiles(x, "wcp.dll", SearchOption.AllDirectories)));
                    
            var wcp = wcpFiles.OrderByDescending(x => new FileInfo(x).LastWriteTime).FirstOrDefault();
            if (wcp == null) throw new FileNotFoundException("Could not locate any wcp.dll file within WinSxS.", "wcp.dll");

            File.Copy(wcp, HelperDir + "\\" + Path.GetFileName(wcp), true);
        }

        private static List<AssemblyIdentity> dependentsRemoved = new List<AssemblyIdentity>();
        private static void RemoveItemsFromManifest(ParsedXML parsedXml, bool isDependent = false)
        {
            foreach (var dependent in parsedXml.Dependents.Where(x => !ExcludeDependentsList.Contains(x.Name)))
            {
                if (dependentsRemoved.Any(x => x.IdentityMatches(dependent))) continue;
                
                Console.WriteLine($"\r\nDependent package {dependent.Name} found...");
                var manifests = Manifest.FindManifestsFromIdentity(dependent);
                if (manifests.Count == 0)
                {
                    Console.WriteLine($"\r\nError: No package found that matches package dependent '{dependent.Name}'.");
                }

                foreach (var manifest in manifests)
                {
                    try
                    {
                        var parsed = Manifest.ParseManifest(manifest);

                        RemoveItemsFromManifest(parsed, true);
                        Console.WriteLine($@"Removing dependent package {dependent.Name} data...");
                        RemoveManifestLinks(manifest);
                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not remove dependent package {dependent.Name}.\r\nException: " + e.Message);
                    }
                }

                dependentsRemoved.Add(dependent);
            }
            
            if (isDependent) Console.WriteLine($"\r\n--- Removing dependent package {parsedXml.Identity.Name}...");
            else Console.WriteLine($"\r\n--- Removing package {parsedXml.Identity.Name}...");

            var servList = parsedXml.Services.Except(RemovedServices).ToList();
            foreach (var service in servList)
            {
                if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil) break;
                
                try
                {
                    var servAction = new ServiceAction()
                    { ServiceName = service, Operation = ServiceOperation.Delete };

                    servAction.RunTask();
                    if (servAction.GetStatus() != UninstallTaskStatus.Completed) throw new Exception();
                } catch (Exception)
                {
                    try
                    {
                        var servAction = new ServiceAction()
                        { ServiceName = service, Operation = ServiceOperation.Delete, RegistryDelete = true };

                        servAction.RunTask();
                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not delete service '{service}'.\r\nException: " + e.Message);
                    }
                }
            }
            RemovedServices.AddRange(servList);

            var deviceList = parsedXml.Devices.Except(RemovedDevices).ToList();
            foreach (var device in deviceList)
            {
                if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil) break;
                
                try
                {
                    var servAction = new ServiceAction()
                    { ServiceName = device, Operation = ServiceOperation.Delete, Device = true };

                    servAction.RunTask();
                } catch (Exception)
                {
                    try
                    {
                        var servAction = new ServiceAction()
                        { ServiceName = device, Operation = ServiceOperation.Delete, RegistryDelete = true, Device = true };

                        servAction.RunTask();
                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not delete device '{device}'.\r\nException: " + e.Message);
                    }
                }
            }
            RemovedDevices.AddRange(deviceList);

            var fileList = parsedXml.Files.Except(RemovedFiles).Where(x => !parsedXml.Directories.Any(y => x.StartsWith(y, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.Length).ToList();
            foreach (var file in fileList)
            {
                try
                {
                    if (ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success))
                    {
                        continue;
                    }
                    
                    if (file.ContainsIC(Environment.ExpandEnvironmentVariables(@"%SYSTEMDRIVE%\Users\Default")))
                    {
                        var usersDir = Environment.GetEnvironmentVariable("SYSTEMDRIVE") + "\\Users";
                        
                        var ignoreList = new List<string>()
                        { "Default User", "Public", "All Users" };
                        var userDirs = Directory.GetDirectories(usersDir).Where(x => !ignoreList.Contains(x.Split('\\').Last())).ToList();

                        foreach (var userDir in userDirs)
                        {
                            var userFile = file.ReplaceIC(Environment.ExpandEnvironmentVariables(@"%SYSTEMDRIVE%\Users\Default"), userDir);
                            if (ExcludeList.Any(x => Regex.Match(userFile, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(userFile, x, RegexOptions.IgnoreCase).Success))
                            {
                                continue;
                            }
                            var fileAction = new FileAction()
                            { RawPath = userFile, };

                            fileAction.RunTask();

                            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(userFile)).Any())
                            {
                                var parentDirAction = new FileAction()
                                { RawPath = Path.GetDirectoryName(userFile), };
                            }
                        }
                    }
                    else
                    {
                        var fileAction = new FileAction()
                        { RawPath = file, };

                        fileAction.RunTask();
                        
                        if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(file)).Any())
                        {
                            new FileAction()
                            { RawPath = Path.GetDirectoryName(file), }.RunTask();
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete file '{file}'.\r\nException: " + e.Message);
                }
            }
            RemovedFiles.AddRange(fileList);

            var dirList = parsedXml.Directories.Except(RemovedDirectories).OrderByDescending(x => x.Length).ToList();
            foreach (var directory in dirList)
            {
                try
                {
                    if (ExcludeList.Any(x => Regex.Match(directory, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(directory, x, RegexOptions.IgnoreCase).Success))
                    {
                        continue;
                    }

                    var parentDir = Directory.GetParent(directory).FullName;

                    bool hadExclusion = false;
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        if (ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success))
                        {
                            hadExclusion = true;
                            continue;
                        }
                        new FileAction()
                        { RawPath = file }.RunTask();
                    }

                    if (!hadExclusion)
                    {
                        var directoryAction = new FileAction()
                        { RawPath = directory, };

                        directoryAction.RunTask();
                        
                        if (!Directory.EnumerateFileSystemEntries(parentDir).Any())
                        {
                            new FileAction()
                            { RawPath = parentDir, }.RunTask();
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete directory '{directory}'.\r\nException: " + e.Message);
                }
            }
            RemovedDirectories.AddRange(dirList);
            
            var regValList = parsedXml.RegistryValues.Except(RemovedRegistryValues).Where(x => !parsedXml.RegistryKeys.Any(y => x.Key.StartsWith(y, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.Key.Length).ToList();
            foreach (var registryValue in regValList)
            {
                try
                {
                    var registryValueAction = new RegistryValueAction()
                    { KeyName = registryValue.Key, Value = registryValue.Value, Operation = RegistryValueOperation.Delete };

                    registryValueAction.RunTask();

                    new RegistryKeyAction()
                    { KeyName = registryValue.Key, OnlyIfEmpty = true}.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete registry value '{registryValue.Key + "\\" + registryValue.Value}'.\r\nException: " + e.Message);
                }
            }
            RemovedRegistryValues.AddRange(regValList);

            var regKeyList = parsedXml.RegistryKeys.Except(RemovedRegistryKeys).OrderByDescending(x => x.Length).ToList();
            foreach (var registryKey in regKeyList)
            {
                try
                {
                    var registryKeyAction = new RegistryKeyAction()
                    { KeyName = registryKey, Operation = RegistryKeyOperation.Delete};

                    registryKeyAction.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete registry key '{registryKey}'.\r\nException: " + e.Message);
                }
            }
            RemovedRegistryKeys.AddRange(regKeyList);

            var taskList = parsedXml.ScheduledTasks.Except(RemovedScheduledTasks).ToList();
            foreach (var scheduledTask in parsedXml.ScheduledTasks)
            {
                if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil) break;
                
                try
                {
                    var scheduledTaskAction = new ScheduledTaskAction()
                    { Path = scheduledTask, Operation = ScheduledTaskOperation.Delete };

                    scheduledTaskAction.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete scheduled task '{scheduledTask}'.\r\nException: " + e.Message);
                }
            }
            RemovedScheduledTasks.AddRange(taskList);

            var evtProvList = parsedXml.EventProviders.Except(RemovedEventProviders).ToList();
            foreach (var eventProvider in evtProvList)
            {
                try
                {
                    var eventProviderAction = new RegistryKeyAction()
                    { KeyName = @"HKLM\Software\Microsoft\Windows\CurrentVersion\WINEVT\Publishers\" + eventProvider, Operation = RegistryKeyOperation.Delete };
                    
                    if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil)
                    {
                        eventProviderAction.KeyName = @"HKLM\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\WINEVT\Publishers\" + eventProvider;
                    }

                    eventProviderAction.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete event provider with GUID '{eventProvider}'.\r\nException: " + e.Message);
                }
            }
            RemovedEventProviders.AddRange(evtProvList);
            
            var evtChannelList = parsedXml.EventChannels.Except(RemovedEventChannels).ToList();
            foreach (var eventChannel in evtChannelList)
            {
                try
                {
                    var eventChannelAction = new RegistryKeyAction()
                    { KeyName = @"HKLM\Software\Microsoft\Windows\CurrentVersion\WINEVT\Channels\" + eventChannel, Operation = RegistryKeyOperation.Delete };
                    
                    if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil)
                    {
                        eventChannelAction.KeyName = @"HKLM\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\WINEVT\Channels\" + eventChannel;
                    }

                    eventChannelAction.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete event channel '{eventChannel}'.\r\nException: " + e.Message);
                }
            }
            RemovedEventChannels.AddRange(evtChannelList);

            var counterList = parsedXml.Counters.Except(RemovedCounters).ToList();
            foreach (var performanceCounter in counterList)
            {
                try
                {
                    var performanceCounterAction = new RegistryKeyAction()
                    { KeyName = @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Perflib\_V2Providers\" + performanceCounter, Operation = RegistryKeyOperation.Delete };

                    if (parsedXml.Identity.Arch != Architecture.amd64 && parsedXml.Identity.Arch != Architecture.msil)
                    {
                        performanceCounterAction.KeyName = @"HKLM\Software\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Perflib\_V2Providers\" + performanceCounter;
                    }

                    performanceCounterAction.RunTask();
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not delete performance counter with GUID '{performanceCounter}'.\r\nException: " + e.Message);
                }
            }
            RemovedCounters.AddRange(counterList);
        }
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        private static void RemoveManifestLinks(Manifest.ManifestData manifest)
        {
            var manifestWithoutExt = Path.GetFileNameWithoutExtension(manifest.ParsedName.RawPath);
            try
            {
                if (!(ExcludeList.Any(x => Regex.Match(@"%WINDIR%\WinSxS\Backup\" + manifestWithoutExt, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(@"%WINDIR%\WinSxS\Backup\" + manifestWithoutExt, x, RegexOptions.IgnoreCase).Success)))
                {
                    new FileAction()
                    { RawPath = Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\Backup\" + manifestWithoutExt + "*") }.RunTask();
                }
            } catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            try
            {
                if (!(ExcludeList.Any(x => Regex.Match(manifest.ParsedName.RawPath, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(manifest.ParsedName.RawPath, x, RegexOptions.IgnoreCase).Success)))
                {
                    new FileAction()
                    { RawPath = manifest.ParsedName.RawPath }.RunTask();
                }
            } catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            
            try
            {
                if (ExcludeList.Any())
                {
                    foreach (var file in Directory.EnumerateFiles(Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\") + manifestWithoutExt, "*", SearchOption.AllDirectories))
                    {
                        if (ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success))
                        {
                            continue;
                        }

                        new FileAction()
                        { RawPath = file }.RunTask();
                    }
                }
                else
                {
                    new FileAction()
                    { RawPath = Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\") + manifestWithoutExt }.RunTask();
                }

            } catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            /*
            if (manifest.Identity.Name.EndsWith("-Deployment"))
            {
                try
                {
                    var servicingFiles = Directory.EnumerateFiles(Environment.ExpandEnvironmentVariables(@"%WINDIR%\servicing\Packages"), $@"{manifest.Identity.Name.Replace("-Deployment", "") + "-Package"}*");

                    foreach (var file in servicingFiles)
                    {
                        if (file.Substring(file.IndexOf('~')).Contains(manifest.ParsedName.Arch.ToString()))
                        {
                            if (ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success))
                            {
                                continue;
                            }
                            new FileAction()
                            { RawPath = file }.RunTask();
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }
            }
            */
            var migrationFile = Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\migration.xml");
            if (File.Exists(migrationFile))
            {
                try
                {
                    string text = File.ReadAllText(migrationFile);
                    text = text.Replace("<file>" + manifest.ParsedName.RawName + "</file>", "");
                    File.WriteAllText(migrationFile, text);
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not modify migration.xml'.\r\nException: " + e.Message);
                }
            }
            /*try
            {

                var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\SideBySide\Winners", true);
                var keys = key.GetSubKeyNames().Where(x => x.StartsWith(parsedName.Arch + "_" + parsedName.ShortName + "_" + parsedName.PublicKey + "_" + parsedName.Language));
                foreach (var subKey in keys)
                {
                    key.DeleteSubKeyTree(subKey);
                }
            } catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            */
            /*
            try
            {
                string path = Registry.LocalMachine.OpenSubKey("COMPONENTS") == null ? @"HKEY_USERS\AME_ComponentsHive" : @"HKLM\COMPONENTS";
                if (path == @"HKEY_USERS\AME_ComponentsHive") RegistryManager.HookComponentsHive();
                
                new RegistryKeyAction()
                { KeyName = path + @"\CanonicalData\Deployments\" + identity.Name + "_" + parsedName.PublicKey + "_" + parsedName.Version + "_" + parsedName.Randomizer }.RunTask();
                new RegistryValueAction()
                { KeyName = path, Value = "DisableWerReporting", Data = 1 }.RunTask();
            } catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            */

            if (manifest.Identity.IsDriver)
            {
                try
                {
                    var file = Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\DriverStore\FileRepository\" + manifest.Identity.Name.Replace("dual_", "") + "_" + manifest.ParsedName.Arch.ToString() + "*");
                    if (!(ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success)))
                    {
                        new FileAction()
                        { RawPath = file }.RunTask();
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }

                try
                {
                    var file = Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\DriverStore\en-US\" + manifest.Identity.Name.Replace("dual_", "") + "_loc");
                    if (!(ExcludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success) && !IncludeList.Any(x => Regex.Match(file, x, RegexOptions.IgnoreCase).Success)))
                    {
                        new FileAction()
                        { RawPath = file }.RunTask();
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }

                try
                {
                    var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverFiles", true);
                    var values = key.GetValueNames();
                    foreach (var value in values)
                    {
                        if ((string)key.GetValue(value) == manifest.Identity.Name.Replace("dual_", ""))
                        {
                            new RegistryValueAction()
                            { KeyName = @"HKLM\SYSTEM\DriverDatabase\DriverFiles", Value = value }.RunTask();
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }

                try
                {
                    var key2 = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverInfFiles", true);
                    var values2 = key2.GetValueNames().Where(manifest.Identity.Name.Replace("dual_", "").Equals);
                    foreach (var value in values2)
                    {
                        new RegistryValueAction()
                        { KeyName = @"HKLM\SYSTEM\DriverDatabase\DriverInfFiles", Value = value }.RunTask();
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }

                try
                {
                    var key3 = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverPackages");
                    var values3 = key3.GetValueNames().Where((manifest.Identity.Name.Replace("dual_", "") + "_" + manifest.ParsedName.Arch.ToString()).Equals);
                    foreach (var value in values3)
                    {
                        new RegistryValueAction()
                        { KeyName = @"HKLM\SYSTEM\DriverDatabase\DriverPackages", Value = value }.RunTask();
                    }
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }
            }
        }

        private static List<string> ExcludeDependentsList = new List<string>();
        private static List<string> ExcludeList = new List<string>();
        private static List<string> IncludeList = new List<string>();
        public static AssemblyIdentity InputIdentity = new AssemblyIdentity();
        public static string HelperDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        
        internal static void Start(string[] args)
        {
            try
            {
                InputIdentity.Name = args[1];
                for (int i = 0; i <= args.Length - 1; i++)
                {
                    try
                    {
                        if (args[i].EqualsIC("-Arch")) InputIdentity.Arch = (Architecture)Enum.Parse(typeof(Architecture), args[i + 1]);
                    } catch (ArgumentException)
                    {
                        Console.WriteLine($"\r\nArgument -Arch must be supplied with amd64, x86, wow64, msil, or All.");
                        Environment.Exit(1);
                    }
                    try
                    {
                        if (args[i].EqualsIC("-Language")) InputIdentity.Language = args[i + 1];
                    } catch (ArgumentException)
                    {
                        Console.WriteLine($"\r\nArgument -Language must be supplied with a string.");
                        Environment.Exit(1);
                    }
                    try
                    {
                        if (args[i].EqualsIC("-HelperDir")) HelperDir = args[i + 1];
                    } catch (ArgumentException)
                    {
                        Console.WriteLine($"\r\nArgument -HelperDir must be supplied with a string.");
                        Environment.Exit(1);
                    }
                    
                    if (args[i].EqualsIC("-xdependent")) ExcludeDependentsList.Add(args[i + 1]);
                    if (args[i].EqualsIC("-xf")) ExcludeList.Add(args[i + 1]);
                    if (args[i].EqualsIC("-if")) IncludeList.Add(args[i + 1]);
                }
            } catch (IndexOutOfRangeException)
            {
                Console.WriteLine($"\r\nArgument -SystemPackage, -Arch, -xdependent, -xf, or -if must have a supplied string.");
                Environment.Exit(1);
            }

            if (!File.Exists(HelperDir + "\\wcp.dll")) FetchWCP();

            var manifests = Manifest.FindManifestsFromIdentity(InputIdentity);

            if (manifests.Count == 0)
            {
                Console.WriteLine($"\r\nNo package found that matches '{InputIdentity.Name}' with the specified properties.");
                Environment.Exit(0);
            }

            foreach (var manifest in manifests)
            {
                try
                {
                    var parsed = Manifest.ParseManifest(manifest);
                    RemoveItemsFromManifest(parsed);

                    Console.WriteLine($@"Removing package {manifest.Identity.Name} data...");
                    
                    RemoveManifestLinks(manifest);
                } catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Could not remove package {InputIdentity.Name}.\r\nException: " + e.Message);
                }
            }
            if (FileLock.HasKilledExplorer)
            {
                try
                {
                    var cmdAction = new CmdAction();
                    cmdAction.Command = "start explorer.exe";
                    cmdAction.Wait = false;
                    cmdAction.RunTask();
                } catch (Exception) { }
            }

            Console.WriteLine("\r\nComplete!");
        }
        
        public class AssemblyIdentity
        {
            public string Name { get; set; }
            internal Architecture Arch { get; set; } = Architecture.All;
            //public string Version { get; set; } = "*";
            public string PublicKey { get; set; } = "*";
            //public string BuildType { get; set; } = "*";
            //public string VersionScope { get; set; } = "*";
            public string Language { get; set; } = "*";
            
            public bool IsDriver => Name.EndsWith(".inf");

            public bool IdentityMatches(AssemblyIdentity other)
            {
                if (other.Name != "*" && Name != "*")
                {
                    if (other.Name.ToLower() != Name.ToLower()) return false;
                }
                if (other.Arch != Architecture.All && Arch != Architecture.All)
                {
                    if (other.Arch != Arch) return false;
                }
                if (other.Language != "*" && Language != "*")
                {
                    if (other.Language.ToLower() != Language.ToLower()) return false;
                }
                /*
                if (other.Version != "*" && Version != "*")
                {
                    if (other.Version != Version) return false;
                }
                */
                if (other.PublicKey != "*" && PublicKey != "*")
                {
                    if (other.PublicKey.ToLower() != PublicKey.ToLower()) return false;
                }
                /*
                if (other.BuildType != "*" && BuildType != "*")
                {
                    if (other.BuildType != BuildType) return false;
                }
                */
                /*
                if (other.VersionScope != "*" && VersionScope != "*")
                {
                    if (other.VersionScope.ToLower() != VersionScope.ToLower()) return false;
                }
                */

                return true;
            }
        }
    }
}