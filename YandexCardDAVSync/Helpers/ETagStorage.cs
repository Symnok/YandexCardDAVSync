// Helpers/ETagStorage.cs
using System.Collections.Generic;
using Windows.Storage;

namespace YandexCardDAVSync.Helpers
{
    public static class ETagStorage
    {
        private const string ETagContainer = "CardDAVETags";
        private const string HrefContainer = "CardDAVHrefs";

        public static void SaveAll(Dictionary<string, string> hrefToEtag)
        {
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(ETagContainer,
                ApplicationDataCreateDisposition.Always);
            container.Values.Clear();
            foreach (var kv in hrefToEtag)
                container.Values[SafeKey(kv.Key)] = kv.Key + "|" + kv.Value;
        }

        public static void SaveUidHrefs(Dictionary<string, string> uidToHref)
        {
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(HrefContainer,
                ApplicationDataCreateDisposition.Always);
            container.Values.Clear();
            foreach (var kv in uidToHref)
                if (!string.IsNullOrEmpty(kv.Key))
                    container.Values[SafeKey(kv.Key)] = kv.Key + "|" + kv.Value;
        }

        public static string GetHrefForUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(HrefContainer)) return null;
            var container = settings.Containers[HrefContainer];
            string key    = SafeKey(uid);
            if (!container.Values.ContainsKey(key)) return null;
            string stored = container.Values[key] as string;
            if (string.IsNullOrEmpty(stored)) return null;
            int sep = stored.IndexOf('|');
            return sep >= 0 ? stored.Substring(sep + 1) : null;
        }

        public static Dictionary<string, string> LoadAll()
        {
            var result   = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(ETagContainer)) return result;
            var container = settings.Containers[ETagContainer];
            foreach (var kv in container.Values)
            {
                string stored = kv.Value as string;
                if (string.IsNullOrEmpty(stored)) continue;
                int sep = stored.IndexOf('|');
                if (sep < 0) continue;
                result[stored.Substring(0, sep)] = stored.Substring(sep + 1);
            }
            return result;
        }

        public static Dictionary<string, string> LoadUidHrefs()
        {
            var result   = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(HrefContainer)) return result;
            var container = settings.Containers[HrefContainer];
            foreach (var kv in container.Values)
            {
                string stored = kv.Value as string;
                if (string.IsNullOrEmpty(stored)) continue;
                int sep = stored.IndexOf('|');
                if (sep < 0) continue;
                result[stored.Substring(0, sep)] = stored.Substring(sep + 1);
            }
            return result;
        }

        public static bool HasData()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(ETagContainer)) return false;
            return settings.Containers[ETagContainer].Values.Count > 0;
        }

        public static void Clear()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Containers.ContainsKey(ETagContainer))
                settings.Containers[ETagContainer].Values.Clear();
            if (settings.Containers.ContainsKey(HrefContainer))
                settings.Containers[HrefContainer].Values.Clear();
            LabelStorage.Clear();
            ContactHashStorage.Clear();
        }

        private static string SafeKey(string s)
        {
            return s.Length <= 200 ? s : s.Substring(s.Length - 200);
        }
    }
}
