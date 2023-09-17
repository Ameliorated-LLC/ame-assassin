#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ame_assassin;
using Microsoft.Win32;
using TrustedUninstaller.Shared.Tasks;

namespace TrustedUninstaller.Shared.Actions
{
    internal enum ServiceOperation
    {
        Stop,
        Continue,
        Start,
        Pause,
        Delete,
        Change
    }
    internal class ServiceAction : ITaskAction
    {
        public ServiceOperation Operation { get; set; } = ServiceOperation.Delete;
        
        public string ServiceName { get; set; } = null!;
        
        public int? Startup { get; set; }
        
        public bool DeleteStop { get; set; } = true;
        
        public bool RegistryDelete { get; set; } = false;
        
        public bool Device { get; set; } = false;
        
        public int ProgressWeight { get; set; } = 4;
        public int GetProgressWeight() => ProgressWeight;
        
        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;

        public string ErrorString() => $"ServiceAction failed to {Operation.ToString().ToLower()} service {ServiceName}.";
        
        private ServiceController? GetService()
        {
            if (ServiceName.EndsWith("*") && ServiceName.StartsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.IndexOf(ServiceName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (ServiceName.EndsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.StartsWith(ServiceName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase));
            if (ServiceName.StartsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.EndsWith(ServiceName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase));
            
            return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.Equals(ServiceName, StringComparison.CurrentCultureIgnoreCase));
        }
        private ServiceController? GetDevice()
        {
            if (ServiceName.EndsWith("*") && ServiceName.StartsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.IndexOf(ServiceName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (ServiceName.EndsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.StartsWith(ServiceName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase));
            if (ServiceName.StartsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.EndsWith(ServiceName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase));
            
            return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.Equals(ServiceName, StringComparison.CurrentCultureIgnoreCase));
        }

        public UninstallTaskStatus GetStatus()
        {
            if (InProgress) return UninstallTaskStatus.InProgress;

            using(var serviceController = GetService())
            {
                if (Operation == ServiceOperation.Change && Startup.HasValue)
                {
                    // TODO: Implement dev log. Example:
                    // if (Registry.LocalMachine.OpenSubKey($@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}") == null) WriteToDevLog($"Warning: Service name '{ServiceName}' not found in registry.");
                    
                    var root = Registry.LocalMachine.OpenSubKey($@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}");
                    if (root == null) return UninstallTaskStatus.Completed;

                    var value = root.GetValue("Start");

                    return (int)value == Startup.Value ? UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
                }
                if (Operation == ServiceOperation.Delete && RegistryDelete)
                {
                    // TODO: Implement dev log. Example:
                    // if (Registry.LocalMachine.OpenSubKey($@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}") == null) WriteToDevLog($"Warning: Service name '{ServiceName}' not found in registry.");
                    
                    var root = Registry.LocalMachine.OpenSubKey($@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}");
                    return root == null ? UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
                }
                
                return Operation switch
                {
                    ServiceOperation.Stop =>
                    serviceController?.Status == ServiceControllerStatus.Stopped
                    || serviceController?.Status == ServiceControllerStatus.StopPending ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                    ServiceOperation.Continue =>
                    serviceController?.Status == ServiceControllerStatus.Running
                    || serviceController?.Status == ServiceControllerStatus.ContinuePending ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                    ServiceOperation.Start =>
                    serviceController?.Status == ServiceControllerStatus.StartPending
                    || serviceController?.Status == ServiceControllerStatus.Running ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                    ServiceOperation.Pause =>
                    serviceController?.Status == ServiceControllerStatus.Paused
                    || serviceController?.Status == ServiceControllerStatus.PausePending ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                    ServiceOperation.Delete =>
                    serviceController == null ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                    _ => throw new ArgumentOutOfRangeException("Argument out of Range", new ArgumentOutOfRangeException())
                };
            }
            
        }
        private readonly string[] RegexNoKill = { "DcomLaunch" }; 

        public void RunTask()
        {
            if (Operation == ServiceOperation.Change && !Startup.HasValue) throw new ArgumentException("Startup property must be specified with the change operation.");
            if (Operation == ServiceOperation.Change && (Startup.Value > 4  || Startup.Value < 0)) throw new ArgumentException("Startup property must be between 1 and 4.");

            // This is a little cursed but it works and is concise lol
            Console.WriteLine($"{Operation.ToString().Replace("Stop", "Stopp").TrimEnd('e')}ing services matching '{ServiceName}'...");
            
            ServiceController? service;
            
            if (Device) service = GetDevice();
            else service = GetService();

            if (service == null && Operation != ServiceOperation.Change)
            {
                Console.WriteLine($"No services found matching '{ServiceName}'.");
                //ErrorLogger.WriteToErrorLog($"The service matching '{ServiceName}' does not exist.", Environment.StackTrace, "ServiceAction Error");
                return;
            }

            InProgress = true;

            
            var cmdAction = new CmdAction();

            if (Operation == ServiceOperation.Delete || Operation == ServiceOperation.Stop)
            {
                if (RegexNoKill.Any(regex => Regex.Match(ServiceName, regex, RegexOptions.IgnoreCase).Success))
                {
                    Console.WriteLine($"Skipping {ServiceName}...");
                    return;
                }
                
                foreach (ServiceController dependentService in service.DependentServices)
                {
                    Console.WriteLine($"Killing dependent service {dependentService.ServiceName}...");
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {dependentService.ServiceName} -caction stop";
                    cmdAction.RunTask();
                    
                    Console.WriteLine("Waiting for the service to stop...");
                    int delay = 100;
                    while (service.Status != ServiceControllerStatus.Stopped && delay <= 800)
                    {
                        service.Refresh();
                        //Wait for the service to stop
                        Task.Delay(delay).Wait();
                        delay += 100;
                    }
                    if (delay >= 800)
                    {
                        Console.WriteLine("\r\nService stop timeout exceeded. Trying second method...");
                        
                        try
                        {
                            using var search = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name='{service.ServiceName}'");

                            foreach (ManagementObject queryObj in search.Get())
                            {
                                var serviceId = (UInt32)queryObj["ProcessId"]; // Access service name  
                                
                                var killServ = new TaskKillAction()
                                {
                                ProcessID = (int)serviceId
                                };
                                killServ.RunTask();
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"\r\nError: Could not kill dependent service {dependentService.ServiceName}.");
                        }
                    }
                }

                if (service.ServiceName == "SgrmAgent" && ((Operation == ServiceOperation.Delete && DeleteStop) || Operation == ServiceOperation.Stop))
                {
                    new TaskKillAction() { ProcessName = "SgrmBroker" }.RunTask();
                }
            }

            if (Operation == ServiceOperation.Delete)
            {

                if (DeleteStop && service.Status != ServiceControllerStatus.StopPending && service.Status != ServiceControllerStatus.Stopped)
                {
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {service.ServiceName} -caction stop";
                    cmdAction.RunTask();
                        
                }

                Console.WriteLine("Waiting for the service to stop...");
                int delay = 100;
                while (DeleteStop && service.Status != ServiceControllerStatus.Stopped && delay <= 1200)
                {
                    service.Refresh();
                    //Wait for the service to stop
                    Task.Delay(delay);
                    delay += 100;
                }
                if (delay >= 1200)
                {
                    Console.WriteLine("\r\nService stop timeout exceeded. Trying second method...");
                        
                    try
                    {
                        using var search = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name='{service.ServiceName}'");

                        foreach (ManagementObject queryObj in search.Get())
                        {
                            var serviceId = (UInt32)queryObj["ProcessId"]; // Access service name  
                                
                            var killServ = new TaskKillAction()
                            {
                                ProcessID = (int)serviceId
                            };
                            killServ.RunTask();
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not kill service {service.ServiceName}.");
                    }
                }

                if (RegistryDelete)
                {
                    var action = new RegistryKeyAction()
                    {
                        KeyName = $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}",
                        Operation = RegistryKeyOperation.Delete
                    };
                    action.RunTask();
                }
                else
                {
                    cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {service.ServiceName} -caction delete";
                    cmdAction.RunTask();
                }
            }
            else if (Operation != ServiceOperation.Change)
            {
                cmdAction.Command = Program.ProcessHacker + $" -s -elevate -c -ctype service -cobject {ServiceName} -caction {Operation.ToString().ToLower()}";
                cmdAction.RunTask();
            }
            else
            {
                var action = new RegistryValueAction()
                {
                    KeyName = $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}", 
                    Value = "Start", 
                    Data = Startup.Value, 
                    Type = RegistryValueType.REG_DWORD, 
                    Operation = RegistryValueOperation.Set
                };
                action.RunTask();
            }

            service?.Dispose();
            Task.Delay(100);

            InProgress = false;
            return;
        }
    }
}
