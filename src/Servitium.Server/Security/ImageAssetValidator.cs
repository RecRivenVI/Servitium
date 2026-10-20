using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts;
using Servitium.Domain.Primitives;

namespace Servitium.Server.Security;

internal static class ImageAssetValidator
{
    internal static async Task ValidateAsync(string path, string extension, CancellationToken cancellationToken)
    {
        byte[] header = new byte[12];
        FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, header.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            int read = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            bool valid = extension switch
            {
                ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
                ".jpg" => read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
                ".webp" => read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
                _ => false,
            };
            if (!valid)
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The uploaded image signature does not match its declared type.");
            }
        }
    }
}
