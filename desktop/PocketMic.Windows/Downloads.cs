using System.IO;
using System.Net.Http;
using PocketMic.Core;

namespace PocketMic.Windows;

internal static class Downloads
{
    public static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PocketMic/0.1.0");
        return client;
    }
    public static async Task FetchAsync(Uri url, string path, string sha256, long size, IProgress<string>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var client = Client();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != size) throw new InvalidDataException("下载文件大小与发布信息不一致。");
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[64 * 1024]; long total = 0; int count;
            while ((count = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += count;
                if (total > size) throw new InvalidDataException("下载文件超出预期大小。");
                await target.WriteAsync(buffer.AsMemory(0, count), cancel);
                progress?.Report($"正在下载… {total * 100 / size}%");
            }
        }
        await ReleaseManifest.VerifyFileAsync(path, sha256, size, cancel);
    }
}
