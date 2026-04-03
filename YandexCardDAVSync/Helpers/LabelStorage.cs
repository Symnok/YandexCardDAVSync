// Helpers/LabelStorage.cs
using System.Collections.Generic;
using Windows.Storage;

namespace YandexCardDAVSync.Helpers
{
    public static class LabelStorage
    {
        private const string ContainerName = "ContactLabels";

        public static void SaveLabels(string uid, Dictionary<string, string> labels)
        {
            if (string.IsNullOrEmpty(uid) || labels == null) return;
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(ContainerName,
                ApplicationDataCreateDisposition.Always);
            var parts = new List<string>();
            foreach (var kv in labels)
                parts.Add(kv.Key + "=" + kv.Value);
            container.Values[SafeKey(uid)] = string.Join("|", parts);
        }

        public static Dictionary<string, string> LoadLabels(string uid)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(uid)) return result;
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(ContainerName)) return result;
            var container = settings.Containers[ContainerName];
            string key    = SafeKey(uid);
            if (!container.Values.ContainsKey(key)) return result;
            string stored = container.Values[key] as string;
            if (string.IsNullOrEmpty(stored)) return result;
            foreach (var part in stored.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                result[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return result;
        }

        public static void Clear()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Containers.ContainsKey(ContainerName))
                settings.Containers[ContainerName].Values.Clear();
        }

        private static string SafeKey(string s)
        {
            return s.Length <= 200 ? s : s.Substring(s.Length - 200);
        }
    }
}
