using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TrustedUninstaller.Shared.Tasks;

namespace TrustedUninstaller.Shared.Actions
{
    public class CmdAction : ITaskAction
    {
        public string Command { get; set; }
        
        public int? Timeout { get; set; }
        
        public bool Wait { get; set; } = true;
        
        public bool ExeDir { get; set; } = false;
        
        public int ProgressWeight { get; set; } = 1;
        
        public int GetProgressWeight() => ProgressWeight;

        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;

        private int? ExitCode { get; set; }

        public string StandardError { get; set; }

        public string StandardOutput { get; set; }

        public string ErrorString() => $"CmdAction failed to run command '{Command}'.";
        
        public UninstallTaskStatus GetStatus()
        {
            if (InProgress)
            {
                return UninstallTaskStatus.InProgress;
            }

            return ExitCode == null ? UninstallTaskStatus.ToDo: UninstallTaskStatus.Completed;
        }
        public void RunTask()
        {
            InProgress = true;
            
            Console.WriteLine($"Running cmd command '{Command}'...");
            
            ExitCode = null;

            var process = new Process();
            var startInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Normal,
                FileName = "cmd.exe",
                Arguments = "/C" + this.Command,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            if (ExeDir) startInfo.WorkingDirectory = Directory.GetCurrentDirectory() + "\\Executables";
            if (!Wait)
            {
                startInfo.RedirectStandardError = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.UseShellExecute = true;
            }
                
            process.StartInfo = startInfo;
            process.Start();
                
            if (!Wait)
            {
                process.Dispose();
                return;
            }
                
            process.OutputDataReceived += ProcOutputHandler;

            if (Wait)
            {
                process.BeginOutputReadLine();
            }

            if (Timeout != null)
            {
                var exited = process.WaitForExit(Timeout.Value);
                if (!exited)
                {
                    process.Kill();
                    throw new TimeoutException($"Command '{Command}' timeout exceeded.");
                }
            }
                
            else process.WaitForExit();

            if (process.ExitCode != 0)
            {
                StandardError = process.StandardError.ReadToEnd();
                Console.WriteLine($"cmd instance exited with error code: {process.ExitCode}");
                if (!String.IsNullOrEmpty(StandardError)) Console.WriteLine($"Error message: {StandardError}");
                this.ExitCode = process.ExitCode;
            }
            else
            {
                ExitCode = 0;
            }
            process.Dispose();
                
            InProgress = false;
            return;
        }

        private static void ProcOutputHandler(object sendingProcess,
         DataReceivedEventArgs outLine)
        {
            var outputString = outLine.Data;

            // Collect the sort command output. 
            if (!String.IsNullOrEmpty(outLine.Data))
            {

                if (outputString.Contains("\\AME"))
                {
                    outputString = outputString.Substring(outputString.IndexOf('>') + 1);
                }
                Console.WriteLine(outputString);
            }
            else
            {
                Console.WriteLine();
            }
        }
    }
}
