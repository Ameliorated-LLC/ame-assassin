using System;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrustedUninstaller.Shared.Tasks;

namespace ame_assassin
{
    public class FileAction : ITaskAction
    {
        public void RunTaskOnMainThread() { throw new NotImplementedException(); }
        public string RawPath { get; set; }
        
        public bool ExeFirst { get; set; } = false;
        
        public int ProgressWeight { get; set; } = 2;
        
        public bool TrustedInstaller { get; set; } = false;

        public int GetProgressWeight() => ProgressWeight;
        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;
        

        public string ErrorString() => $"FileAction failed to remove file or directory '{Environment.ExpandEnvironmentVariables(RawPath)}'.";

        private string GetRealPath()
        {
            return Environment.ExpandEnvironmentVariables(RawPath);
        }

        private string GetRealPath(string path)
        {
            return Environment.ExpandEnvironmentVariables(path);
        }

        public UninstallTaskStatus GetStatus()
        {
            if (InProgress) return UninstallTaskStatus.InProgress; var realPath = GetRealPath();
            
            if (realPath.Contains("*"))
            {
                var lastToken = realPath.LastIndexOf("\\");
                var parentPath = realPath.Remove(lastToken).TrimEnd('\\');

                // This is to prevent it from re-iterating with an incorrect argument
                if (parentPath.Contains("*")) return UninstallTaskStatus.Completed;
                var filter = realPath.Substring(lastToken + 1);

                if (Directory.Exists(parentPath) && (Directory.GetFiles(parentPath, filter).Any() || Directory.GetDirectories(parentPath, filter).Any()))
                {
                    return UninstallTaskStatus.ToDo;
                } 
                else return UninstallTaskStatus.Completed;
            }
            
            var isFile = File.Exists(realPath);
            var isDirectory = Directory.Exists(realPath);

            return isFile || isDirectory ? UninstallTaskStatus.ToDo : UninstallTaskStatus.Completed;
        }
        
        [DllImport("Unlocker.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool EzUnlockFileW(string path);
        
        private async Task DeleteFile(string file, bool log = false)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e)
            {
            }

            if (File.Exists(file))
            {
                try
                {
                    var result = EzUnlockFileW(file);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error while unlocking file: " + e.Message);
                }

                try
                {
                    await Task.Run(() => File.Delete(file));
                }
                catch (Exception e)
                {
                }
            }
        }
        private async Task RemoveDirectory(string dir, bool log = false)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        private async Task DeleteItemsInDirectory(string dir, string filter = "*")
        {
            var realPath = GetRealPath(dir);

            var files = Directory.EnumerateFiles(realPath, filter);
            var directories = Directory.EnumerateDirectories(realPath, filter);
            
            if (ExeFirst) files = files.ToList().OrderByDescending(x => x.EndsWith(".exe"));

            var lockedFilesList = new List<string> { "MpOAV.dll", "MsMpLics.dll", "EppManifest.dll", "MpAsDesc.dll", "MpClient.dll", "MsMpEng.exe" };
            foreach (var file in files)
            {
                Console.WriteLine($"Deleting {file}...");

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                await DeleteFile(file);

                if (File.Exists(file))
                {
                    TaskKillAction taskKillAction = new TaskKillAction();

                    if (file.EndsWith(".sys"))
                    {
                        var driverService = Path.GetFileNameWithoutExtension(file);
                        try
                        {
                            //ServiceAction won't work here due to it not being able to detect driver services.
                            var cmdAction = new CmdAction();
                            Console.WriteLine($"Removing driver service {driverService}...");

                            // TODO: Replace with win32
                            try
                            {
                                ServiceInstaller ServiceInstallerObj = new ServiceInstaller();
                                ServiceInstallerObj.Context = new InstallContext();
                                ServiceInstallerObj.ServiceName = driverService; 
                                ServiceInstallerObj.Uninstall(null);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Service uninstall failed: " + e.Message);
                            }
                                
                            cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {driverService} -caction stop";
                            if (Program.UseKernelDriver) cmdAction.RunTaskOnMainThread();

                            cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {driverService} -caction delete";
                            if (Program.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                        }
                        catch (Exception servException)
                        {
                            Console.WriteLine(servException.Message);
                        }
                    }
                    if (lockedFilesList.Contains(Path.GetFileName(file)))
                    {
                        TaskKillAction killAction = new TaskKillAction()
                        {
                            ProcessName = "MsMpEng"
                        };

                        killAction.RunTask();

                        killAction.ProcessName = "NisSrv";
                        killAction.RunTask();

                        killAction.ProcessName = "SecurityHealthService";
                        killAction.RunTask();

                        killAction.ProcessName = "smartscreen";
                        killAction.RunTask();

                    }

                    var processes = new List<Process>();
                    try
                    {
                        processes = FileLock.WhoIsLocking(file);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }

                    var delay = 0;

                    int svcCount = 0;
                    foreach (var svchost in processes.Where(x => x.ProcessName.Equals("svchost")))
                    {
                        try
                        {
                            foreach (var serviceName in Win32.ServiceEx.GetServicesFromProcessId(svchost.Id))
                            {
                                svcCount++;
                                try
                                {
                                    var serviceController = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName));
                                    if (serviceController != null)
                                        svcCount += serviceController.DependentServices.Length;
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine($"\r\nError: Could not get amount of dependent services for {serviceName}.\r\nException: " + e.Message);
                                }
                            }
                        } catch (Exception e)
                        {
                            Console.WriteLine($"\r\nError: Could not get amount of services locking file.\r\nException: " + e.Message);
                        }
                    }
                    
                    while (processes.Any() && delay <= 800)
                    {
                        Console.WriteLine("Processes locking the file:");
                        foreach (var process in processes)
                        {
                            Console.WriteLine(process.ProcessName);
                        }
                        if (svcCount > 10)
                        {
                            Console.WriteLine("Amount of locking services exceeds 10, skipping...");
                            break;
                        }

                        foreach (var process in processes)
                        {
                            try
                            {
                                if (process.ProcessName.Equals("TrustedUninstaller.CLI"))
                                {
                                    Console.WriteLine("Skipping TU.CLI...");
                                    continue;
                                }
                                if (Regex.Match(process.ProcessName, "ame.?wizard", RegexOptions.IgnoreCase).Success)
                                {
                                    Console.WriteLine("Skipping AME Wizard...");
                                    continue;
                                }

                                taskKillAction.ProcessName = process.ProcessName;
                                taskKillAction.ProcessID = process.Id;

                                Console.WriteLine($"Killing locking process {process.ProcessName} with PID {process.Id}...");
                            }
                            catch (InvalidOperationException)
                            {
                                // Calling ProcessName on a process object that has exited will thrown this exception causing the
                                // entire loop to abort. Since killing a process takes a bit of time, another process in the loop
                                // could exit during that time. This accounts for that.
                                continue;
                            }

                            try
                            {
                                taskKillAction.RunTask();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.Message);
                            }
                        }

                        // This gives any obstinant processes some time to unlock the file on their own.
                        //
                        // This could be done above but it's likely to cause HasExited errors if delays are
                        // introduced after WhoIsLocking.
                        System.Threading.Thread.Sleep(delay);
                        
                        try
                        {
                            processes = FileLock.WhoIsLocking(file);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                        
                        delay += 100;
                    }
                    if (delay >= 800)
                        Console.WriteLine($"Could not kill locking processes for file '{file}'. Process termination loop exceeded max cycles (8).");
                    
                    if (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        new TaskKillAction() { ProcessName = Path.GetFileNameWithoutExtension(file) }.RunTask();
                    }

                    await DeleteFile(file, true);
                }
            }
            //Loop through any subdirectories
            foreach (var directory in directories)
            {
                //Deletes the content of the directory
                await DeleteItemsInDirectory(directory);

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                await RemoveDirectory(directory, true);

                if (Directory.Exists(directory))
                    Console.WriteLine($"Could not remove directory '{directory}'.");
            }
        }

        public async void RunTask()
        {
            var realPath = GetRealPath();
            
            Console.WriteLine($"Removing file or directory '{realPath}'...");
            
            if (realPath.Contains("*"))
            {
                var lastToken = realPath.LastIndexOf("\\");
                var parentPath = realPath.Remove(lastToken).TrimEnd('\\');

                if (parentPath.Contains("*")) throw new ArgumentException("Parent directories to a given file filter cannot contain wildcards.");
                var filter = realPath.Substring(lastToken + 1);

                await DeleteItemsInDirectory(parentPath, filter);
                
                InProgress = false;
                return;
            }
            
            var isFile = File.Exists(realPath);
            var isDirectory = Directory.Exists(realPath);
            
            if (isDirectory)
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                await RemoveDirectory(realPath);

                if (Directory.Exists(realPath))
                {
                    CmdAction permAction = new CmdAction()
                    {
                        Command = $"takeown /f \"{realPath}\" /r /d Y>NUL & icacls \"{realPath}\" /t /grant Administrators:F /c > NUL",
                        Timeout = 5000
                    };
                    try
                    {
                        permAction.RunTaskOnMainThread();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }

                    try
                    {
                        if (realPath.Contains("Defender"))
                        {
                            TaskKillAction killAction = new TaskKillAction()
                            {
                                ProcessName = "MsMpEng"
                            };

                            killAction.RunTask();

                            killAction.ProcessName = "NisSrv";
                            killAction.RunTask();

                            killAction.ProcessName = "SecurityHealthService";
                            killAction.RunTask();

                            killAction.ProcessName = "smartscreen";
                            killAction.RunTask();
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                    
                    await RemoveDirectory(realPath, true);

                    if (Directory.Exists(realPath))
                    {
                        //Delete the files in the initial directory. DOES delete directories.
                        await DeleteItemsInDirectory(realPath);

                        System.GC.Collect();
                        System.GC.WaitForPendingFinalizers();
                        await RemoveDirectory(realPath, true);
                    }
                }
            }
            if (isFile)
            {
                try
                {
                    var lockedFilesList = new List<string> { "MpOAV.dll", "MsMpLics.dll", "EppManifest.dll", "MpAsDesc.dll", "MpClient.dll", "MsMpEng.exe" };
                    var fileName = realPath.Split('\\').LastOrDefault();

                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    await DeleteFile(realPath);

                    if (File.Exists(realPath))
                    {
                        CmdAction permAction = new CmdAction()
                        {
                            Command = $"takeown /f \"{realPath}\" /r /d Y>NUL & icacls \"{realPath}\" /t /grant Administrators:F /c > NUL",
                            Timeout = 5000
                        };
                        try
                        {
                            permAction.RunTaskOnMainThread();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                        
                        TaskKillAction taskKillAction = new TaskKillAction();

                        if (realPath.EndsWith(".sys"))
                        {
                            var driverService = Path.GetFileNameWithoutExtension(realPath);
                            try
                            {
                                //ServiceAction won't work here due to it not being able to detect driver services.
                                var cmdAction = new CmdAction();
                                Console.WriteLine($"Removing driver service {driverService}...");

                                // TODO: Replace with win32
                                try
                                {
                                    ServiceInstaller ServiceInstallerObj = new ServiceInstaller();
                                    ServiceInstallerObj.Context = new InstallContext();
                                    ServiceInstallerObj.ServiceName = driverService; 
                                    ServiceInstallerObj.Uninstall(null);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("Service uninstall failed: " + e.Message);
                                }
                                
                                cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {driverService} -caction stop";
                                if (Program.UseKernelDriver) cmdAction.RunTaskOnMainThread();

                                cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {driverService} -caction delete";
                                if (Program.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                            }
                            catch (Exception servException)
                            {
                                Console.WriteLine(servException.Message);
                            }
                        }

                        if (lockedFilesList.Contains(fileName))
                        {
                            TaskKillAction killAction = new TaskKillAction()
                            {
                                ProcessName = "MsMpEng"
                            };

                            killAction.RunTask();

                            killAction.ProcessName = "NisSrv";
                            killAction.RunTask();

                            killAction.ProcessName = "SecurityHealthService";
                            killAction.RunTask();

                            killAction.ProcessName = "smartscreen";
                            killAction.RunTask();

                        }

                        var processes = new List<Process>();
                        try
                        {
                            processes = FileLock.WhoIsLocking(realPath);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                        var delay = 0;

                        int svcCount = 0;
                        foreach (var svchost in processes.Where(x => x.ProcessName.Equals("svchost")))
                        {
                            try
                            {
                                foreach (var serviceName in Win32.ServiceEx.GetServicesFromProcessId(svchost.Id))
                                {
                                    svcCount++;
                                    try
                                    {
                                        var serviceController = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName));
                                        if (serviceController != null)
                                            svcCount += serviceController.DependentServices.Length;
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine($"\r\nError: Could not get amount of dependent services for {serviceName}.\r\nException: " + e.Message);
                                    }
                                }
                            } catch (Exception e)
                            {
                                Console.WriteLine($"\r\nError: Could not get amount of services locking file.\r\nException: " + e.Message);
                            }
                        }
                        if (svcCount > 8) Console.WriteLine("Amount of locking services exceeds 8, skipping...");
                        
                        while (processes.Any() && delay <= 800 && svcCount <= 8)
                        {
                            Console.WriteLine("Processes locking the file:");
                            foreach (var process in processes)
                            {
                                Console.WriteLine(process.ProcessName);
                            }

                            foreach (var process in processes)
                            {
                                try
                                {
                                    if (process.ProcessName.Equals("TrustedUninstaller.CLI"))
                                    {
                                        Console.WriteLine("Skipping TU.CLI...");
                                        continue;
                                    }
                                    if (Regex.Match(process.ProcessName, "ame.?wizard", RegexOptions.IgnoreCase).Success)
                                    {
                                        Console.WriteLine("Skipping AME Wizard...");
                                        continue;
                                    }

                                    taskKillAction.ProcessName = process.ProcessName;
                                    taskKillAction.ProcessID = process.Id;

                                    Console.WriteLine($"Killing {process.ProcessName} with PID {process.Id}... it is locking {realPath}");
                                }
                                catch (InvalidOperationException)
                                {
                                    // Calling ProcessName on a process object that has exited will thrown this exception causing the
                                    // entire loop to abort. Since killing a process takes a bit of time, another process in the loop
                                    // could exit during that time. This accounts for that.
                                    continue;
                                }

                                try
                                {
                                    taskKillAction.RunTask();
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.Message);
                                }
                            }

                            // This gives any obstinant processes some time to unlock the file on their own.
                            //
                            // This could be done above but it's likely to cause HasExited errors if delays are
                            // introduced after WhoIsLocking.
                            System.Threading.Thread.Sleep(delay);
                        
                            try
                            {
                                processes = FileLock.WhoIsLocking(realPath);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.Message);
                            }
                        
                            delay += 100;
                        }
                        if (delay >= 800)
                            Console.WriteLine($"Could not kill locking processes for file '{realPath}'. Process termination loop exceeded max cycles (8).");

                        if (Path.GetExtension(realPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            new TaskKillAction() { ProcessName = Path.GetFileNameWithoutExtension(realPath) }.RunTask();
                        }
                        
                        await DeleteFile(realPath, true);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }
            else
            {
                Console.WriteLine($"File or directory '{realPath}' not found.");
            }

            InProgress = false;
            return;
        }
    }
}