namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// The one place in the Gateway that opens an attachment file.
/// </summary>
/// <remarks>
/// <para>
/// Two callers need the bytes and neither is the right home for the read: <c>MediaPayloadAdapter</c>
/// formats them for a provider, and <c>InboundAttachmentPersister</c> copies them into durable storage.
/// Duplicating the read would also duplicate its failure modes — a missing file, a permission error and
/// a locked file all have to degrade rather than throw, and that judgement is easier to keep consistent
/// in one method than in two.
/// </para>
/// <para>
/// Returning a bool instead of a nullable keeps the caller's shape simple: "not readable" is the common
/// case on the legacy path, not an exceptional one.
/// </para>
/// </remarks>
internal static class LocalAttachmentFile
{
    /// <summary>Reads the file at <paramref name="path"/>, or returns false if it cannot be read.</summary>
    public static bool TryRead(string? path, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!File.Exists(path))
                return false;

            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads the file behind a <c>file:</c> URI; false for any other scheme.</summary>
    public static bool TryRead(Uri? uri, out byte[] bytes)
    {
        bytes = [];
        if (uri is null || !uri.IsFile)
            return false;

        return TryRead(uri.LocalPath, out bytes);
    }
}
