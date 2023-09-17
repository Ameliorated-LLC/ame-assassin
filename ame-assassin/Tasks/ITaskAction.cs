using System.Threading.Tasks;

namespace TrustedUninstaller.Shared.Tasks
{
    internal enum Scope
    {
        AllUsers = 0,
        CurrentUser = 1,
        ActiveUsers = 2,
        DefaultUser = 3
    }
    public interface ITaskAction
    {
        public int GetProgressWeight();
        public void ResetProgress();
        public string ErrorString();
        public UninstallTaskStatus GetStatus();
        public void RunTask();
    }
}