// Helpers/BackgroundTaskHelper.cs
using System.Threading.Tasks;
using System;
using Windows.ApplicationModel.Background;

namespace YandexCardDAVSync.Helpers
{
    public static class BackgroundTaskHelper
    {
        private const string TaskName       = "YandexToPhoneSyncTask";
        private const string TaskEntryPoint = "SyncComponent.YandexToPhoneSyncTask";

        public static async Task<bool> RegisterAsync()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
                if (t.Value.Name == TaskName) return true;

            var status = await BackgroundExecutionManager.RequestAccessAsync().AsTask();
            if (status == BackgroundAccessStatus.DeniedByUser         ||
                status == BackgroundAccessStatus.DeniedBySystemPolicy ||
                status == BackgroundAccessStatus.Unspecified)
                return false;

            var builder = new BackgroundTaskBuilder
            {
                Name           = TaskName,
                TaskEntryPoint = TaskEntryPoint,
                IsNetworkRequested = true
            };

            builder.SetTrigger(new TimeTrigger(15, false));
            builder.AddCondition(
                new SystemCondition(SystemConditionType.InternetAvailable));

            builder.Register();
            return true;
        }

        public static void Unregister()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
            {
                if (t.Value.Name == TaskName)
                {
                    t.Value.Unregister(true);
                    return;
                }
            }
        }

        public static bool IsRegistered()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
                if (t.Value.Name == TaskName) return true;
            return false;
        }
    }
}
