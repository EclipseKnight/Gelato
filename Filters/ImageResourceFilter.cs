using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Gelato.Decorators;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Serves image requests for search results (non-library gelato items), and a stream row's
/// images from its movie/episode. Library item images are handled by ImageProcessorDecorator.
/// </summary>
public sealed class ImageResourceFilter(
    IHttpClientFactory http,
    GelatoManager manager,
    ILibraryManager libraryManager,
    IApplicationPaths appPaths,
    IImageProcessor imageProcessor,
    ILogger<ImageResourceFilter> log
) : IAsyncResourceFilter
{
    // A search result's poster is kept here once fetched (named by its URL), so it can be resized
    // like a library image. Jellyfin's cache cleanup task removes what isn't used for a month.
    private const long MaxPosterBytes = 25 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, Lazy<Task<FileInfo>>> Fetching = new(
        StringComparer.Ordinal
    );

    // Jellyfin writes a resized copy straight to its cache path, so a second request for the same
    // size arriving mid-write could serve a half-written file. Requests for the same poster and
    // size wait for the first one.
    private static readonly SemaphoreSlim[] ResizeLocks = Enumerable
        .Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor
            is not ControllerActionDescriptor
            {
                // HEAD requests share these action names; the Head* names in ImageController are
                // route names.
                ActionName: "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2"
            }
        )
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        if (
            !routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid)
        )
        {
            await next();
            return;
        }

        // The search result was opened and inserted: its images are the item's now.
        if (manager.GetInsertedId(guid) is { } insertedId)
        {
            routeValues["itemId"] = insertedId.ToString("N");
            await NotFoundOrNext(ctx, next, insertedId);
            return;
        }

        // A stream row has no images of its own; the DTO hands out its movie's image tags.
        if (
            libraryManager.GetItemById(guid) is Video { PrimaryVersionId: { } primaryId } row
            && row.HasStreamTag()
        )
        {
            routeValues["itemId"] = primaryId.ToString("N");
            await NotFoundOrNext(ctx, next, primaryId);
            return;
        }

        // Only handle cached search results — library items go through ProcessImage
        var url = manager.GetStremioMeta(guid)?.Poster;
        if (url is null)
        {
            await NotFoundOrNext(ctx, next, guid);
            return;
        }

        log.LogDebug(
            "ImageFilter: serving search result item={ItemId} url={Url}",
            guid,
            Redact.Url(url)
        );

        try
        {
            // Fetched once, then resized like any library image: clients ask for the size they
            // show (maxWidth=240 on a card), and the remote poster is often 600x900 or larger.
            var original = await FetchPoster(url, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            var request = ctx.HttpContext.Request;
            var gate = ResizeLocks[
                (uint)StringComparer.Ordinal.GetHashCode(original.FullName + request.QueryString + request.Headers.Accept)
                    % ResizeLocks.Length
            ];
            await gate.WaitAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            string path;
            string? mimeType;
            try
            {
                (path, mimeType, _) = await imageProcessor
                    .ProcessImage(ProcessingOptions(request, guid, original))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not resizable (an odd format, a broken file): the poster as it came, as before.
                log.LogDebug(ex, "ImageFilter: resize failed for item={ItemId}, sending the original", guid);
                (path, mimeType) = (original.FullName, OriginalMimeType(original));
            }
            finally
            {
                gate.Release();
            }
            if (ctx.HttpContext.Request.Query.ContainsKey("tag"))
                ctx.HttpContext.Response.Headers.CacheControl = "public, max-age=31536000";
            ctx.Result = new PhysicalFileResult(path, mimeType ?? "image/jpeg");
        }
        catch (OperationCanceledException)
            when (ctx.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away, e.g. an image scrolled out of view.
            log.LogDebug("ImageFilter: client aborted image for item={ItemId}", guid);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "ImageFilter: poster failed for item={ItemId} url={Url}",
                guid,
                Redact.Url(url)
            );
            await next();
        }
    }

    /// <summary>
    /// The search result's poster on disk, downloaded the first time it is asked for. Requests
    /// for the same poster at once share one download.
    /// </summary>
    private async Task<FileInfo> FetchPoster(string url, CancellationToken cancellationToken)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var dir = Path.Combine(appPaths.CachePath, "gelato-search-images", name[..2]);
        if (Existing(dir, name) is { } existing)
            return existing;

        var task = Fetching.GetOrAdd(name, _ => new Lazy<Task<FileInfo>>(() => Download(url, dir, name)));
        try
        {
            // The download carries on for the others if this client goes away.
            return await task.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.Value.IsCompleted)
                Fetching.TryRemove(name, out _);
        }
    }

    private static string OriginalMimeType(FileInfo file) =>
        file.Extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };

    private static FileInfo? Existing(string dir, string name)
    {
        if (!Directory.Exists(dir))
            return null;
        foreach (var ext in (string[])[".jpg", ".png", ".webp", ".gif"])
        {
            var file = new FileInfo(Path.Combine(dir, name + ext));
            if (file.Exists && file.Length > 0)
                return file;
        }

        return null;
    }

    private async Task<FileInfo> Download(string url, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var client = http.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var res = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is > MaxPosterBytes)
            throw new InvalidDataException($"Poster is larger than {MaxPosterBytes} bytes.");

        // Jellyfin goes by the extension (transparency, the type it serves the original as).
        var ext = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg",
        };
        var path = Path.Combine(dir, name + ext);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = File.Create(temp))
            {
                await using var input = await res.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPosterBytes)
                        throw new InvalidDataException($"Poster is larger than {MaxPosterBytes} bytes.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return new FileInfo(path);
    }

    /// <summary>
    /// The resize the client asked for, read from the same query parameters Jellyfin's image
    /// endpoint takes. Output formats follow the Accept header, as Jellyfin's own does.
    /// </summary>
    private static ImageProcessingOptions ProcessingOptions(HttpRequest request, Guid itemId, FileInfo original)
    {
        int? Int(string key) =>
            int.TryParse(request.Query[key].ToString(), out var value) && value > 0 ? value : null;

        ImageFormat[] formats;
        if (Enum.TryParse<ImageFormat>(request.Query["format"].ToString(), true, out var format))
        {
            formats = [format];
        }
        else
        {
            var accept = request.Headers.Accept.ToString();
            formats = accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase)
                ? [ImageFormat.Webp, ImageFormat.Jpg, ImageFormat.Png]
                : [ImageFormat.Jpg, ImageFormat.Png];
        }

        return new ImageProcessingOptions
        {
            ItemId = itemId,
            Image = new ItemImageInfo
            {
                Path = original.FullName,
                Type = ImageType.Primary,
                DateModified = original.LastWriteTimeUtc,
            },
            Width = Int("width"),
            Height = Int("height"),
            MaxWidth = Int("maxWidth"),
            MaxHeight = Int("maxHeight"),
            FillWidth = Int("fillWidth"),
            FillHeight = Int("fillHeight"),
            Quality = Int("quality") ?? 100,
            Blur = Int("blur"),
            SupportedOutputFormats = formats,
        };
    }

    /// <summary>
    /// Answers not found for an image whose remote failed recently, and hands the request on for
    /// everything else. ProcessImage ends in the same not found once it has looked at the empty
    /// placeholder; stopping here keeps the repeat out of the controller and out of the log.
    /// </summary>
    private async Task NotFoundOrNext(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next,
        Guid itemId
    )
    {
        if (IsKnownDeadImage(ctx, itemId))
        {
            ctx.Result = new NotFoundResult();
            return;
        }

        await next();
    }

    private bool IsKnownDeadImage(ResourceExecutingContext ctx, Guid itemId)
    {
        var values = ctx.RouteData.Values;
        if (
            !Enum.TryParse<ImageType>(values["imageType"]?.ToString(), true, out var type)
            || libraryManager.GetItemById(itemId) is not { } item
        )
            return false;

        _ = int.TryParse(values["imageIndex"]?.ToString(), out var index);
        if (item.GetImageInfo(type, index)?.Path is not { } path)
            return false;

        var file = new FileInfo(path);
        if (file.Exists && file.Length > 0)
            return false;

        var urlFile = ImageProcessorDecorator.ResolveUrlFile(appPaths, itemId, path, type, index);
        if (urlFile is null)
            return false;

        try
        {
            return ImageProcessorDecorator.IsKnownDead(File.ReadAllText(urlFile).Trim());
        }
        catch (IOException)
        {
            return false;
        }
    }
}
