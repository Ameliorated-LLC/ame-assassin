using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ame_assassin;
using TrustedUninstaller.Shared.Tasks;

namespace TrustedUninstaller.Shared.Actions
{
    class TaskKillAction : ITaskAction
    {
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess,
            bool bInheritHandle, int dwProcessId);
        public enum ProcessAccessFlags : uint
        {
            QueryLimitedInformation = 0x1000
        }
        
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);
        
        public string? ProcessName { get; set; }
        
        public string? PathContains { get; set; }
        
        public int ProgressWeight { get; set; } = 2;
        public int GetProgressWeight() => ProgressWeight;

        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;
        
        public int? ProcessID { get; set; }
        
        public string ErrorString()
        {
            string text = $"TaskKillAction failed to kill processes matching '{ProcessName}'.";

            try
            {
                var processes = GetProcess().Select(process => process.ProcessName).Distinct().ToList();
                if (processes.Count > 1)
                {
                    text = $"TaskKillAction failed to kill processes:";
                    foreach (var process in processes)
                    {
                        text += "|NEWLINE|" + process;
                    }
                }
                else if (processes.Count == 1) text = $"TaskKillAction failed to kill process {processes[0]}.";
            } catch (Exception) { }

            return text;
        }

        public UninstallTaskStatus GetStatus()
        {
            if (InProgress)
            {
                return UninstallTaskStatus.InProgress;
            }

            List<Process> processToTerminate = new List<Process>();
            if (ProcessID.HasValue)
            {
                try { processToTerminate.Add(Process.GetProcessById((int)ProcessID)); } catch (Exception) { } 
            }
            else
            {
                processToTerminate = GetProcess().ToList();
            }

            return processToTerminate.Any() ? UninstallTaskStatus.ToDo : UninstallTaskStatus.Completed;
        }

        private IEnumerable<Process> GetProcess()
        {
            if (ProcessName == null) return new List<Process>();
            
            if (ProcessName.EndsWith("*") && ProcessName.StartsWith("*")) return Process.GetProcesses()
                .Where(process => process.ProcessName.IndexOf(ProcessName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (ProcessName.EndsWith("*")) return Process.GetProcesses()
                .Where(process => process.ProcessName.StartsWith(ProcessName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase));
            if (ProcessName.StartsWith("*")) return Process.GetProcesses()
                .Where(process => process.ProcessName.EndsWith(ProcessName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase));

            return Process.GetProcessesByName(ProcessName);
        }

        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool IsProcessCritical(IntPtr hProcess, ref bool Critical);
        
        private readonly string[] RegexNoKill = { "lsass", "csrss", "winlogon", "TrustedUninstaller\\.CLI", "dwm", "conhost", "ame.?wizard", "ame.?assassin" }; 
        private readonly string[] RegexNotCritical = { "SecurityHealthService", "wscsvc", "MsMpEng", "SgrmBroker" };
        public void RunTask()
        {
            InProgress = true;

            if (ProcessName == "" && ProcessID.HasValue)
            {
                Console.WriteLine($"Killing process with PID '{ProcessID.Value}'...");
            }
            else
            {
                if (RegexNoKill.Any(regex => Regex.Match(ProcessName, regex, RegexOptions.IgnoreCase).Success))
                {
                    Console.WriteLine($"Skipping {ProcessName}...");
                    return;
                }
                
                Console.WriteLine($"Killing processes matching '{ProcessName}'...");
            }
            
            var cmdAction = new CmdAction();
            
            if (ProcessName != null)
            {
                //If the service is svchost, we stop the service instead of killing it.
                if (ProcessName.Contains("svchost"))
                {
                    // bool serviceFound = false;
                    try
                    {
                        using var search = new ManagementObjectSearcher($"select * from Win32_Service where ProcessId = '{ProcessID}'");

                        foreach (ManagementObject queryObj in search.Get())
                        {
                            var serviceName = (string)queryObj["Name"]; // Access service name  
                            
                            var stopServ = new ServiceAction()
                            {
                                ServiceName = serviceName,
                                Operation = ServiceOperation.Stop

                            };
                            stopServ.RunTask();
                        }
                    }
                    catch (NullReferenceException e)
                    {
                        Console.WriteLine($"\r\nError: A service with PID: {ProcessID} could not be found.");
                    }


/*                    foreach (var serv in servicesToDelete)
                    {
                        //The ID can only be associated with one of the services, there's no need to loop through
                        //them all if we already found the service.
                        if (serviceFound)
                        {
                            break;
                        }

                        try
                        {
                            using var search = new ManagementObjectSearcher($"select ProcessId from Win32_Service where Name = '{serv}'").Get();
                            var servID = (uint)search.OfType<ManagementObject>().FirstOrDefault()["ProcessID"];

                            if (servID == ProcessID)
                            {
                                serviceFound = true;



                            }
                            search.Dispose();
                        }
                        catch (Exception e)
                        {
                            var search = new ManagementObjectSearcher($"select Name from Win32_Service where ProcessID = '{ProcessID}'").Get();
                            var servName = search.OfType<ManagementObject>().FirstOrDefault()["Name"];
                            Console.WriteLine($"Could not find {servName} but PID {ProcessID} still exists.");
                            ErrorLogger.WriteToErrorLog(e.Message, e.StackTrace, $"Exception Type: {e.GetType()}");
                            return;
                        }
                    }*/
                    //None of the services listed, we shouldn't kill svchost.
/*                    if (!serviceFound)
                    {
                        var search = new ManagementObjectSearcher($"select Name from Win32_Service where ProcessID = '{ProcessID}'").Get();
                        var servName = search.OfType<ManagementObject>().FirstOrDefault()["Name"];
                        Console.WriteLine($"A critical system process \"{servName}\" with PID {ProcessID} caused the Wizard to fail.");
                        WinUtil.UninstallDriver();
                        Environment.Exit(-1);
                        return;
                    }*/

                    Task.Delay(100);

                    InProgress = false;
                    return;
                }

                if (PathContains != null && !ProcessID.HasValue)
                {

                    var processes = GetProcess().ToList();
                    if (processes.Count > 0) Console.WriteLine("Processes:");

                    foreach (var process in processes.Where(x => x.MainModule.FileName.Contains(PathContains)))
                    {
                        Console.WriteLine(process.ProcessName + " - " + process.Id);
                        
                        if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success)) {
                            bool isCritical = false;
                            IntPtr hprocess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, process.Id);
                            IsProcessCritical(hprocess, ref isCritical);
                            CloseHandle(hprocess);
                            if (isCritical)
                            {
                                Console.WriteLine($"{process.ProcessName} is a critical process, skipping...");
                                continue;
                            }
                        }

                        cmdAction.Command = Environment.Is64BitOperatingSystem ?
                            $"Executables\\ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype process -cobject {process.Id} -caction terminate" :
                            $"Executables\\ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype process -cobject {process.Id} -caction terminate";
                        if (Program.UseKernelDriver) cmdAction.RunTask();
                        else TerminateProcess(process.Handle, 1);
                        
                        int i = 0;
                        while (i <= 15 && GetProcess().Any(x => x.Id == process.Id && x.ProcessName == process.ProcessName))
                        {
                            Task.Delay(300);
                            i++;
                        }
                        if (i >= 15) Console.WriteLine("Error: Task kill timeout exceeded.");
                    }
                    InProgress = false;
                    return;
                }
            }
            
            if (ProcessID.HasValue)
            {
                if (ProcessName != null && ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    var process = Process.GetProcessById(ProcessID.Value);
                    TerminateProcess(process.Handle, 1);
                    FileLock.HasKilledExplorer = true;
                }
                else
                {
                    var process = Process.GetProcessById(ProcessID.Value);
                    
                    if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success)) {
                        bool isCritical = false;
                        IntPtr hprocess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, process.Id);
                        IsProcessCritical(hprocess, ref isCritical);
                        CloseHandle(hprocess);
                        if (isCritical)
                        {
                            Console.WriteLine($"{process.ProcessName} is a critical process, skipping...");
                            return;
                        }
                    }
                    
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype process -cobject {ProcessID} -caction terminate";
                    if (Program.UseKernelDriver) cmdAction.RunTask();
                    else TerminateProcess(Process.GetProcessById(ProcessID.Value).Handle, 1);
                }

                Task.Delay(100);
            }
            else
            {
                var processes = GetProcess().ToList();
                if (processes.Count > 0) Console.WriteLine("Processes:");

                foreach (var process in processes)
                {
                    Console.WriteLine(process.ProcessName + " - " + process.Id);
                    
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype process -cobject {process.ProcessName}.exe -caction terminate";
                    if (process.ProcessName == "explorer")
                    {
                        TerminateProcess(process.Handle, 1);
                        FileLock.HasKilledExplorer = true;
                    }
                    else
                    {
                        if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success)) {
                            bool isCritical = false;
                            IntPtr hprocess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, process.Id);
                            IsProcessCritical(hprocess, ref isCritical);
                            CloseHandle(hprocess);
                            if (isCritical)
                            {
                                Console.WriteLine($"{process.ProcessName} is a critical process, skipping...");
                                return;
                            }
                        }
                        
                        if (Program.UseKernelDriver) cmdAction.RunTask();
                        else TerminateProcess(process.Handle, 1);
                    }

                    int i = 0;

                    while (i <= 15 && GetProcess().Any(x => x.Id == process.Id && x.ProcessName == process.ProcessName))
                    {
                        Task.Delay(300);
                        i++;
                    }
                    if (i >= 15) Console.WriteLine("Error: Task kill timeout exceeded.");
                }
            }
            
            InProgress = false;
            return;
        }
    }
}
