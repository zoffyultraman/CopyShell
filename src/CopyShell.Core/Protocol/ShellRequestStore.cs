using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopyShell.Core.Protocol;

public sealed class ShellRequestStore
{
    public const int CurrentVersion = 1;
    public const long MaximumRequestBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _requestDirectory;
    private readonly TimeProvider _timeProvider;

    public ShellRequestStore(
        string? requestDirectory = null,
        TimeProvider? timeProvider = null)
    {
        _requestDirectory = Path.GetFullPath(
            requestDirectory ?? GetDefaultRequestDirectory());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string GetDefaultRequestDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyShell",
            "Requests");

    public async Task<ShellRequest> ReadAndDeleteAsync(
        string requestPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(requestPath);
        ValidateRequestPath(fullPath);

        var requestId = Guid.Parse(Path.GetFileNameWithoutExtension(fullPath));
        var processingPath = Path.Combine(_requestDirectory, $"{requestId:D}.processing");
        Directory.CreateDirectory(_requestDirectory);

        try
        {
            File.Move(fullPath, processingPath);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("右键菜单请求不存在、已过期或已被其他进程读取。", exception);
        }

        try
        {
            var information = new FileInfo(processingPath);
            if (information.Length > MaximumRequestBytes)
            {
                throw new InvalidDataException("右键菜单请求超过 1 MiB 限制。");
            }

            await using var stream = new FileStream(
                processingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var request = await JsonSerializer.DeserializeAsync<ShellRequest>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            ValidateRequest(request, requestId);
            return request!;
        }
        finally
        {
            File.Delete(processingPath);
        }
    }

    public void DeleteStaleRequests(TimeSpan maximumAge)
    {
        if (!Directory.Exists(_requestDirectory))
        {
            return;
        }

        var threshold = _timeProvider.GetUtcNow() - maximumAge;
        foreach (var path in Directory.EnumerateFiles(_requestDirectory))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".processing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void ValidateRequestPath(string fullPath)
    {
        var root = Path.EndsInDirectorySeparator(_requestDirectory)
            ? _requestDirectory
            : _requestDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileNameWithoutExtension(fullPath), out _))
        {
            throw new InvalidDataException("请求文件不在 CopyShell 的安全目录中。");
        }
    }

    private void ValidateRequest(ShellRequest? request, Guid expectedRequestId)
    {
        if (request is null)
        {
            throw new InvalidDataException("右键菜单请求为空。");
        }

        if (request.Version != CurrentVersion)
        {
            throw new InvalidDataException($"不支持的请求版本：{request.Version}。");
        }

        if (request.RequestId != expectedRequestId)
        {
            throw new InvalidDataException("请求 ID 与文件名不一致。");
        }

        var now = _timeProvider.GetUtcNow();
        if (request.CreatedAtUtc < now.AddMinutes(-15) ||
            request.CreatedAtUtc > now.AddMinutes(1) ||
            request.ExpiresAtUtc <= now ||
            request.ExpiresAtUtc <= request.CreatedAtUtc ||
            request.ExpiresAtUtc > request.CreatedAtUtc.AddMinutes(15))
        {
            throw new InvalidDataException("右键菜单请求已过期或时间无效。");
        }

        if (request.Sources is not { Count: > 0 and <= 4096 } sources ||
            sources.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
        {
            throw new InvalidDataException("请求中的源路径无效。");
        }
    }
}
