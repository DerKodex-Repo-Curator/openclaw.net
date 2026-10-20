using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// P4 of the attachment-by-reference design: inbound media is copied into the media cache before it
/// becomes a session reference, so the reference does not point into OS temp.
/// </summary>
/// <remarks>
/// Channels download inbound media into <c>Path.GetTempPath()</c> and emit <c>[IMAGE_PATH:/tmp/…]</c>.
/// Without this step the session history stores a path the OS may delete at any time, and the failure
/// surfaces days later as a placeholder with no explanation. See
/// docs/superpowers/specs/2026-10-19-attachment-by-reference-design.md.
/// </remarks>
public sealed class InboundAttachmentPersisterTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly MediaCacheStore _mediaCache;

    public InboundAttachmentPersisterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        _cacheRoot = Path.Combine(_workspace, "media-cache");
        Directory.CreateDirectory(_workspace);
        _mediaCache = new MediaCacheStore(_cacheRoot);
    }

    [Fact]
    public async Task Copies_a_temp_attachment_into_the_cache_and_rewrites_the_marker()
    {
        var tempPath = await WriteTempFileAsync("cat.png", [1, 2, 3]);
        var text = $"look at this\n[IMAGE_PATH:{tempPath}]";

        var rewritten = await PersistAsync(text);

        Assert.NotEqual(text, rewritten);
        Assert.DoesNotContain(tempPath, rewritten, StringComparison.Ordinal);

        var asset = Assert.Single(Directory.GetFiles(_cacheRoot, "media_*.png"));
        Assert.Contains($"[IMAGE_PATH:{asset}]", rewritten, StringComparison.Ordinal);
        Assert.Equal("look at this\n", rewritten[.."look at this\n".Length]);

        // The channel's copy is left alone: moving another component's file is how ownership bugs start.
        Assert.True(File.Exists(tempPath));

        // What the runtime will see once the rewritten text becomes a ChatTurn.
        var turn = AttachmentReferences.CreateUserTurn(rewritten);
        var attachment = Assert.Single(turn.Attachments!);
        Assert.Equal(asset, attachment.Value);
        Assert.NotNull(attachment.AssetId);
        Assert.StartsWith(AttachmentReferences.MediaAssetIdPrefix, attachment.AssetId!, StringComparison.Ordinal);

        // The display name follows the cache file, not the channel's original name. That is the price of
        // recognising the asset id from the file name: both want the same slot. The original name is
        // still recorded in the cache's sidecar metadata, and the placeholder carries the id.
        Assert.Equal(Path.GetFileName(asset), attachment.FileName);
    }

    [Fact]
    public async Task Leaves_attachments_that_already_live_in_the_cache_alone()
    {
        // [FILE_URL:/media/{id}] markers are resolved to a cache path before this runs, so a second
        // round trip must not copy a cache file into the cache.
        var existing = await _mediaCache.SaveAsync(new byte[] { 9, 9 }, "image/png", "already.png", CancellationToken.None);
        var text = $"[IMAGE_PATH:{existing.Path}]";

        var rewritten = await PersistAsync(text);

        Assert.Equal(text, rewritten);
        Assert.Single(Directory.GetFiles(_cacheRoot, "media_*.png"));
    }

    [Fact]
    public async Task Leaves_a_missing_file_alone_instead_of_failing_the_turn()
    {
        var text = "[IMAGE_PATH:/definitely/not/here/nope.png]";

        var rewritten = await PersistAsync(text);

        Assert.Equal(text, rewritten);
        Assert.Empty(Directory.GetFiles(_cacheRoot));
    }

    [Fact]
    public async Task Keeps_marker_order_and_surrounding_prose()
    {
        // Rebuilding the message from MediaMarkerProtocol.Extract would move every marker to the end,
        // which changes what the model reads. Rewriting in place is what preserves the original shape.
        var first = await WriteTempFileAsync("first.png", [1]);
        var second = await WriteTempFileAsync("second.png", [2]);
        var text = $"before\n[IMAGE_PATH:{first}]\nbetween\n[IMAGE_PATH:{second}]\nafter";

        var rewritten = await PersistAsync(text);

        var lines = rewritten.Split('\n');
        Assert.Equal("before", lines[0]);
        Assert.StartsWith("[IMAGE_PATH:", lines[1], StringComparison.Ordinal);
        Assert.Equal("between", lines[2]);
        Assert.StartsWith("[IMAGE_PATH:", lines[3], StringComparison.Ordinal);
        Assert.Equal("after", lines[4]);
        Assert.Equal(2, Directory.GetFiles(_cacheRoot, "media_*.png").Length);
    }

    [Fact]
    public async Task Ignores_messages_without_path_markers()
    {
        var text = "just text\n[IMAGE_URL:https://example.test/a.png]";

        Assert.Equal(text, await PersistAsync(text));
        Assert.Empty(Directory.GetFiles(_cacheRoot));
    }

    [Fact]
    public async Task Copies_documents_as_well_as_images()
    {
        var tempPath = await WriteTempFileAsync("report.pdf", [4, 5, 6]);

        var rewritten = await PersistAsync($"[FILE_PATH:{tempPath}]");

        var asset = Assert.Single(Directory.GetFiles(_cacheRoot, "media_*.pdf"));
        Assert.Contains($"[FILE_PATH:{asset}]", rewritten, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private Task<string> PersistAsync(string text)
        => InboundAttachmentPersister.PersistAsync(
            text,
            _mediaCache,
            _cacheRoot,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

    private async Task<string> WriteTempFileAsync(string fileName, byte[] content)
    {
        var directory = Path.Combine(_workspace, "channel-temp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
