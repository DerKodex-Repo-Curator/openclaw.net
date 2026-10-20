using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// Last-resort adaptation from a media URI to the payload a local provider client can serialise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this still exists.</b> Since attachment inlining landed (P2), a locally-attached file reaches
/// a chat client as <c>DataContent</c> — bytes, already budgeted and deduplicated upstream. This class
/// handles the two cases the inliner does not own: sessions written before attachment references
/// (markers still inside <c>ChatTurn.Content</c>) and <c>ReferenceMode = false</c>, both of which still
/// produce <c>UriContent</c> carrying a <c>file:</c> URI.
/// </para>
/// <para>
/// <b>Why not just delete it.</b> Deleting it would make the rollback switch unable to serve local
/// images at all — the flag would then only roll back the storage format while silently breaking a
/// provider. A rollback switch that breaks something is worse than no rollback switch. What matters is
/// that the conversion lives in exactly one place and is purely mechanical: no budget, no window, no
/// judgement about whether to send. That judgement is <c>AttachmentInliner</c>'s job and stays there.
/// </para>
/// <para>
/// Deliberately synchronous: <c>OllamaChatClient</c> builds its message JSON inside an iterator, so an
/// async signature would force either a restructure or a blocking wait inside an async method. These
/// reads only happen on the legacy path, which shrinks on its own as old sessions age out.
/// </para>
/// <para>
/// It does not open files itself — <see cref="LocalAttachmentFile"/> is the only reader in the Gateway,
/// shared with the inbound persister. This class only decides which of the three provider-visible shapes
/// (bare base64, data URL, untouched URI) a given URI turns into.
/// </para>
/// </remarks>
internal static class MediaPayloadAdapter
{
    /// <summary>
    /// Bare base64 for providers that want base64 in their own field (Ollama's <c>images</c> array).
    /// </summary>
    /// <returns><see langword="null"/> when the URI is neither a readable file nor a data URI.</returns>
    public static string? ToBase64(Uri? uri)
    {
        if (uri is null)
            return null;

        if (uri.IsFile)
            return LocalAttachmentFile.TryRead(uri, out var fileBytes) ? Convert.ToBase64String(fileBytes) : null;

        if (IsDataUri(uri))
        {
            var raw = uri.OriginalString;
            var comma = raw.IndexOf(',', StringComparison.Ordinal);
            return comma >= 0 && comma + 1 < raw.Length ? raw[(comma + 1)..] : null;
        }

        return null;
    }

    /// <summary>
    /// A <c>data:</c> URL for providers that take an OpenAI-style <c>{"url": ...}</c> part.
    /// </summary>
    /// <param name="uri">The media URI.</param>
    /// <param name="declaredMediaType">
    /// Media type carried on the content part. Usually the wildcard <c>"image/*"</c>, which is not a
    /// valid MIME type inside a data URL, so it is only trusted when it is concrete.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the URI should be forwarded untouched — remote URLs, which the
    /// provider fetches itself, and anything unreadable.
    /// </returns>
    public static string? ToDataUrl(Uri? uri, string? declaredMediaType)
    {
        if (uri is null)
            return null;

        if (IsDataUri(uri))
            return uri.OriginalString;

        if (!uri.IsFile)
            return null;

        if (!LocalAttachmentFile.TryRead(uri, out var bytes))
            return null;

        var mediaType = ResolveConcreteMediaType(uri, declaredMediaType);
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static bool IsDataUri(Uri uri) => uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The content part's media type is <c>"image/*"</c> for every image marker kind (that is what the
    /// marker path always passed to <c>UriContent</c>), and a wildcard produces an invalid data URL.
    /// The file extension is the only concrete signal available here.
    /// </summary>
    private static string ResolveConcreteMediaType(Uri uri, string? declaredMediaType)
    {
        if (declaredMediaType is { Length: > 0 } && !declaredMediaType.Contains('*', StringComparison.Ordinal))
            return declaredMediaType;

        return AttachmentReferences.TryResolveMediaType(uri.LocalPath) ?? "application/octet-stream";
    }
}
