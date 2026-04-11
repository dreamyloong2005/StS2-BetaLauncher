using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace STS2Mobile.Steam;

public class DownloadProgress
{
    public long TotalBytes;
    public long DownloadedBytes;
    public int TotalFiles;
    public int CompletedFiles;
    public string CurrentFile;

    public double Percentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100.0 : 0;
}

public class DepotDownloader(SteamConnection connection, string dataDir) : IDisposable
{
    private const uint AppId = 2868840;
    private const int MaxRetries = 5;
    private const int MaxConcurrentDownloads = 8;

    private readonly SteamConnection _connection = connection;
    private readonly string _gameDir = AppPaths.ExternalGameFilesDir;
    private readonly string _stateDir = Path.Combine(dataDir, "download_state");
    private readonly Client _cdnClient = new(connection.Client);
    private readonly DownloadProgress _progress = new();

    private IReadOnlyList<Server> _servers;
    private int _serverIndex;
    private readonly Dictionary<(uint, string), string> _cdnAuthTokens = [];

    // MODIFIED: key now includes branch name
    private readonly Dictionary<
        (uint DepotId, string Branch),
        (ulong Code, DateTime Expiry)
    > _manifestRequestCodes = [];
    private readonly Dictionary<
        uint,
        SteamApps.PICSProductInfoCallback.PICSProductInfo
    > _appInfoCache = [];

    public event Action<DownloadProgress> ProgressChanged;
    public event Action<string> LogMessage;

    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            Directory.CreateDirectory(_stateDir);

            ulong accessToken = _connection.AppAccessToken;
            var infoResult = await _connection.Apps.PICSGetProductInfo(
                new[] { new SteamApps.PICSRequest(AppId, accessToken) },
                Enumerable.Empty<SteamApps.PICSRequest>()
            );

            SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo = null;
            foreach (var cb in infoResult.Results)
            {
                if (cb.Apps.TryGetValue(AppId, out var info))
                {
                    appInfo = info;
                    break;
                }
            }

            if (appInfo == null)
                throw new Exception("Failed to get app info from Steam");

            _appInfoCache[AppId] = appInfo;
            var depots = await ParseDepotsAsync(appInfo.KeyValues["depots"]);

            foreach (var (depotId, branch, manifestId) in depots) // MODIFIED
            {
                ct.ThrowIfCancellationRequested();
                if (LoadCachedManifestId(depotId) != manifestId)
                {
                    Log($"Update available: depot {depotId} manifest changed");
                    return true;
                }
            }

            Log("Game is up to date");
            return false;
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            Directory.CreateDirectory(_gameDir);
            Directory.CreateDirectory(_stateDir);

            Log("Fetching app info...");

            ulong accessToken = _connection.AppAccessToken;
            var infoResult = await _connection.Apps.PICSGetProductInfo(
                [new SteamApps.PICSRequest(AppId, accessToken)],
                []
            );

            SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo = null;
            foreach (var cb in infoResult.Results)
            {
                if (cb.Apps.TryGetValue(AppId, out var info))
                {
                    appInfo = info;
                    break;
                }
            }

            _appInfoCache[AppId] = appInfo ?? throw new Exception("Failed to get app info from Steam");
            var depotSection = appInfo.KeyValues["depots"];
            var depots = await ParseDepotsAsync(depotSection);
            if (depots.Count == 0)
                throw new Exception("No downloadable depots found");

            Log("Getting CDN servers...");
            var allServers = await ContentServerDirectoryService.LoadAsync(
                _connection.Configuration,
                ct
            );
            if (allServers == null || allServers.Count == 0)
                throw new Exception("No CDN servers available");

            _servers = allServers
                .Where(s => s.Type == "SteamCache" || s.Type == "CDN")
                .OrderBy(s => s.WeightedLoad)
                .ToList();

            if (_servers.Count == 0)
                _servers = allServers.ToList();

            Log($"Using {_servers.Count} CDN servers");

            foreach (var (depotId, branch, manifestId) in depots) // MODIFIED
            {
                ct.ThrowIfCancellationRequested();
                await DownloadDepotAsync(depotId, branch, manifestId, ct); // MODIFIED
            }

            Log("All game files downloaded!");
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    // MODIFIED: return tuple includes branch name
    private async Task<List<(uint DepotId, string Branch, ulong ManifestId)>> ParseDepotsAsync(
        KeyValue depotSection
    )
    {
        var result = new List<(uint, string, ulong)>();

        foreach (var depot in depotSection.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId))
                continue;

            // Skip non-Windows depots.
            var config = depot["config"];
            if (config != KeyValue.Invalid)
            {
                var oslist = config["oslist"]?.Value;
                if (oslist != null && oslist.Length > 0 && !oslist.Contains("windows"))
                {
                    Log($"Skipping depot {depotId} (OS: {oslist})");
                    continue;
                }
            }

            var manifests = depot["manifests"];

            if (manifests == KeyValue.Invalid)
            {
                var depotFromApp = depot["depotfromapp"];
                if (
                    depotFromApp != KeyValue.Invalid
                    && depotFromApp.Value != null
                    && uint.TryParse(depotFromApp.Value, out var otherAppId)
                )
                {
                    Log($"Depot {depotId} references app {otherAppId}, fetching...");
                    var otherAppInfo = await GetAppInfoAsync(otherAppId);
                    if (otherAppInfo != null)
                    {
                        var otherDepots = otherAppInfo.KeyValues["depots"];
                        var otherDepot = otherDepots[depotId.ToString()];
                        if (otherDepot != KeyValue.Invalid)
                            manifests = otherDepot["manifests"];
                    }
                }

                if (manifests == KeyValue.Invalid)
                    continue;
            }

            // MODIFIED: explicitly handle branch "public-beta"
            // You could also iterate over all branches if needed, but here we only need public-beta.
            const string branchName = "public-beta";
            var gidNode = manifests[branchName]["gid"];
            if (gidNode == KeyValue.Invalid || gidNode.Value == null)
                continue;

            if (!ulong.TryParse(gidNode.Value, out var manifestId))
                continue;

            Log($"Found depot {depotId} branch {branchName} manifest {manifestId}");
            result.Add((depotId, branchName, manifestId));
        }

        return result;
    }

    private async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(
        uint appId
    )
    {
        if (_appInfoCache.TryGetValue(appId, out var cached))
            return cached;

        var tokenResult = await _connection.Apps.PICSGetAccessTokens(
            new[] { appId },
            Enumerable.Empty<uint>()
        );
        ulong token = 0;
        tokenResult.AppTokens?.TryGetValue(appId, out token);

        var infoResult = await _connection.Apps.PICSGetProductInfo(
            new[] { new SteamApps.PICSRequest(appId, token) },
            Enumerable.Empty<SteamApps.PICSRequest>()
        );

        foreach (var cb in infoResult.Results)
        {
            if (cb.Apps.TryGetValue(appId, out var info))
            {
                _appInfoCache[appId] = info;
                return info;
            }
        }

        return null;
    }

    private Server GetNextServer()
    {
        var idx = Interlocked.Increment(ref _serverIndex);
        return _servers[((idx % _servers.Count) + _servers.Count) % _servers.Count];
    }

    private async Task<string> GetCdnAuthToken(uint depotId, Server server)
    {
        var key = (depotId, server.Host);
        if (_cdnAuthTokens.TryGetValue(key, out var cached))
            return cached;

        var result = await _connection.Content.GetCDNAuthToken(AppId, depotId, server.Host);
        if (result.Result == EResult.OK)
        {
            _cdnAuthTokens[key] = result.Token;
            return result.Token;
        }

        return null;
    }

    // MODIFIED: added branch parameter
    private async Task<ulong> GetManifestRequestCodeAsync(
        uint depotId,
        string branch,
        ulong manifestId
    )
    {
        var cacheKey = (depotId, branch);
        if (
            _manifestRequestCodes.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow < cached.Expiry
        )
        {
            return cached.Code;
        }

        var code = await _connection.Content.GetManifestRequestCode(
            depotId,
            AppId,
            manifestId,
            branch // MODIFIED: use branch instead of "public"
        );
        if (code == 0)
            throw new Exception(
                $"Failed to get manifest request code for depot {depotId} branch {branch}. "
                    + "Ensure the account owns this app and has access to this branch."
            );

        _manifestRequestCodes[cacheKey] = (code, DateTime.UtcNow.AddMinutes(5));
        return code;
    }

    // MODIFIED: added branch parameter
    private async Task DownloadDepotAsync(
        uint depotId,
        string branch,
        ulong manifestId,
        CancellationToken ct
    )
    {
        Log($"Processing depot {depotId} branch {branch}...");

        bool isUpdate = LoadCachedManifestId(depotId) != manifestId;

        var keyResult = await _connection.Apps.GetDepotDecryptionKey(depotId, AppId);
        if (keyResult.Result != EResult.OK)
            throw new Exception($"Failed to get depot key for {depotId}: {keyResult.Result}");
        var depotKey = keyResult.DepotKey;

        var manifestRequestCode = await GetManifestRequestCodeAsync(depotId, branch, manifestId);

        Log($"Downloading manifest for depot {depotId}...");
        DepotManifest manifest = null;
        for (int attempt = 0; attempt < MaxRetries && manifest == null; attempt++)
        {
            var server = GetNextServer();
            try
            {
                manifest = await _cdnClient.DownloadManifestAsync(
                    depotId,
                    manifestId,
                    manifestRequestCode,
                    server,
                    depotKey
                );
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var token = await GetCdnAuthToken(depotId, server);
                if (token != null)
                {
                    manifest = await _cdnClient.DownloadManifestAsync(
                        depotId,
                        manifestId,
                        manifestRequestCode,
                        server,
                        depotKey,
                        cdnAuthToken: token
                    );
                }
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                Log($"Manifest download failed (attempt {attempt + 1}): {ex.Message}");
            }
        }

        if (manifest == null)
            throw new Exception(
                $"Failed to download manifest for depot {depotId} after {MaxRetries} attempts"
            );

        var oldManifest = LoadCachedManifest(depotId);

        foreach (
            var temp in Directory.GetFiles(_gameDir, "*.downloading", SearchOption.AllDirectories)
        )
        {
            try
            {
                File.Delete(temp);
            }
            catch { }
        }

        var filesToDownload = GetFilesNeedingDownload(oldManifest, manifest, isUpdate);
        var filesToDelete = GetFilesToDelete(oldManifest, manifest);

        foreach (var fileName in filesToDelete)
        {
            var path = Path.Combine(_gameDir, fileName.Replace('\\', '/'));
            if (File.Exists(path))
            {
                File.Delete(path);
                Log($"Deleted: {fileName}");
            }
        }

        _progress.TotalFiles = filesToDownload.Count;
        _progress.CompletedFiles = 0;
        _progress.TotalBytes = filesToDownload.Sum(f => (long)f.TotalSize);
        _progress.DownloadedBytes = 0;
        ReportProgress();

        if (filesToDownload.Count == 0)
        {
            Log($"Depot {depotId}: already up to date");
        }
        else
        {
            Log(
                $"Downloading {filesToDownload.Count} files ({FormatSize(_progress.TotalBytes)}) with {MaxConcurrentDownloads} threads..."
            );

            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
            var tasks = new List<Task>();

            foreach (var file in filesToDownload)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                await DownloadFileAsync(file, depotId, depotKey, ct);
                                Interlocked.Increment(ref _progress.CompletedFiles);
                                ReportProgress();
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        },
                        ct
                    )
                );
            }

            await Task.WhenAll(tasks);
        }

        SaveManifest(depotId, manifest, manifestId);
        Log($"Depot {depotId} complete");
    }

    // The rest of the methods (DownloadFileAsync, VerifyChunkHash, VerifyFileHash, etc.) remain unchanged.
    // I'll include them for completeness but they are identical to your original.

    private async Task DownloadFileAsync(
        DepotManifest.FileData file,
        uint depotId,
        byte[] depotKey,
        CancellationToken ct
    )
    {
        var fileName = file.FileName.Replace('\\', '/');
        _progress.CurrentFile = fileName;
        ReportProgress();

        if (file.Flags.HasFlag(EDepotFileFlag.Directory))
        {
            Directory.CreateDirectory(Path.Combine(_gameDir, fileName));
            return;
        }

        var filePath = Path.Combine(_gameDir, fileName);
        var fileDir = Path.GetDirectoryName(filePath);
        if (fileDir != null)
            Directory.CreateDirectory(fileDir);

        if (File.Exists(filePath) && VerifyFileHash(filePath, file))
        {
            Interlocked.Add(ref _progress.DownloadedBytes, (long)file.TotalSize);
            ReportProgress();
            return;
        }

        var tempPath = filePath + ".downloading";

        using (var fs = File.Create(tempPath))
        {
            foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
            {
                ct.ThrowIfCancellationRequested();

                var buffer = new byte[chunk.UncompressedLength];
                int written = 0;

                for (int attempt = 0; attempt < MaxRetries; attempt++)
                {
                    var server = GetNextServer();
                    try
                    {
                        written = await _cdnClient.DownloadDepotChunkAsync(
                            depotId,
                            chunk,
                            server,
                            buffer,
                            depotKey
                        );

                        if (!VerifyChunkHash(buffer, written, chunk))
                        {
                            if (attempt < MaxRetries - 1)
                            {
                                Log($"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying...");
                                written = 0;
                                continue;
                            }
                            throw new Exception(
                                $"Chunk SHA-1 verification failed for {fileName} "
                                    + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                            );
                        }

                        break;
                    }
                    catch (SteamKitWebRequestException ex)
                        when (ex.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var token = await GetCdnAuthToken(depotId, server);
                        if (token != null)
                        {
                            written = await _cdnClient.DownloadDepotChunkAsync(
                                depotId,
                                chunk,
                                server,
                                buffer,
                                depotKey,
                                cdnAuthToken: token
                            );

                            if (!VerifyChunkHash(buffer, written, chunk))
                            {
                                if (attempt < MaxRetries - 1)
                                {
                                    Log(
                                        $"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying..."
                                    );
                                    written = 0;
                                    continue;
                                }
                                throw new Exception(
                                    $"Chunk SHA-1 verification failed for {fileName} "
                                        + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                                );
                            }

                            break;
                        }
                    }
                    catch (Exception ex) when (attempt < MaxRetries - 1)
                    {
                        Log($"Chunk download failed (attempt {attempt + 1}): {ex.Message}");
                    }
                }

                if (written == 0 && chunk.UncompressedLength > 0)
                    throw new Exception(
                        $"Failed to download chunk for {fileName} after {MaxRetries} attempts"
                    );

                fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                fs.Write(buffer, 0, written);

                Interlocked.Add(ref _progress.DownloadedBytes, written);
                ReportProgress();
            }
        }

        if (!VerifyFileHash(tempPath, file))
        {
            File.Delete(tempPath);
            throw new Exception($"SHA-1 verification failed for {fileName} after download");
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    private static bool VerifyChunkHash(byte[] buffer, int length, DepotManifest.ChunkData chunk)
    {
        if (chunk.ChunkID == null || chunk.ChunkID.Length == 0)
            return true;

        var hash = System.Security.Cryptography.SHA1.HashData(buffer.AsSpan(0, length));
        return hash.AsSpan().SequenceEqual(chunk.ChunkID);
    }

    private static bool VerifyFileHash(string path, DepotManifest.FileData file)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != (long)file.TotalSize)
                return false;

            using var fs = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA1.HashData(fs);
            return hash.AsSpan().SequenceEqual(file.FileHash);
        }
        catch
        {
            return false;
        }
    }

    private List<DepotManifest.FileData> GetFilesNeedingDownload(
        DepotManifest oldManifest,
        DepotManifest newManifest,
        bool isUpdate
    )
    {
        var oldFiles = oldManifest?.Files.ToDictionary(f => f.FileName);
        var result = new List<DepotManifest.FileData>();
        int verified = 0;
        int corrupt = 0;

        foreach (var file in newManifest.Files)
        {
            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                continue;

            if (isUpdate && oldFiles != null)
            {
                if (
                    !oldFiles.TryGetValue(file.FileName, out var oldFile)
                    || !file.FileHash.SequenceEqual(oldFile.FileHash)
                )
                {
                    result.Add(file);
                    continue;
                }
            }

            var filePath = Path.Combine(_gameDir, file.FileName.Replace('\\', '/'));
            if (VerifyFileHash(filePath, file))
            {
                verified++;
            }
            else
            {
                if (File.Exists(filePath))
                {
                    corrupt++;
                    Log($"File needs re-download (hash mismatch): {file.FileName}");
                }
                result.Add(file);
            }
        }

        if (verified > 0)
            Log($"Verified {verified} existing files");
        if (corrupt > 0)
            Log($"Found {corrupt} corrupt files requiring re-download");

        return result;
    }

    private static List<string> GetFilesToDelete(
        DepotManifest oldManifest,
        DepotManifest newManifest
    )
    {
        if (oldManifest == null)
            return new List<string>();

        var newFiles = new HashSet<string>(newManifest.Files.Select(f => f.FileName));
        return oldManifest
            .Files.Where(f => !newFiles.Contains(f.FileName))
            .Select(f => f.FileName)
            .ToList();
    }

    private ulong LoadCachedManifestId(uint depotId)
    {
        var path = Path.Combine(_stateDir, $"{depotId}.id");
        if (!File.Exists(path))
            return 0;

        return ulong.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : 0;
    }

    private DepotManifest LoadCachedManifest(uint depotId)
    {
        var path = Path.Combine(_stateDir, $"{depotId}.manifest");
        if (!File.Exists(path))
            return null;

        try
        {
            using var fs = File.OpenRead(path);
            return DepotManifest.Deserialize(fs);
        }
        catch
        {
            return null;
        }
    }

    private void SaveManifest(uint depotId, DepotManifest manifest, ulong manifestId)
    {
        using (var fs = File.Create(Path.Combine(_stateDir, $"{depotId}.manifest")))
        {
            manifest.Serialize(fs);
        }
        File.WriteAllText(Path.Combine(_stateDir, $"{depotId}.id"), manifestId.ToString());
    }

    private void Log(string msg)
    {
        PatchHelper.Log($"[Depot] {msg}");
        LogMessage?.Invoke(msg);
    }

    private void ReportProgress()
    {
        ProgressChanged?.Invoke(_progress);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        _cdnClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
