namespace OpenClaw.Core.Models;

/// <summary>
/// A media attachment carried <em>by reference</em> instead of by a literal marker line inside
/// <see cref="ChatTurn.Content"/>.
/// </summary>
/// <remarks>
/// <para>
/// The marker protocol (<see cref="MediaMarkerProtocol"/>) stays the wire format: channels, plugins
/// and out-of-process adapters keep emitting <c>[IMAGE_URL:...]</c> lines. This record is what the
/// session history stores instead, so that history holds a described reference rather than a raw
/// path or URL smeared into prose.
/// </para>
/// <para>
/// <b>P1 shape.</b> <see cref="Kind"/> and <see cref="Value"/> are always populated, which is what
/// makes the read path able to rebuild byte-for-byte what the marker path produced.
/// <see cref="AssetId"/>, <see cref="Digest"/> and <see cref="SizeBytes"/> are filled from P2
/// onward, when the inliner starts resolving bytes through the media cache; until then they are
/// null/0 and nothing reads them. Keeping the original value means a P1 session can be read by P1
/// code only — there is no cache write in this phase.
/// </para>
/// </remarks>
public sealed record ChatAttachmentRef
{
    public required MediaMarkerKind Kind { get; init; }

    /// <summary>The marker payload: an absolute path, a URL, or a channel file id.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// Media class derived from <see cref="Kind"/> when the reference is written ("image/*",
    /// "audio/*", "video/*", or "application/octet-stream"). Derived rather than sniffed: P1 must
    /// not read the file, and the marker kind is all the old path used anyway.
    /// </summary>
    public string? MimeType { get; init; }

    /// <summary>Best-effort display name for placeholders and operator-facing output.</summary>
    public string? FileName { get; init; }

    /// <summary>0 when unknown. P1 never opens the file, so this stays 0 until P2 fills it.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Content digest for in-request deduplication. Null in P1.</summary>
    public string? Digest { get; init; }

    /// <summary>Media-cache asset id. Null in P1 (nothing is copied into the cache yet).</summary>
    public string? AssetId { get; init; }
}

/// <summary>
/// Conversion between the marker wire format and <see cref="ChatAttachmentRef"/>.
/// </summary>
/// <remarks>
/// Everything here is a pure function of its input. That is deliberate: the whole point of P1 is
/// that the reference round-trip is <em>provably</em> equivalent to the marker path, which is only
/// checkable if neither side has IO, time, or ambient state in it.
/// </remarks>
public static class AttachmentReferences
{
    /// <summary>
    /// Lifts marker lines out of <paramref name="content"/> into structured references.
    /// </summary>
    /// <returns>
    /// The remaining prose and the references. When there are no markers, <c>content</c> is returned
    /// unchanged and the references are <see langword="null"/> — the null is load-bearing: it marks
    /// a turn as legacy (or attachment-free) so the read path keeps using the marker parser, which
    /// is what makes old sessions behave exactly as they did before.
    /// </returns>
    public static (string Content, List<ChatAttachmentRef>? Attachments) Extract(string content)
    {
        if (string.IsNullOrEmpty(content))
            return (content, null);

        var (markers, remainingText) = MediaMarkerProtocol.Extract(content);
        if (markers.Count == 0)
            return (content, null);

        var attachments = new List<ChatAttachmentRef>(markers.Count);
        foreach (var marker in markers)
        {
            attachments.Add(new ChatAttachmentRef
            {
                Kind = marker.Kind,
                Value = marker.Value,
                MimeType = MediaClassFor(marker.Kind),
                FileName = GuessFileName(marker),
                AssetId = TryGetMediaAssetId(marker.Value, out var assetId) ? assetId : null
            });
        }

        return (remainingText, attachments);
    }

    /// <summary>
    /// Prefix of the ids the media cache generates. Shared with the store that produces them, so the
    /// recognition in <see cref="TryGetMediaAssetId"/> is a contract rather than a coincidence that
    /// breaks when someone renames a directory.
    /// </summary>
    public const string MediaAssetIdPrefix = "media_";

    /// <summary>Hex characters after the prefix in a generated id.</summary>
    private const int MediaAssetIdHexLength = 16;

    /// <summary>
    /// Recovers a media-cache asset id from a path or URL that points at a cached asset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attachment references carry the payload's location in <see cref="ChatAttachmentRef.Value"/>
    /// because that is what makes them readable without an indirection layer. When the location happens
    /// to be a media-cache file, the id is recoverable from the file name — so it is recovered here and
    /// recorded, which is what lets an operator correlate a placeholder or a log line with a file on
    /// disk without guessing from a GUID-looking path.
    /// </para>
    /// <para>
    /// Deliberately strict: the exact prefix, the exact hex length, lowercase only. A looser match would
    /// turn a user file that happens to be called <c>media_photo.png</c> into a bogus asset reference,
    /// and the failure would be a placeholder pointing at nothing.
    /// </para>
    /// </remarks>
    public static bool TryGetMediaAssetId(string? value, out string assetId)
    {
        assetId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(value);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (stem.Length != MediaAssetIdPrefix.Length + MediaAssetIdHexLength ||
            !stem.StartsWith(MediaAssetIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = MediaAssetIdPrefix.Length; index < stem.Length; index++)
        {
            if (!char.IsAsciiHexDigitLower(stem[index]))
                return false;
        }

        assetId = stem;
        return true;
    }

    /// <summary>
    /// Builds the user turn for a freshly received message, lifting its attachments.
    /// </summary>
    /// <param name="message">The inbound message, markers and all.</param>
    /// <param name="referenceMode">
    /// When false the markers stay in <see cref="ChatTurn.Content"/> and the turn is byte-for-byte what
    /// the pre-reference build produced. This is the write half of the rollback switch: turning read
    /// time back on is enough for behaviour, but rollback should restore the storage format too.
    /// </param>
    public static ChatTurn CreateUserTurn(string message, bool referenceMode = true)
    {
        if (!referenceMode)
            return new ChatTurn { Role = "user", Content = message };

        var (content, attachments) = Extract(message);
        return new ChatTurn { Role = "user", Content = content, Attachments = attachments };
    }

    /// <summary>
    /// Renders a turn back to the marker wire format, for consumers that can only send text (the MAF
    /// runtime before P2 gives it multimodal support).
    /// </summary>
    /// <remarks>
    /// This is <em>semantic</em> equivalence, not byte equivalence: marker lines always land at the
    /// end of the text instead of wherever the channel originally put them. In practice channels
    /// append them as trailing lines already, so the rendered text matches what the model saw
    /// before. The alternative — keeping markers inside <see cref="ChatTurn.Content"/> for MAF —
    /// would defeat the point of the phase.
    /// </remarks>
    public static string RenderMarkerText(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Attachments is not { Count: > 0 } attachments)
            return turn.Content;

        var lines = new List<string>(attachments.Count);
        foreach (var attachment in attachments)
            lines.Add(ToMarkerLine(attachment));

        var markers = string.Join('\n', lines);
        return string.IsNullOrWhiteSpace(turn.Content) ? markers : turn.Content.TrimEnd() + "\n" + markers;
    }

    /// <summary>
    /// A short note telling a prose-only consumer that this turn carried media.
    /// </summary>
    /// <remarks>
    /// Used by history compaction, which flattens turns to text. Before attachment references the
    /// marker line itself recorded this — and leaked the URL into the summary. This keeps the fact
    /// and drops the payload, so a summarized turn still reads as "there was a picture here".
    /// </remarks>
    public static string SummarizationNote(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return turn.Attachments is { Count: > 0 } attachments
            ? $" [{attachments.Count} attachment(s) omitted]"
            : string.Empty;
    }

    /// <summary>The exact marker line this reference was parsed from.</summary>
    public static string ToMarkerLine(ChatAttachmentRef attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return MediaMarkerProtocol.Format(new MediaMarker(attachment.Kind, attachment.Value));
    }

    /// <summary>
    /// Media class for a marker kind. Must stay identical to the mapping the marker path used, or
    /// the reference path would send a different <c>mediaType</c> to the provider.
    /// </summary>
    public static string MediaClassFor(MediaMarkerKind kind) => kind switch
    {
        MediaMarkerKind.ImageUrl or MediaMarkerKind.ImagePath or MediaMarkerKind.TelegramImageFileId => "image/*",
        MediaMarkerKind.AudioUrl or MediaMarkerKind.TelegramAudioFileId => "audio/*",
        MediaMarkerKind.VideoUrl or MediaMarkerKind.TelegramVideoFileId => "video/*",
        MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl or MediaMarkerKind.FilePath
            or MediaMarkerKind.TelegramDocumentFileId => "application/octet-stream",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// A real MIME type for <paramref name="attachment"/>, resolved from its file name extension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MediaClassFor"/> returns a wildcard (<c>"image/*"</c>) because that is all the marker
    /// path ever passed to <c>UriContent</c>, where providers treat it as a hint. <c>DataContent</c>
    /// is different: the media type becomes the header of a <c>data:</c> URL, and
    /// <c>data:image/*;base64,...</c> is not a valid MIME type — providers reject it. So inlining by
    /// value needs a concrete type, and the only signal available without reading the bytes is the
    /// extension.
    /// </para>
    /// <para>
    /// Unknown extensions fall back to <c>application/octet-stream</c> rather than guessing from the
    /// kind. Guessing <c>image/jpeg</c> for an extensionless file would let a mislabelled payload
    /// reach the model as if it were valid; octet-stream makes the failure explicit instead.
    /// </para>
    /// </remarks>
    public static string ResolveMediaType(ChatAttachmentRef attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var extension = ExtensionOf(attachment);
        if (extension is not null && ConcreteMediaTypes.TryGetValue(extension, out var mediaType))
            return mediaType;

        return "application/octet-stream";
    }

    /// <summary>
    /// Concrete MIME type for a file name or path, or <see langword="null"/> when the extension is not
    /// in the table.
    /// </summary>
    /// <remarks>
    /// Exposed for callers that hold a path rather than a <see cref="ChatAttachmentRef"/> — notably the
    /// last-resort provider adapters that still materialise a <c>file:</c> URI. Returning null instead
    /// of a default lets the caller decide whether "unknown" means octet-stream or "leave it alone",
    /// which are different answers in that context.
    /// </remarks>
    public static string? TryResolveMediaType(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return null;

        var extension = Path.GetExtension(pathOrName);
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        return ConcreteMediaTypes.TryGetValue(extension, out var mediaType) ? mediaType : null;
    }

    private static readonly Dictionary<string, string> ConcreteMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".py"] = "text/x-python",
        [".sh"] = "text/x-sh",
        [".zip"] = "application/zip",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    /// <summary>
    /// File extension of the referenced object, taken from <see cref="ChatAttachmentRef.FileName"/>
    /// when known and from the path/URL tail otherwise.
    /// </summary>
    private static string? ExtensionOf(ChatAttachmentRef attachment)
    {
        var candidate = attachment.FileName;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            if (attachment.Kind is MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath)
            {
                candidate = attachment.Value;
            }
            else if (Uri.TryCreate(attachment.Value, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
            {
                // AbsolutePath drops the query string, which is where signed URLs keep their expiry
                // rather than their extension.
                candidate = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
            }
            else
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var extension = Path.GetExtension(candidate);
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }

    private static string? GuessFileName(MediaMarker marker)
    {
        var value = marker.Value;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // A local path gives the name directly; a URL gives its last path segment. Anything else
        // (a channel file id, a data URI) has no meaningful name and is left null rather than
        // producing 200 characters of base64 in the middle of a placeholder.
        if (marker.Kind is MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath)
        {
            var name = Path.GetFileName(value);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
        {
            var name = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }
}
