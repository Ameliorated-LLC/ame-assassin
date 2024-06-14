﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ame_assassin;
using TrustedUninstaller.Shared.Tasks;

namespace ame_assassin
{
    class TaskKillAction : ITaskAction
    {
        public void RunTaskOnMainThread() { throw new NotImplementedException(); }
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

        private List<Process> GetProcess()
        {
            if (ProcessID.HasValue)
            {
                var list = new List<Process>();
                try
                {
                    var process = Process.GetProcessById(ProcessID.Value);
                    if (ProcessName == null || process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
                        list.Add(process);
                    else
                        return list;
                }
                catch (Exception e)
                {
                    return list;
                } 
            }
            
            if (ProcessName == null)
            {
                return new List<Process>();
            }
            
            if (ProcessName.EndsWith("*") && ProcessName.StartsWith("*")) return Process.GetProcesses().ToList()
                .Where(process => process.ProcessName.IndexOf(ProcessName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            if (ProcessName.EndsWith("*")) return Process.GetProcesses()
                .Where(process => process.ProcessName.StartsWith(ProcessName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (ProcessName.StartsWith("*")) return Process.GetProcesses()
                .Where(process => process.ProcessName.EndsWith(ProcessName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase)).ToList();

            return Process.GetProcessesByName(ProcessName).ToList();
        } 
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool IsProcessCritical(IntPtr hProcess, ref bool Critical);
        
        private readonly string[] RegexNoKill = { "lsass", "csrss", "winlogon", "TrustedUninstaller\\.CLI", "dwm", "conhost", "ame.?wizard", "ame.?assassin" };
        // These processes give access denied errors when getting their handle for IsProcessCritical.
        // TODO: Investigate how to properly acquire permissions.
        private readonly string[] RegexNotCritical = { "SecurityHealthService", "wscsvc", "MsMpEng", "SgrmBroker" };
        public void RunTask()
        {
            InProgress = true;
            
            if (string.IsNullOrEmpty(ProcessName) && ProcessID.HasValue)
            {
                Console.WriteLine($"Killing process with PID '{ProcessID.Value}'...");
            }
            else
            {
                if (ProcessName != null && RegexNoKill.Any(regex => Regex.Match(ProcessName, regex, RegexOptions.IgnoreCase).Success))
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
                if (ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (ProcessID.HasValue)
                        {
                            foreach (var serviceName in Win32.ServiceEx.GetServicesFromProcessId(ProcessID.Value))
                            {
                                try
                                {
                                    var stopServ = new ServiceAction()
                                    {
                                        ServiceName = serviceName,
                                        Operation = ServiceOperation.Stop
                                    };
                                    stopServ.RunTask();
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine($"Could not kill service " + serviceName + ": " + e.Message);
                                    Console.WriteLine(e.Message);
                                }
                            }
                        }
                        else
                        {
                            foreach (var process in GetProcess())
                            {
                                foreach (var serviceName in Win32.ServiceEx.GetServicesFromProcessId(process.Id))
                                {
                                    try
                                    {
                                        var stopServ = new ServiceAction()
                                        {
                                            ServiceName = serviceName,
                                            Operation = ServiceOperation.Stop

                                        };
                                        stopServ.RunTask();
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine($"Could not kill service " + serviceName + ": " + e.Message);
                                        Console.WriteLine(e.Message);
                                    }
                                }
                            }
                        }
                    }
                    catch (NullReferenceException e)
                    {
                        Console.WriteLine($"A service with PID: {ProcessID.Value} could not be found.");
                        Console.WriteLine(e.Message);
                    }

                    int i;
                    for (i = 0; i <= 6 && GetProcess().Any(); i++)
                    {
                        Thread.Sleep(100 * i);
                    }
                    if (i < 6)
                    {
                        InProgress = false;
                        return;
                    }
                }

                if (PathContains != null && !ProcessID.HasValue)
                {
                    var processes = GetProcess();
                    if (processes.Count > 0) Console.WriteLine("Processes:");

                    foreach (var process in processes.Where(x =>
                             {
                                 try
                                 {
                                     return x.MainModule.FileName.Contains(PathContains);
                                 }
                                 catch (Exception e)
                                 {
                                     return false;
                                 }
                             }))
                    {
                        Console.WriteLine(process.ProcessName + " - " + process.Id);

                        if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success))
                        {
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
                        try
                        {
                            if (!TerminateProcess(process.Handle, 1))
                                Console.WriteLine("TerminateProcess failed with error code: " + Marshal.GetLastWin32Error());
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Could not open process handle: " + e.Message);
                        }
                        try
                        {
                            process.WaitForExit(1000);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error waiting for process exit: " + e.Message);
                        }

                        if (process.ProcessName == "explorer") continue;

                        cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype process -cobject {process.Id} -caction terminate";
                        if (Program.UseKernelDriver && process.ProcessName != "explorer") cmdAction.RunTaskOnMainThread();

                        int i = 0;

                        while (i <= 3 && GetProcess().Any(x => x.Id == process.Id && x.ProcessName == process.ProcessName))
                        {
                            try
                            {
                                try
                                {
                                    if (Program.UseKernelDriver)
                                        cmdAction.RunTaskOnMainThread();
                                    else
                                        TerminateProcess(process.Handle, 1);
                                }
                                catch (Exception e) { }

                                process.WaitForExit(500);
                            }
                            catch (Exception e) { }
                            Thread.Sleep(100);
                            i++;
                        }
                        if (i >= 3) Console.WriteLine($"Task kill timeout exceeded.");

                    }
                    InProgress = false;
                    return;
                }
            }
            if (ProcessID.HasValue)
            {
                var process = Process.GetProcessById(ProcessID.Value);
                if (ProcessName != null && ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    try {
                        if (!TerminateProcess(process.Handle, 1))
                            Console.WriteLine("TerminateProcess failed with error code: " + Marshal.GetLastWin32Error());

                        try
                        {
                            process.WaitForExit(1000);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error waiting for process exit: " + e.Message);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Could not open process handle: " + e.Message);
                    }
                }
                else
                {
                    if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success))
                    {
                        bool isCritical = false;
                        try
                        {
                            IntPtr hprocess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, process.Id);
                            IsProcessCritical(hprocess, ref isCritical);
                            CloseHandle(hprocess);
                        }
                        catch (InvalidOperationException e)
                        {
                            Console.WriteLine("Could not check if process is critical.");
                            return;
                        }
                        if (isCritical)
                        {
                            Console.WriteLine($"{process.ProcessName} is a critical process, skipping...");
                            return;
                        }
                    }
                    try
                    {
                        if (!TerminateProcess(process.Handle, 1))
                            Console.WriteLine("TerminateProcess failed with error code: " + Marshal.GetLastWin32Error());
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Could not open process handle: " + e.Message);
                    }
                    try
                    {
                        process.WaitForExit(1000);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error waiting for process exit: " + e.Message);
                    }
                    
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype process -cobject {ProcessID.Value} -caction terminate";
                    if (Program.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                }
                
                int i = 0;
                
                while (i <= 3 && GetProcess().Any(x => x.Id == process.Id && x.ProcessName == process.ProcessName))
                {
                    try
                    {
                        try
                        {
                            if (Program.UseKernelDriver)
                                cmdAction.RunTaskOnMainThread();
                            else
                                TerminateProcess(process.Handle, 1);
                        }
                        catch (Exception e)
                        {
                        }

                        process.WaitForExit(500);
                    }
                    catch (Exception e)
                    {
                    }
                    Thread.Sleep(100);
                    i++;
                }
                if (i >= 3) Console.WriteLine($"Task kill timeout exceeded.");
            }
            else
            {
                var processes = GetProcess();
                if (processes.Count > 0) Console.WriteLine("Processes:");

                foreach (var process in processes)
                {
                    Console.WriteLine(process.ProcessName + " - " + process.Id);

                    if (!RegexNotCritical.Any(x => Regex.Match(process.ProcessName, x, RegexOptions.IgnoreCase).Success))
                    {
                        bool isCritical = false;
                        try
                        {
                            IntPtr hprocess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, process.Id);
                            IsProcessCritical(hprocess, ref isCritical);
                            CloseHandle(hprocess);
                        }
                        catch (InvalidOperationException e)
                        {
                            Console.WriteLine("Could not check if process is critical.");
                            continue;
                        }
                        if (isCritical)
                        {
                            Console.WriteLine($"{process.ProcessName} is a critical process, skipping...");
                            continue;
                        }
                    }
                    try
                    {
                        if (!TerminateProcess(process.Handle, 1))
                            Console.WriteLine("TerminateProcess failed with error code: " + Marshal.GetLastWin32Error());
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Could not open process handle: " + e.Message);
                    }
                    try
                    {
                        process.WaitForExit(1000);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error waiting for process exit: " + e.Message);
                    }
                    
                    if (process.ProcessName == "explorer") continue;
                    
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype process -cobject {process.Id} -caction terminate";
                    if (Program.UseKernelDriver && process.ProcessName != "explorer") cmdAction.RunTaskOnMainThread();

                    int i = 0;

                    while (i <= 3 && GetProcess().Any(x => x.Id == process.Id && x.ProcessName == process.ProcessName))
                    {
                        try
                        {
                            try
                            {
                                if (Program.UseKernelDriver)
                                    cmdAction.RunTaskOnMainThread();
                                else
                                    TerminateProcess(process.Handle, 1);
                            }
                            catch (Exception e)
                            {
                            }

                            process.WaitForExit(500);
                        }
                        catch (Exception e)
                        {
                        }
                        Thread.Sleep(100);
                        i++;
                    }
                    if (i >= 3) Console.WriteLine($"Task kill timeout exceeded.");
                }
            }
            
            InProgress = false;
            return;
        }
    }
}
