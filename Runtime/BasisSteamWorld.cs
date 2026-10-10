using Basis.BasisUI;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Steam
{
    public sealed class BasisSteamWorldCheck
    {
        public bool IsValid;
        public string Error = string.Empty, Url = string.Empty, Password = string.Empty, Name = string.Empty;
    }
    public static class BasisSteamWorld
    {
        public static async Task<BasisSteamWorldCheck> ValidateAsync(string url, string password)
        {
            BasisSteamWorldCheck check = new BasisSteamWorldCheck();
            InputValidation.EntryValidationResponse entry = InputValidation.ValidateEntry(url, password, Array.Empty<BasisDataStoreItemKeys.ItemKey>());
            if (entry.Result != InputValidation.EntryValidationResult.Success)
            {
                check.Error = entry.Result switch
                {
                    InputValidation.EntryValidationResult.EmptyUrl => BasisLocalization.Get("steam.world.error.emptyUrl"),
                    InputValidation.EntryValidationResult.EmptyPassword => BasisLocalization.Get("steam.world.error.emptyPassword"),
                    _ => BasisLocalization.Get("steam.world.error.invalidUrl"),
                };
                return check;
            }
            if (!Uri.TryCreate(entry.ProcessedUrl, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                check.Error = BasisLocalization.Get("steam.world.error.notRemote");
                return check;
            }
            BasisDataStoreItemKeys.ItemKey item = new BasisDataStoreItemKeys.ItemKey
            {
                Url = entry.ProcessedUrl,
                Pass = entry.Password,
                Mode = 0,
            };
            LibraryProvider.BasisLoadableBundleWrapper wrapper = LibraryProvider.CreateNewWrapperFromItem(item);
            BasisMetaLoadResult meta;
            try
            {
                using CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                meta = await BasisBeeManagement.HandleMetaOnlyLoad(wrapper.basisTrackedBundleWrapper, new BasisProgressReport(), cancel.Token);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Steam lobby world check failed for {entry.ProcessedUrl}: {ex.Message}", BasisDebug.LogTag.Networking);
                check.Error = BasisLocalization.Get("steam.world.error.unreadable");
                return check;
            }
            if (!meta.Loaded)
            {
                check.Error = BasisLocalization.Get("steam.world.error.unreadable");
                return check;
            }
            LibraryProvider.BasisLoadableBundleWrapper loaded = await LibraryProvider.LoadWrapperFromDisc(item, wrapper);
            BasisBundleConnector connector = loaded?.BasisLoadableBundle?.BasisBundleConnector;
            if (LibraryProvider.ResolveModeFromConnector(connector) != BundledContentHolder.Mode.World)
            {
                check.Error = BasisLocalization.Get("steam.world.error.notWorld");
                return check;
            }
            string name = connector.BasisBundleDescription?.AssetBundleName;
            check.Name = string.IsNullOrWhiteSpace(name) ? uri.Segments[uri.Segments.Length - 1] : name;
            check.Url = entry.ProcessedUrl;
            check.Password = entry.Password;
            check.IsValid = true;
            return check;
        }
    }
}
