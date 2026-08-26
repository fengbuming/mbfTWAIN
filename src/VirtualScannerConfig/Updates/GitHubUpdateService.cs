using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MbfTwain.VirtualScannerConfig.Updates;

internal sealed class GitHubUpdateService
{
    private const string RepositoryOwner = "fengbuming";
    private const string RepositoryName = "mbfTWAIN";
    private const string OfficialApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

    // GitHub remains the source of release metadata and file digests. These only
    // provide alternate transport for the exact, already-verified release asset.
    private const string MirrorApiTemplate = "https://gh-proxy.com/{0}";
    private const string MirrorDownloadTemplate = "https://ghproxy.net/{0}";
    private const string MirrorDownloadTemplateAlt = "https://gh-proxy.com/{0}";

    private static readonly string[] DownloadMirrorTemplates =
    [
        MirrorDownloadTemplate,
        MirrorDownloadTemplateAlt,
    ];

    // 元数据检查：官方 API 永远第一位（直连用户享有最低延迟与最权威响应），
    // 镜像仅在官方失败后按顺序逐个回退。
    private static readonly string[] ApiUrlCandidates = BuildApiUrlCandidates();

    private static readonly HttpClient _httpClient = CreateHttpClient();

    public Version CurrentVersion { get; } = GetCurrentVersion();

    public string CurrentVersionText => CurrentVersion.ToString(3);

    public async Task<ReleaseUpdateInfo> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        List<string> errors = [];
        foreach (string url in ApiUrlCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await FetchReleaseInfoAsync(url, CurrentVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                errors.Add($"{url}: {exception.Message}");
            }
        }

        string detail = string.Join("; ", errors);
        throw new InvalidOperationException(
            "无法访问 GitHub 获取最新版本信息。请检查网络连接，或稍后重试。" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail}）"));
    }

    public async Task<string> DownloadInstallerAsync(
        ReleaseUpdateInfo update,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (update.InstallerUri is null || string.IsNullOrWhiteSpace(update.InstallerName))
        {
            throw new InvalidOperationException("最新 GitHub Release 没有可下载的安装包。");
        }

        string updateDirectory = Path.Combine(Path.GetTempPath(), "mbfTwain", "updates");
        Directory.CreateDirectory(updateDirectory);
        string fileName = Path.GetFileName(update.InstallerName);
        string targetPath = Path.Combine(updateDirectory, fileName);
        string partialPath = $"{targetPath}.download";

        List<string> errors = [];
        foreach (string url in BuildDownloadUrlCandidates(update.InstallerUri))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadInstallerFromUrlAsync(url, partialPath, update, progress, cancellationToken)
                    .ConfigureAwait(false);
                VerifyInstallerIntegrity(partialPath, update.InstallerSize, update.InstallerSha256);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(partialPath, targetPath);
                return targetPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                errors.Add($"{url}: {exception.Message}");
                TryDeleteFile(partialPath);
            }
        }

        string downloadDetail = string.Join("; ", errors);
        throw new InvalidOperationException(
            $"安装包下载失败（{update.InstallerName}）。请检查网络后重试。" +
            (string.IsNullOrWhiteSpace(downloadDetail) ? string.Empty : $"（{downloadDetail}）"));
    }

    /// <summary>
    /// 尝试通过一个候选 URL 获取最新 Release 元数据。
    /// </summary>
    /// <exception cref="UpdateSourceUnavailableException">仓库不存在、未授权或达到限额（不重试）</exception>
    /// <exception cref="HttpRequestException">网络层失败（会回退到镜像）</exception>
    /// <exception cref="InvalidOperationException">响应解析失败或 TAG 非法（回退到镜像）</exception>
    private static async Task<ReleaseUpdateInfo> FetchReleaseInfoAsync(
        string url,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateSourceUnavailableException(
                "无法访问 GitHub Release。仓库为私有时，请设置 MBF_TWAIN_GITHUB_TOKEN 后再检查更新。");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UpdateSourceUnavailableException(
                "GitHub 更新检查未授权或达到限额，请检查 MBF_TWAIN_GITHUB_TOKEN。");
        }

        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string tagName = GetRequiredString(root, "tag_name");
        Version latestVersion = ParseTagVersion(tagName)
            ?? throw new InvalidOperationException($"GitHub release tag is not a semantic version: {tagName}");
        string htmlUrl = GetRequiredString(root, "html_url");
        DateTimeOffset? publishedAt = TryGetDateTimeOffset(root, "published_at");

        JsonElement? installerAsset = FindInstallerAsset(root);
        Uri? installerUri = null;
        string? installerName = null;
        long? installerSize = null;
        string? installerSha256 = null;
        if (installerAsset is { } asset)
        {
            installerName = GetRequiredString(asset, "name");
            installerUri = new Uri(GetRequiredString(asset, "browser_download_url"));
            installerSize = TryGetInt64(asset, "size");
            installerSha256 = TryGetSha256Digest(asset);
        }

        return new ReleaseUpdateInfo(
            latestVersion,
            tagName,
            latestVersion.CompareTo(currentVersion) > 0,
            new Uri(htmlUrl),
            installerUri,
            installerName,
            installerSize,
            installerSha256,
            publishedAt);
    }

    private static async Task DownloadInstallerFromUrlAsync(
        string url,
        string partialPath,
        ReleaseUpdateInfo update,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 每个下载源独立超时：官方 60s、镜像 90s。共享 HttpClient 的默认
        // Timeout 会覆盖请求级 Timeout，因此这里临时创建专用 client，避免
        // 某个慢官方端点或镜像把后续候选整体拖死。
        bool isMirror = IsMirrorDownloadUrl(url);
        using var client = new HttpClient
        {
            Timeout = isMirror ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mbfTwain.VirtualScannerConfig", "1.0"));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength ?? update.InstallerSize;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[1024 * 128];
        long bytesReadTotal = 0;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesReadTotal += bytesRead;
            progress?.Report(new DownloadProgress(bytesReadTotal, totalBytes));
        }
    }

    private static void VerifyInstallerIntegrity(string partialPath, long? expectedSize, string? expectedSha256)
    {
        var info = new FileInfo(partialPath);
        if (!info.Exists)
        {
            throw new InvalidOperationException("下载文件不存在。");
        }

        if (expectedSize is > 0 && info.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"安装包大小不匹配：预期 {expectedSize} 字节，实际 {info.Length} 字节。");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            // GitHub API 未提供 digest 时，仅校验大小，不阻止下载。
            return;
        }

        using var stream = File.OpenRead(partialPath);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包 SHA-256 校验失败，文件可能已被篡改，已取消安装。");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // 元数据检查总超时 20s，覆盖官方和镜像 API 两个候选。
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mbfTwain.VirtualScannerConfig", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        AuthenticationHeaderValue? authorization = GetGitHubAuthorizationHeader();
        if (authorization is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = authorization;
        }

        return httpClient;
    }

    private static AuthenticationHeaderValue? GetGitHubAuthorizationHeader()
    {
        string? token = Environment.GetEnvironmentVariable("MBF_TWAIN_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        token = token.Trim();
        int separatorIndex = token.IndexOf(' ');
        if (separatorIndex > 0)
        {
            string scheme = token[..separatorIndex];
            string parameter = token[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(parameter)
                ? null
                : new AuthenticationHeaderValue(scheme, parameter);
        }

        return new AuthenticationHeaderValue("Bearer", token);
    }

    private static string[] BuildApiUrlCandidates()
    {
        List<string> urls = [OfficialApiUrl];
        AddMirror(urls, MirrorApiTemplate, OfficialApiUrl);
        return [.. urls];
    }

    private static List<string> BuildDownloadUrlCandidates(Uri installerUri)
    {
        List<string> urls = [];
        string officialUrl = installerUri.ToString();
        urls.Add(officialUrl);

        // 官方 asset 直链一般为 github.com/.../releases/download/...，
        // 该域名在国内不通畅时可被镜像代理；对象存储直链（objects.githubusercontent.com）
        // 镜像大概率不可用，保留官方直链即可。
        if (IsMirrorableDownloadUrl(officialUrl))
        {
            foreach (string template in DownloadMirrorTemplates)
            {
                AddMirror(urls, template, officialUrl);
            }
        }

        return urls;
    }

    private static bool IsMirrorableDownloadUrl(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMirrorDownloadUrl(string url)
    {
        return url.StartsWith("https://ghproxy.net/", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://gh-proxy.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMirror(List<string> urls, string template, string originalUrl)
    {
        if (!template.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            !template.Contains("{0}", StringComparison.Ordinal))
        {
            return;
        }

        string candidate = string.Format(template, originalUrl);
        if (!string.IsNullOrWhiteSpace(candidate) && !urls.Contains(candidate))
        {
            urls.Add(candidate);
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is not UpdateSourceUnavailableException and
            (HttpRequestException or TaskCanceledException or InvalidOperationException or IOException);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 文件可能被占用，忽略即可
        }
    }

    private static Version GetCurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0];

        return ParseTagVersion(informationalVersion)
            ?? assembly.GetName().Version
            ?? new Version(0, 0, 0);
    }

    private static JsonElement? FindInstallerAsset(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = TryGetString(asset, "name");
            string? downloadUrl = TryGetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                return asset.Clone();
            }
        }

        return null;
    }

    private static string? TryGetSha256Digest(JsonElement asset)
    {
        // GitHub Release API 的 asset 通常带 `digest: "sha256:<hex>"` 字段，
        // 用于下载后完整性校验（对齐 codex-usage-hud 的做法）。
        string? digest = TryGetString(asset, "digest");
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        int separator = digest.IndexOf(':');
        if (separator > 0 && digest[..separator].Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            string value = digest[(separator + 1)..].Trim();
            if (value.Length == 64 && value.All(Uri.IsHexDigit))
            {
                return value.ToLowerInvariant();
            }
        }

        return null;
    }

    private static Version? ParseTagVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        string value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        int suffixIndex = value.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            value = value[..suffixIndex];
        }

        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return null;
        }

        var versionParts = new int[4];
        for (int index = 0; index < versionParts.Length; index++)
        {
            versionParts[index] = 0;
        }

        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out versionParts[index]))
            {
                return null;
            }
        }

        return new Version(versionParts[0], versionParts[1], versionParts[2], versionParts[3]);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName)
            ?? throw new InvalidOperationException($"GitHub response is missing `{propertyName}`.");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.GetString();
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt64(out long value) ? value : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out DateTimeOffset dateTimeOffset)
            ? dateTimeOffset
            : null;
    }

    /// <summary>
    /// 仓库确定性地无法返回更新信息（404 私有仓库、401/403 未授权或限额）。
    /// 镜像无从改善此类错误，因此不进行重试。
    /// </summary>
    private sealed class UpdateSourceUnavailableException : Exception
    {
        public UpdateSourceUnavailableException(string message)
            : base(message)
        {
        }
    }
}