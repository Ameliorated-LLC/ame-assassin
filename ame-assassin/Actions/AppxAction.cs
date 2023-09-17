using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using TrustedUninstaller.Shared.Tasks;
using System.Diagnostics;

namespace TrustedUninstaller.Shared.Actions
{
    // Integrate ame-assassin later
    internal class AppxAction : ITaskAction
    {
        public string DisplayName { get; set; }
        
        public int ProgressWeight { get; set; } = 1;
        public int GetProgressWeight() => ProgressWeight;
        
        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;

        public string ErrorString() => $"AppxAction failed to remove {DisplayName}.";
        
        private Package GetPackage()
        {
            var packageManager = new PackageManager();

            return packageManager.FindPackages().FirstOrDefault(package => package.Id.Name == DisplayName);
        }
        public UninstallTaskStatus GetStatus()
        {
            if (InProgress) return UninstallTaskStatus.InProgress;
            return GetPackage() == null ? UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
        }

        public void RunTask()
        {
            if (InProgress) throw new TaskInProgressException("Another Appx action was called while one was in progress.");
            InProgress = true;

            Console.WriteLine($"Removing Appx item '{DisplayName}'...");
            
            int exitCode = 0;
            var psi = new ProcessStartInfo()
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                Arguments = $"get-appxpackage *{DisplayName}* | remove-appxpackage",
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = Process.Start(psi);

            proc.WaitForExit();
            var output = proc.StandardOutput.ReadToEndAsync();
            var errorOutput = proc.StandardError.ReadToEndAsync();
            Console.WriteLine($"Output: {output}");
            Console.WriteLine($"Error output: {errorOutput}");
            exitCode =  proc.ExitCode;

            InProgress = false;
            return;
        }
    }
}
