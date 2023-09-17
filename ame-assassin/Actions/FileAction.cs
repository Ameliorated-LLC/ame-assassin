using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ame_assassin;
using TrustedUninstaller.Shared.Tasks;

namespace TrustedUninstaller.Shared.Actions
{

    public class FileAction : ITaskAction
    {
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
        
        private void DeleteFile(string file, bool log = false)
        {
            if (!TrustedInstaller)
            {
                try {File.Delete(file);} catch {}
                    
                if (File.Exists(file))
                {
                    CmdAction delAction = new CmdAction()
                    {
                        Command = $"del /q /f {file}"
                    };
                    delAction.RunTask();
                }
            }
        }
        private void RemoveDirectory(string dir, bool log = false)
        {
            if (!TrustedInstaller)
            {
                try { Directory.Delete(dir, true); } catch { }
                    
                if (Directory.Exists(dir))
                {
                    Console.WriteLine("Directory still exists.. trying second method.");
                    var deleteDirCmd = new CmdAction()
                    {
                        Command = $"rmdir /Q /S \"{dir}\""
                    };
                    deleteDirCmd.RunTask();
                        
                    if (deleteDirCmd.StandardError != null)
                    {
                        Console.WriteLine($"Error Output: {deleteDirCmd.StandardError}");
                    }
                    if (deleteDirCmd.StandardOutput != null)
                    {
                        Console.WriteLine($"Standard Output: {deleteDirCmd.StandardOutput}");
                    }
                }
            }
        }
        private void DeleteItemsInDirectory(string dir, string filter = "*")
        {
            var realPath = GetRealPath(dir);

            var files = Directory.EnumerateFiles(realPath, filter);
            var directories = Directory.EnumerateDirectories(realPath, filter);
            
            if (ExeFirst) files = files.ToList().OrderByDescending(x => x.EndsWith(".exe"));

            var lockedFilesList = new List<string> { "MpOAV.dll", "MsMpLics.dll", "EppManifest.dll", "MpAsDesc.dll", "MpClient.dll", "MsMpEng.exe" };
            foreach (var file in files)
            {
                Console.WriteLine($"Deleting file '{file}'...");

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                DeleteFile(file);

                if (File.Exists(file))
                {
                    TaskKillAction taskKillAction = new TaskKillAction();

                    if (file.EndsWith(".sys"))
                    {
                        var driverService = Path.GetFileNameWithoutExtension(file);
                        try
                        {
                            //ServiceAction won't work here due to it not being able to detect driver services.
                            var servAction = new ServiceAction()
                            {
                                Operation = ServiceOperation.Delete,
                                Device = true,
                                ServiceName = driverService
                            };
                            
                            servAction.RunTask();
                        }
                        catch (Exception servException)
                        {
                            Console.WriteLine("\r\nError: Could not delete driver service.\r\nException: " + servException.Message);
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
                        Console.WriteLine($"\r\nError: Could get locking processes for file '{file}'.\r\nException: " + e.Message);
                    }

                    var delay = 0;

                    while (processes.Any() && delay <= 200)
                    {
                        Console.WriteLine("Processes locking the file:");
                        foreach (var process in processes)
                        {
                            Console.WriteLine(process.ProcessName);
                        }
                        
                        int svcCount = 0;
                        foreach (var svchost in processes.Where(x => x.ProcessName.Equals("svchost")))
                        {
                            try
                            {
                                using var search = new ManagementObjectSearcher($"select * from Win32_Service where ProcessId = '{svchost.Id}'");

                                foreach (ManagementObject queryObj in search.Get())
                                {
                                    var serviceName = (string)queryObj["Name"]; // Access service name  
                                
                                    var serv = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName));

                                    if (serv == null) svcCount++;
                                    else svcCount += serv.DependentServices.Length + 1;
                                }
                            } catch (Exception e)
                            {
                                Console.WriteLine($"\r\nError: Could not get amount of services locking file.\r\nException: " + e.Message);
                            }
                        }
                            
                        if (svcCount > 8)
                        {
                            Console.WriteLine("Amount of locking services exceeds 8, skipping...");
                            break;
                        }

                        foreach (var process in processes)
                        {
                            try
                            {
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

                            taskKillAction.RunTask();
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
                            Console.WriteLine($"\r\nError: Could get locking processes for file '{file}'.\r\nException: " + e.Message);
                        }

                        delay += 100;
                    }

                    if (delay >= 200)
                        Console.WriteLine($"\r\nError: Could not kill locking processes for file '{file}'. Process termination loop exceeded max cycles (2).");

                    DeleteFile(file, true);
                }
            }
            //Loop through any subdirectories
            foreach (var directory in directories)
            {
                //Deletes the content of the directory
                DeleteItemsInDirectory(directory);

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                RemoveDirectory(directory, true);

                if (Directory.Exists(directory))
                    Console.WriteLine($"\r\nError: Could not remove directory '{directory}'.");
            }
        }

        public void RunTask()
        {
            InProgress = true;

            var realPath = GetRealPath();
            
            Console.WriteLine($"Removing file or directory '{realPath}'...");
            
            if (realPath.Contains("*"))
            {
                var lastToken = realPath.LastIndexOf("\\");
                var parentPath = realPath.Remove(lastToken).TrimEnd('\\');

                if (parentPath.Contains("*")) throw new ArgumentException("Parent directories to a given file filter cannot contain wildcards.");
                var filter = realPath.Substring(lastToken + 1);

                DeleteItemsInDirectory(parentPath, filter);
                
                InProgress = false;
                return;
            }
            
            var isFile = File.Exists(realPath);
            var isDirectory = Directory.Exists(realPath);
            
            if (isDirectory)
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                RemoveDirectory(realPath);

                if (Directory.Exists(realPath))
                {
                    CmdAction permAction = new CmdAction()
                    {
                        Command = $"takeown /f \"{realPath}\" /r /d Y>NUL & icacls \"{realPath}\" /t /grant Administrators:F /c > NUL",
                        Timeout = 5000
                    };
                    try
                    {
                        permAction.RunTask();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not take ownership of file or directory {realPath}.\r\nException: " + e.Message);
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
                        Console.WriteLine("\r\nError: Could not kill defender processes.\r\nException: " + e.Message);
                    }
                    
                    RemoveDirectory(realPath, true);

                    if (Directory.Exists(realPath))
                    {
                        //Delete the files in the initial directory. DOES delete directories.
                        DeleteItemsInDirectory(realPath);

                        System.GC.Collect();
                        System.GC.WaitForPendingFinalizers();
                        RemoveDirectory(realPath, true);
                    }
                }
            }
            else if (isFile)
            {
                try
                {
                    var lockedFilesList = new List<string> { "MpOAV.dll", "MsMpLics.dll", "EppManifest.dll", "MpAsDesc.dll", "MpClient.dll", "MsMpEng.exe" };
                    var fileName = realPath.Split('\\').LastOrDefault();

                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    DeleteFile(realPath);

                    if (File.Exists(realPath))
                    {
                        CmdAction permAction = new CmdAction()
                        {
                            Command = $"takeown /f \"{realPath}\" /r /d Y>NUL & icacls \"{realPath}\" /t /grant Administrators:F /c > NUL",
                            Timeout = 5000
                        };
                        try
                        {
                            permAction.RunTask();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"\r\nError: Could not take ownership of file or directory {realPath}.\r\nException: " + e.Message);
                        }
                        
                        TaskKillAction taskKillAction = new TaskKillAction();

                        if (realPath.EndsWith(".sys"))
                        {
                            var driverService = Path.GetFileNameWithoutExtension(realPath);
                            try
                            {
                                //ServiceAction won't work here due to it not being able to detect driver services.
                                var servAction = new ServiceAction()
                                {
                                Operation = ServiceOperation.Delete,
                                Device = true,
                                ServiceName = driverService
                                };
                            
                                servAction.RunTask();
                            }
                            catch (Exception servException)
                            {
                                Console.WriteLine("\r\nError: Could not delete driver service.\r\nException: " + servException.Message);
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
                            Console.WriteLine($"\r\nError: Could not get locking processes for file '{realPath}'.\r\nException: " + e.Message);
                        }
                        var delay = 0;

                        while (processes.Any() && delay <= 200)
                        {
                            Console.WriteLine("Processes locking the file:");
                            foreach (var process in processes)
                            {
                                Console.WriteLine(process.ProcessName);
                            }

                            int svcCount = 0;
                            foreach (var svchost in processes.Where(x => x.ProcessName.Equals("svchost")))
                            {
                                try
                                {
                                    using var search = new ManagementObjectSearcher($"select * from Win32_Service where ProcessId = '{svchost.Id}'");

                                    foreach (ManagementObject queryObj in search.Get())
                                    {
                                        var serviceName = (string)queryObj["Name"]; // Access service name  

                                        var serv = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName));
                                        
                                        svcCount += serv.DependentServices.Length + 1;
                                    }
                                } catch (Exception e)
                                {
                                    Console.WriteLine($"\r\nError: Could not get amount of services locking file.\r\nException: " + e.Message);
                                }
                            }
                            
                            if (svcCount > 8)
                            {
                                Console.WriteLine("Amount of locking services exceeds 8, skipping...");
                                break;
                            }

                            foreach (var process in processes)
                            {
                                try
                                {
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
                                    Console.WriteLine($"\r\nError: Could not kill process {process.ProcessName}.\r\nException: " + e);
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
                                Console.WriteLine($"\r\nError: Could get locking processes for file '{realPath}'.\r\nException: " + e.Message);
                            }
                        
                            delay += 100;
                        }
                        if (delay >= 200)
                            Console.WriteLine($"\r\nError: Could not kill locking processes for file '{realPath}'. Process termination loop exceeded max cycles (2).");

                        DeleteFile(realPath, true);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"\r\nError: Error while trying to delete {realPath}.\r\nException: " + e.Message);
                }
            }

            InProgress = false;
            return;
        }
    }
}