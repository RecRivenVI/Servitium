using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Infrastructure.Distribution;
using Servitium.Server.Hosting;

namespace Servitium.Server.Hmcl;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "ASP.NET Core endpoint parameter binding resolves this service through dependency injection.")]
internal sealed class StagedPrismUploadStore(ServerHostOptions options)
{
    private readonly string _root = Path.Combine(options.DataRoot, "staging", "prism-uploads");

    internal async Task<(Guid Id, HmclArchivePlan Plan)> SaveAndInspectAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 20L * 1024 * 1024 * 1024)
        {
            throw new Domain.Primitives.DomainException("archive-limit-exceeded", "The Prism archive size is outside the allowed range.");
        }

        _ = Directory.CreateDirectory(_root);
        var id = Guid.NewGuid();
        string path = PathFor(id);
        try
        {
            using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return (id, HmclDistributionStore.InspectPrismArchive(path));
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }

    internal string GetPath(Guid uploadId)
    {
        string path = PathFor(uploadId);
        return !File.Exists(path)
            ? throw new Domain.Primitives.DomainException("upload-session-expired", "The staged Prism upload does not exist.")
            : path;
    }

    internal void Delete(Guid uploadId)
    {
        string path = PathFor(uploadId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(Guid uploadId)
    {
        return Path.Combine(_root, uploadId.ToString("N") + ".zip");
    }
}
