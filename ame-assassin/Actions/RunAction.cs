using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrustedUninstaller.Shared.Tasks;

namespace ame_assassin
{
    public enum Privilege
    {
        TrustedInstaller,
        System,
        CurrentUserElevated,
        CurrentUser,
    }
    
    public class RunAction : ITaskAction
    {
        public void RunTaskOnMainThread()
        {
            RunAsProcess(Exe);
        }
       
        public string Exe { get; set; } 

        public string? Arguments { get; set; }
        
        public bool CreateWindow { get; set; } = false;
        
        public int? Timeout { get; set; }
        public bool Wait { get; set; } = true;
        
        public int ProgressWeight { get; set; } = 5;
        public int GetProgressWeight() => ProgressWeight;

        private bool InProgress { get; set; } = false;
        public void ResetProgress() => InProgress = false;
        private bool HasExited { get; set; } = false;
        //public int ExitCode { get; set; }
        public string? Output { get; private set; }
        private string? StandardError { get; set; }
        
        public string ErrorString() => String.IsNullOrEmpty(Arguments) ? $"RunAction failed to execute '{Exe}'." : $"RunAction failed to execute '{Exe}' with arguments '{Arguments}'.";
        
        public static bool ExistsInPath(string fileName)
        {
            if (File.Exists(fileName))
                return true;

            var values = Environment.GetEnvironmentVariable("PATH");
            foreach (var path in values.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, fileName);
                if (File.Exists(fullPath))
                    return true;
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath + ".exe"))
                    return true;
            }
            return false;
        }
        
        public UninstallTaskStatus GetStatus()
        {
            if (InProgress)
            {
                return UninstallTaskStatus.InProgress;
            }

            return HasExited || !Wait ?  UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
        }

        public void RunTask()
        {
            throw new NotImplementedException();
        }

        private void RunAsProcess(string file)
        {
            var startInfo = new ProcessStartInfo
            {
                CreateNoWindow = !this.CreateWindow,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal,
                RedirectStandardError = false,
                RedirectStandardOutput = false,
                FileName = file,
            };
            if (Arguments != null) startInfo.Arguments = Environment.ExpandEnvironmentVariables(Arguments);

            if (!Wait)
            {
                startInfo.RedirectStandardError = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.UseShellExecute = true;
            }

            var exeProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            exeProcess.Start();

            if (!Wait)
            {
                exeProcess.Dispose();
                return;
            }

            if (Timeout.HasValue)
            {
                var exited = exeProcess.WaitForExit(Timeout.Value);
                if (!exited)
                {
                    exeProcess.Kill();
                    throw new TimeoutException($"Executable run timeout exceeded.");
                }
            }
            else
            {
                bool exited = exeProcess.WaitForExit(30000);

                // WaitForExit alone seems to not be entirely reliable
                while (!exited && ExeRunning(exeProcess.ProcessName, exeProcess.Id))
                {
                    exited = exeProcess.WaitForExit(30000);
                }
            }

            HasExited = true;
            exeProcess.Dispose();
        }
        
        private static bool ExeRunning(string name, int id)
        {
            try
            {
                return Process.GetProcessesByName(name).Any(x => x.Id == id);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
