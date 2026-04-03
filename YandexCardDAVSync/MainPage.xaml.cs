using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using YandexCardDAVSync.Helpers;
using YandexCardDAVSync.Services;
using Windows.Storage;
using Windows.ApplicationModel.Background;

namespace YandexCardDAVSync
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var saved = CredentialStorage.Load();
            if (saved != null)
            {
                TxtEmail.Text        = saved.UserName;
                TxtPassword.Password = saved.Password;
                BtnForget.Visibility   = Visibility.Visible;
                TxtLastSync.Visibility = Visibility.Visible;
                TxtLastSync.Text       = "Credentials loaded from secure storage";
                await BackgroundTaskHelper.RegisterAsync();
            }
            UpdateBgStatus();
        }

        private void UpdateBgStatus()
        {
            bool registered  = BackgroundTaskHelper.IsRegistered();
            TxtBgStatus.Text = registered ? "Every 15 min" : "Not registered";
            TxtBgStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                registered ? Windows.UI.Colors.Green : Windows.UI.Colors.Gray);

            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("LastBgSync"))
            {
                TxtLastBgSync.Text       = "Last: " + settings.Values["LastBgSync"];
                TxtLastBgSync.Visibility = Visibility.Visible;
            }
        }

        private async void UpdateProgress(string msg)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                TxtProgress.Text = msg);
        }

        // ================================================================
        // Validate credentials
        // ================================================================
        private bool ValidateInputs(out string email, out string password)
        {
            email    = TxtEmail.Text.Trim();
            password = TxtPassword.Password.Trim().Replace(" ", "");

            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
            {
                ShowError("Please enter a valid Yandex address (e.g. user@yandex.ru).");
                return false;
            }
            if (string.IsNullOrEmpty(password) || password.Length < 16)
            {
                ShowError("Please enter your Yandex App Password (16 characters).\n" +
                          "NOT your regular Yandex account password.");
                return false;
            }
            return true;
        }

        private void SaveCredentialsIfRequested(string email, string password)
        {
            if (ChkSaveCredentials.IsChecked == true)
            {
                CredentialStorage.Save(email, password);
                BtnForget.Visibility = Visibility.Visible;
            }
        }

        // ================================================================
        // BUTTON 1: Yandex → Phone
        // ================================================================
        private async void BtnYandexToPhone_Click(object sender, RoutedEventArgs e)
        {
            string email, password;
            if (!ValidateInputs(out email, out password)) return;
            SaveCredentialsIfRequested(email, password);

            SetUiBusy(true);
            HideAllBanners();

            try
            {
                IProgress<string> progress = new Progress<string>(
                    msg => UpdateProgress(msg));

                var cardDav = new CardDavService(email, password);
                var store   = new ContactStoreService();
                int count   = 0;
                string mode = "";

                bool isFirstSync = !ETagStorage.HasData();

                if (isFirstSync)
                {
                    mode = "full";
                    var fetchResult = await cardDav.FetchAllContactsAsync(progress);
                    var contacts    = fetchResult.Contacts;
                    var etags       = fetchResult.Etags;

                    if (contacts.Count == 0)
                    {
                        ShowError("No contacts found in your Yandex account.");
                        return;
                    }

                    count = await store.SyncAsync(contacts, progress);
                    ETagStorage.SaveAll(etags);

                    var uidToHref = new Dictionary<string, string>();
                    foreach (var c in contacts)
                        if (!string.IsNullOrEmpty(c.Uid) && !string.IsNullOrEmpty(c.Href))
                            uidToHref[c.Uid] = c.Href;
                    ETagStorage.SaveUidHrefs(uidToHref);
                }
                else
                {
                    mode = "incremental";
                    var localEtags = ETagStorage.LoadAll();
                    var diff       = await cardDav.GetChangesAsync(localEtags, progress);

                    if (diff.ChangedHrefs.Count == 0 && diff.DeletedHrefs.Count == 0)
                    {
                        ETagStorage.SaveAll(diff.ServerEtags);
                        TxtSuccess.Text          = "All contacts up to date. No changes.";
                        BannerSuccess.Visibility = Visibility.Visible;
                        TxtLastSync.Text         = "Last sync: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                        TxtLastSync.Visibility   = Visibility.Visible;
                        return;
                    }

                    progress.Report(diff.ChangedHrefs.Count + " to update, " +
                                    diff.DeletedHrefs.Count + " to delete.");

                    if (diff.ChangedHrefs.Count > 0)
                    {
                        var changed = await cardDav.FetchContactsByHrefsAsync(
                            diff.ChangedHrefs, diff.ServerEtags, progress);

                        var updatedUidHrefs = ETagStorage.LoadUidHrefs();
                        foreach (var vc in changed)
                        {
                            await store.UpsertContactAsync(vc);
                            count++;
                            if (!string.IsNullOrEmpty(vc.Uid) &&
                                !string.IsNullOrEmpty(vc.Href))
                                updatedUidHrefs[vc.Uid] = vc.Href;
                        }
                        ETagStorage.SaveUidHrefs(updatedUidHrefs);
                    }

                    if (diff.DeletedHrefs.Count > 0)
                    {
                        progress.Report("Removing " + diff.DeletedHrefs.Count +
                                        " deleted contacts...");
                        foreach (var href in diff.DeletedHrefs)
                        {
                            string uid = UidFromHref(href);
                            if (!string.IsNullOrEmpty(uid))
                                await store.DeleteContactByUidAsync(uid);
                        }
                    }

                    ETagStorage.SaveAll(diff.ServerEtags);
                }

                string when = DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                TxtSuccess.Text = mode == "full"
                    ? count + " contacts synced from Yandex (full sync)."
                    : count + " contacts updated from Yandex (incremental).";
                BannerSuccess.Visibility = Visibility.Visible;
                TxtLastSync.Text         = "Last sync: " + when;
                TxtLastSync.Visibility   = Visibility.Visible;

                await BackgroundTaskHelper.RegisterAsync();
                UpdateBgStatus();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetUiBusy(false);
            }
        }

        // ================================================================
        // BUTTON 2: Phone → Yandex
        // ================================================================
        private async void BtnPhoneToYandex_Click(object sender, RoutedEventArgs e)
        {
            string email, password;
            if (!ValidateInputs(out email, out password)) return;
            SaveCredentialsIfRequested(email, password);

            SetUiBusy(true);
            HideAllBanners();

            try
            {
                IProgress<string> progress = new Progress<string>(
                    msg => UpdateProgress(msg));

                var store   = new ContactStoreService();
                var cardDav = new CardDavService(email, password);

                var phoneContacts = await store.ReadAllContactsAsync(progress);

                if (phoneContacts.Count == 0)
                {
                    ShowError("No contacts found on phone.\n" +
                              "Run Yandex → Phone sync first.");
                    return;
                }

                var savedHashes = ContactHashStorage.LoadAll();
                var changed     = new List<Windows.ApplicationModel.Contacts.Contact>();

                foreach (var contact in phoneContacts)
                    if (!ContactHashStorage.IsUnchanged(contact, savedHashes))
                        changed.Add(contact);

                if (changed.Count == 0)
                {
                    TxtSuccess.Text          = "No changes detected on phone.";
                    BannerSuccess.Visibility = Visibility.Visible;
                    TxtLastSync.Text         = "Last check: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                    TxtLastSync.Visibility   = Visibility.Visible;
                    return;
                }

                progress.Report("Found " + changed.Count + " changed contact(s). Uploading...");

                int uploaded = 0, failed = 0, i = 0;
                var currentHashes = ContactHashStorage.LoadAll();

                foreach (var contact in changed)
                {
                    bool wasNew    = string.IsNullOrEmpty(contact.RemoteId);
                    string usedUid = await cardDav.UploadContactAsync(contact, progress);

                    if (usedUid != null)
                    {
                        uploaded++;
                        if (wasNew)
                            await store.SaveRemoteIdAsync(contact.Id, usedUid);
                        currentHashes[usedUid] = ContactHashStorage.ComputeHash(contact);
                    }
                    else failed++;

                    i++;
                    if (i % 5 == 0)
                        progress.Report("Uploaded " + i + " of " + changed.Count + "...");
                }

                ContactHashStorage.SaveAll(currentHashes);

                string result = uploaded + " of " + phoneContacts.Count +
                                " contacts uploaded to Yandex (" +
                                changed.Count + " changed).";
                if (failed > 0) result += "\n" + failed + " failed.";

                TxtSuccess.Text          = result;
                BannerSuccess.Visibility = Visibility.Visible;
                TxtLastSync.Text         = "Last upload: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                TxtLastSync.Visibility   = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetUiBusy(false);
            }
        }

        // ================================================================
        // FORGET button
        // ================================================================
        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            BackgroundTaskHelper.Unregister();
            CredentialStorage.Delete();
            ETagStorage.Clear();
            TxtEmail.Text            = string.Empty;
            TxtPassword.Password     = string.Empty;
            BtnForget.Visibility     = Visibility.Collapsed;
            TxtLastSync.Visibility   = Visibility.Collapsed;
            TxtLastBgSync.Visibility = Visibility.Collapsed;
            HideAllBanners();
            UpdateBgStatus();
        }

        // ================================================================
        // Helpers
        // ================================================================
        private string UidFromHref(string href)
        {
            int lastSlash = href.LastIndexOf('/');
            if (lastSlash < 0) return string.Empty;
            return href.Substring(lastSlash + 1);
        }

        private void SetUiBusy(bool busy)
        {
            BtnYandexToPhone.IsEnabled = !busy;
            BtnPhoneToYandex.IsEnabled = !busy;
            TxtEmail.IsEnabled         = !busy;
            TxtPassword.IsEnabled      = !busy;
            PanelProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) TxtProgress.Text = string.Empty;
        }

        private void ShowError(string message)
        {
            TxtError.Text            = message;
            BannerError.Visibility   = Visibility.Visible;
            BannerSuccess.Visibility = Visibility.Collapsed;
        }

        private void HideAllBanners()
        {
            BannerError.Visibility   = Visibility.Collapsed;
            BannerSuccess.Visibility = Visibility.Collapsed;
        }
    }
}
