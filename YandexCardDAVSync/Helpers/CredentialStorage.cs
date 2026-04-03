// Helpers/CredentialStorage.cs
// Securely stores Yandex address + App Password in the Windows Credential Locker.

using Windows.Security.Credentials;

namespace YandexCardDAVSync.Helpers
{
    public static class CredentialStorage
    {
        private const string ResourceName = "YandexCardDAVSync";

        public static void Save(string yandexAddress, string appPassword)
        {
            Delete();
            var vault      = new PasswordVault();
            var credential = new PasswordCredential(
                ResourceName, yandexAddress, appPassword);
            vault.Add(credential);
        }

        public static PasswordCredential Load()
        {
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource(ResourceName);
                if (creds == null || creds.Count == 0) return null;
                var cred = creds[0];
                cred.RetrievePassword();
                return cred;
            }
            catch { return null; }
        }

        public static bool HasCredentials()
        {
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource(ResourceName);
                return creds != null && creds.Count > 0;
            }
            catch { return false; }
        }

        public static void Delete()
        {
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource(ResourceName);
                if (creds == null) return;
                foreach (var c in creds)
                    vault.Remove(c);
            }
            catch { }
        }
    }
}
