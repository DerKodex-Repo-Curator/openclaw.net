using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Turns a <see cref="ChatTurn"/> into the provider-facing content list.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place where a turn's media becomes <see cref="AIContent"/>. Before attachment
/// references existed the same logic lived here <em>and</em> — dead — in <c>AgentRuntime</c>; keeping
/// one copy is what makes "the reference path and the marker path agree" a checkable claim rather
/// than a hope.
/// </para>
/// <para>
/// <b>Why two branches.</b> A turn with <see cref="ChatTurn.Attachments"/> null is either
/// attachment-free or written by an older build, and both must behave exactly as before, so it goes
/// through the marker parser. A turn with references skips the parser entirely and uses the stored
/// kind/value. Both branches end in the same <see cref="Append"/> so the per-marker rules cannot
/// drift apart.
/// </para>
/// </remarks>
internal static class AttachmentContentRenderer
{
    public static IList<AIContent> Build(string content, List<ChatAttachmentRef>? attachments)
    {
        var contents = new List<AIContent>();

        if (attachments is not null)
        {
            if (!string.IsNullOrWhiteSpace(content))
                contents.Add(new TextContent(content));

            foreach (var attachment in attachments)
                Append(contents, attachment.Kind, attachment.Value);
        }
        else
        {
            var (markers, remainingText) = MediaMarkerProtocol.Extract(content);
            if (!string.IsNullOrWhiteSpace(remainingText))
                contents.Add(new TextContent(remainingText));

            foreach (var marker in markers)
                Append(contents, marker.Kind, marker.Value);
        }

        // A turn whose text was empty but which carried only unusable references still needs to send
        // *something*; this mirrors the old fallback exactly.
        if (contents.Count == 0)
            contents.Add(new TextContent(content));

        return contents;
    }

    /// <summary>
    /// One marker → one content part, with the degradation rules the marker path always had.
    /// </summary>
    private static void Append(List<AIContent> contents, MediaMarkerKind kind, string value)
    {
        var mediaType = AttachmentReferences.MediaClassFor(kind);

        switch (kind)
        {
            case MediaMarkerKind.ImagePath:
            case MediaMarkerKind.FilePath:
                contents.Add(new UriContent(new Uri(Path.GetFullPath(value)), mediaType));
                break;
            default:
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    contents.Add(new UriContent(uri, mediaType));
                else if (Uri.TryCreate(value, UriKind.Relative, out _))
                    contents.Add(new TextContent(value));
                break;
        }
    }
}
