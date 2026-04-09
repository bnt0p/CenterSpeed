using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Kxnrl.SteamApi;
using Kxnrl.SteamApi.Api;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using SteamDatabase.ValvePak;
using ISteamApi = Sharp.Shared.Objects.ISteamApi;

internal class AddonManager : ISteamListener, IGameListener
{
    private const string SteamWebApiKey = "4F74FDD0D7F635F2F3B95B0B05A71AEC";
    private const int MaxRetries = 5;

    private string _vpkPath = null!;
    private readonly string _lastUpdateTimeFile;
    private string _contentPath = null!;
    private DateTime _addonLastUpdateTime;
    private ulong _currentAddonId;
    private bool _restarted;

    private readonly ConcurrentDictionary<ulong, int> _retryCounts = [];
    private readonly ConcurrentDictionary<ulong, DateTime> _pendingUpdateTimes = [];

    private readonly ISharedSystem _sharedSystem;
    private readonly string _sharpPath;
    private readonly string _assetPath;
    private readonly ILogger<AddonManager> _logger;
    private readonly ISteamApi _steamApi;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clientManager;

    public AddonManager(
        ISharedSystem sharedSystem,
        string sharpPath,
        string assetPath,
        ILogger<AddonManager> logger)
    {
        _sharedSystem = sharedSystem;
        _sharpPath = sharpPath;
        _assetPath = assetPath;
        _modSharp = sharedSystem.GetModSharp();
        _clientManager = sharedSystem.GetClientManager();
        _logger = logger;
        _steamApi = _modSharp.GetSteamGameServer();

        _logger.LogInformation("[Ctor] sharpPath={sharp}, assetPath={asset}", _sharpPath, _assetPath);

        _lastUpdateTimeFile = Path.GetFullPath(Path.Combine(assetPath, "last_update_time.txt"));

        _logger.LogInformation("[Ctor] lastUpdateFile={file}", _lastUpdateTimeFile);

        UpdateAddonId(3668423973, false);
    }

    public bool Init()
    {
        _logger.LogInformation("[Init] Installing listeners");

        _modSharp.InstallGameListener(this);
        _modSharp.InstallSteamListener(this);

        return true;
    }

    public void Shutdown()
    {
        _logger.LogInformation("[Shutdown] Removing listeners");

        _modSharp.RemoveGameListener(this);
        _modSharp.RemoveSteamListener(this);
    }

    public void OnPostInit()
    {
        _logger.LogInformation("[OnPostInit] Loading last update time");
        UpdateAddonPublishTime();
    }

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 1;

    int ISteamListener.ListenerVersion => ISteamListener.ApiVersion;
    int ISteamListener.ListenerPriority => 1;

    public void OnSteamServersConnected()
        => _logger.LogInformation("[Steam] Connected");

    public void OnSteamServersDisconnected(SteamApiResult reason)
        => _logger.LogWarning("[Steam] Disconnected reason={reason}", reason);

    public void OnSteamServersConnectFailure(SteamApiResult reason, bool stillRetrying)
        => _logger.LogError("[Steam] Connect failure reason={reason}, retrying={retry}", reason, stillRetrying);

    public void OnDownloadItemResult(ulong sharedFileId, SteamApiResult result)
    {
        _logger.LogInformation("[DownloadResult] id={id}, result={result}", sharedFileId, result);

        if (sharedFileId != _currentAddonId)
        {
            _logger.LogDebug("Ignoring result for non-active addon {id}", sharedFileId);
            return;
        }

        switch (result)
        {
            case SteamApiResult.Success:
                HandleDownloadSuccess(sharedFileId);
                break;

            case SteamApiResult.Fail:
            case SteamApiResult.NoConnection:
                HandleDownloadRetry(sharedFileId, result);
                break;

            default:
                _logger.LogError("Unhandled result {result} for addon {id}", result, sharedFileId);
                _pendingUpdateTimes.TryRemove(sharedFileId, out _);
                _retryCounts.TryRemove(sharedFileId, out _);
                break;
        }
    }

    private void HandleDownloadSuccess(ulong sharedFileId)
    {
        _logger.LogInformation("[HandleDownloadSuccess] id={id}", sharedFileId);

        if (_pendingUpdateTimes.TryRemove(sharedFileId, out var newUpdateTime))
        {
            _addonLastUpdateTime = newUpdateTime;
            _logger.LogInformation("Updated lastUpdateTime={time}", _addonLastUpdateTime);
        }
        else
        {
            _logger.LogWarning("No pending update time found for {id}", sharedFileId);
        }

        try
        {
            File.WriteAllText(_lastUpdateTimeFile, _addonLastUpdateTime.ToString("O"));
            _logger.LogDebug("Wrote last update file");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed writing last update file");
        }

        Task.Run(UpdateAssetsFromPackage, CancellationToken.None);
    }

    private void HandleDownloadRetry(ulong sharedFileId, SteamApiResult reason)
    {
        var retries = _retryCounts.GetValueOrDefault(sharedFileId, 0);

        _logger.LogWarning("[Retry] id={id}, reason={reason}, attempt={attempt}/{max}",
            sharedFileId, reason, retries + 1, MaxRetries);

        _pendingUpdateTimes.TryRemove(sharedFileId, out _);

        if (retries < MaxRetries)
        {
            _retryCounts[sharedFileId] = retries + 1;
            _steamApi.DownloadItem(sharedFileId, true);
        }
        else
        {
            _logger.LogError("Max retries reached for {id}", sharedFileId);
            _retryCounts.TryRemove(sharedFileId, out _);
        }
    }

    public void OnItemInstalled(ulong publishedFileId)
    {
        _logger.LogInformation("[OnItemInstalled] id={id}", publishedFileId);
    }

    public void OnServerActivate()
    {
        _logger.LogInformation("[OnServerActive]");

        if (_modSharp.HasCommandLine("-dual_addon"))
        {
            if (CheckAndEnforceDualAddon())
                return;
        }

        if (_currentAddonId == 0)
        {
            _logger.LogWarning("No addon id set");
            return;
        }

        Task.Run(CheckAddonDetail, CancellationToken.None);

        _modSharp.PushTimer(() =>
        {
            _logger.LogDebug("Timer triggered CheckAddonDetail");
            Task.Run(CheckAddonDetail, CancellationToken.None);
        }, 500f, GameTimerFlags.StopOnMapEnd);
    }

    private bool CheckAndEnforceDualAddon()
    {
        if (_restarted)
            return false;

        var addonName = _modSharp.GetAddonName();

        _logger.LogInformation("[DualAddonCheck] addonName={name}", addonName);

        if (string.IsNullOrWhiteSpace(addonName))
        {
            _logger.LogWarning("AddonName empty → restart");
            TriggerRestart("de_mirage");
            return true;
        }

        var split = addonName.Split(',');
        var hasOurAddon = split.Any(s => ulong.TryParse(s, out var r) && r == _currentAddonId);

        if (!hasOurAddon)
        {
            _logger.LogWarning("Addon missing → restart");
            TriggerRestart("de_mirage");
            return true;
        }

        return false;
    }

    private void TriggerRestart(string mapName)
    {
        _logger.LogWarning("[Restart] map={map}", mapName);

        _restarted = true;
        _modSharp.ServerCommand($"changelevel {mapName}");
    }

    private void UpdateAssetsFromPackage()
    {
        _logger.LogInformation("[UpdateAssets] vpk={vpk}", _vpkPath);

        if (string.IsNullOrWhiteSpace(_vpkPath) || !File.Exists(_vpkPath))
        {
            _logger.LogError("Invalid VPK path");
            return;
        }

        HashSet<string> paths;

        try
        {
            paths = ExtractVpkContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VPK extraction failed");
            return;
        }

        _logger.LogInformation("Extracted {count} files", paths.Count);

        CleanupObsoleteFiles(paths);
        CleanupSourceContent();

        _modSharp.InvokeFrameAction(RestartLevel);
    }

    private HashSet<string> ExtractVpkContent()
    {
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var package = new Package();
        package.Read(_vpkPath);

        _logger.LogInformation("VPK entries={count}", package.Entries?.Count);

        int written = 0;
        
        foreach (var entry in package.Entries ?? new())
        {
            foreach (var value in entry.Value)
            {
                if (string.IsNullOrWhiteSpace(value.DirectoryName))
                    continue;

                var dir = Path.Combine(_assetPath, value.DirectoryName);
                Directory.CreateDirectory(dir);

                var fullPath = Path.Combine(_assetPath, value.GetFullPath());
                extracted.Add(Path.GetFullPath(fullPath));

                package.ReadEntry(value, out var bytes, false);
                File.WriteAllBytes(fullPath, bytes);

                written++;
            }
        }

        _logger.LogInformation("Written files={count}", written);

        return extracted;
    }

    private void CleanupObsoleteFiles(HashSet<string> validPaths)
    {
        var files = Directory.GetFiles(_assetPath, "*", SearchOption.AllDirectories);

        int deleted = 0;

        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);

            if (validPaths.Contains(full) || full.Equals(_lastUpdateTimeFile, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(full);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed deleting {file}", full);
            }
        }

        _logger.LogInformation("Deleted obsolete={count}", deleted);
    }

    private void CleanupSourceContent()
    {
        if (!Directory.Exists(_contentPath))
            return;

        var files = Directory.GetFiles(_contentPath);
        var idStr = _currentAddonId.ToString();

        int deleted = 0;

        foreach (var file in files)
        {
            try
            {
                if (Path.GetFileName(file).StartsWith(idStr))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed deleting {file}", file);
            }
        }

        _logger.LogInformation("CleanupSource deleted={count}", deleted);
    }

    private void RestartLevel()
    {
        var hasPlayers = _clientManager.GetGameClients()
            .Any(i => i is { IsFakeClient: false, IsHltv: false, SignOnState: >= SignOnState.Connected });

        _logger.LogInformation("[RestartLevel] hasPlayers={players}", hasPlayers);

        if (hasPlayers)
            return;

        try
        {
            var map = _modSharp.GetGlobals().MapName;

            if (string.IsNullOrWhiteSpace(map))
                map = "de_mirage";

            _logger.LogInformation("Restarting map={map}", map);

            _modSharp.ServerCommand($"changelevel {map}");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Restart failed");
            _modSharp.ServerCommand("changelevel de_mirage");
        }
    }

    private async Task CheckAddonDetail()
    {
        _logger.LogInformation("[CheckAddonDetail] id={id}", _currentAddonId);

        try
        {
            UpdateAddonPublishTime();

            var service = SteamApiFactory.GetSteamApi<IPublishedFileService>(SteamWebApiKey);
            var details = await service.GetDetails([_currentAddonId], true);

            _logger.LogDebug("Steam returned count={count}", details.Length);

            var detail = details.FirstOrDefault(d => d.PublishedFileId == _currentAddonId);

            if (detail == null)
            {
                _logger.LogWarning("No detail found");
                return;
            }

            if (detail.IsWaitingForApprove)
            {
                _logger.LogInformation("Waiting for approval");
                return;
            }

            var remote = DateTimeOffset.FromUnixTimeSeconds(detail.TimeUpdated).LocalDateTime;

            _logger.LogInformation("Compare remote={remote} local={local}", remote, _addonLastUpdateTime);

            if (remote > _addonLastUpdateTime)
            {
                _pendingUpdateTimes[detail.PublishedFileId] = remote;

                _logger.LogInformation("Triggering download");
                _steamApi.DownloadItem(detail.PublishedFileId, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "CheckAddonDetail failed");
        }
    }

    private void UpdateAddonPublishTime()
    {
        if (!File.Exists(_lastUpdateTimeFile))
        {
            _logger.LogDebug("No last update file found");
            return;
        }

        try
        {
            var str = File.ReadAllText(_lastUpdateTimeFile);

            if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, out var time))
            {
                _addonLastUpdateTime = time.LocalDateTime;
                _logger.LogInformation("Loaded lastUpdateTime={time}", _addonLastUpdateTime);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading last update file");
        }
    }

    public void UpdateAddonId(ulong id, bool wantsToRestart)
    {
        _logger.LogInformation("[UpdateAddonId] id={id}, restart={restart}", id, wantsToRestart);

        _currentAddonId = id;

        _contentPath = Path.GetFullPath(Path.Combine(
            _sharpPath,
            "../bin/",
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linuxsteamrt64" : "win64",
            "steamapps",
            "workshop",
            "content",
            "730",
            id.ToString()));

        _vpkPath = Path.Combine(_contentPath, id + "_dir.vpk");

        _logger.LogInformation("Paths content={content}, vpk={vpk}", _contentPath, _vpkPath);

        if (wantsToRestart)
        {
            _modSharp.InvokeFrameAction(RestartLevel);
        }
    }
}