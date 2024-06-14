using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.TaskScheduler;
using TrustedUninstaller.Shared.Tasks;

namespace ame_assassin
{
    internal enum ScheduledTaskOperation
    {
        Delete = 0,
        Enable = 1,
        Disable = 2,
        DeleteFolder = 3
    }

    internal class ScheduledTaskAction : ITaskAction
    {
        public void RunTaskOnMainThread() { throw new NotImplementedException(); }
        public ScheduledTaskOperation Operation { get; set; } = ScheduledTaskOperation.Delete;
        public string? RawTask { get; set; } = null;
        public string Path { get; set; }

        public int ProgressWeight { get; set; } = 1;
        public int GetProgressWeight() => ProgressWeight;

        private bool InProgress { get; set; } = false;
        public void ResetProgress() => InProgress = false;

        public string ErrorString() => $"ScheduledTaskAction failed to change task {Path} to state {Operation.ToString()}";

        public UninstallTaskStatus GetStatus()
        {
            if (InProgress)
            {
                return UninstallTaskStatus.InProgress;
            }
            
            using TaskService ts = new TaskService();

            if (Operation != ScheduledTaskOperation.DeleteFolder)
            {
                var task = ts.GetTask(Path);
                if (task is null)
                {
                    return Operation == ScheduledTaskOperation.Delete ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
                }

                if (task.Enabled)
                {
                    return Operation == ScheduledTaskOperation.Enable ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
                }

                return Operation == ScheduledTaskOperation.Disable ?
                    UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
            }
            else
            {
                var folder = ts.GetFolder(Path);
                if (folder == null)
                    return UninstallTaskStatus.Completed;
                
                return folder.GetTasks().Any() ? UninstallTaskStatus.ToDo : UninstallTaskStatus.Completed;
            }
        }

        public void RunTask()
        {
            if (GetStatus() == UninstallTaskStatus.Completed)
            {
                return;
            }

            Console.WriteLine($"{Operation.ToString().TrimEnd('e')}ing scheduled task '{Path}'...");

            using TaskService ts = new TaskService();

            InProgress = true;

            if (Operation != ScheduledTaskOperation.DeleteFolder)
            {

                var task = ts.GetTask(Path);
                if (task is null)
                {
                    if (Operation == ScheduledTaskOperation.Delete)
                    {
                        return;
                    }

                    if (RawTask is null || RawTask.Length == 0)
                    {
                        return;
                    }
                }

                switch (Operation)
                {
                    case ScheduledTaskOperation.Delete:
                        // TODO: This will probably not work if we actually use sub-folders
                        ts.RootFolder.DeleteTask(Path);
                        break;
                    case ScheduledTaskOperation.Enable:
                    case ScheduledTaskOperation.Disable:
                        {
                            if (task is null && !(RawTask is null))
                            {
                                task = ts.RootFolder.RegisterTask(Path, RawTask);
                            }

                            if (!(task is null))
                            {
                                task.Enabled = Operation == ScheduledTaskOperation.Enable;
                            }
                            else
                            {
                                throw new ArgumentException($"Task provided is null.");
                            }

                            break;
                        }
                    default:
                        throw new ArgumentException($"Argument out of range.");
                }

                InProgress = false;
                return;
            }
            else
            {
                var folder = ts.GetFolder(Path);

                if (folder is null) return;
                
                folder.GetTasks().ToList().ForEach(x => folder.DeleteTask(x.Name));

                try
                {
                    folder.Parent.DeleteFolder(folder.Name);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }

                InProgress = false;
                return;
            }
        }
    }
}
