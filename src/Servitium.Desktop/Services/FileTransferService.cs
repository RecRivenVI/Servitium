using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Files;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Operations;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

internal sealed class FileTransferService(
    ServitiumApiClient client,
    LocalizationService localization,
    TransferPauseGate pause,
    Action<TransferProgressEventArgs> report)
{
    internal static FileStream OpenUpload(LocalizationService localization, string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? throw new ArgumentException(localization.Get("error.not-found"))
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal async Task<OperationAcceptedDto> UploadAsync(
        InstanceDto instance, string sourcePath, string destinationPath, string? modKind, CancellationToken cancellationToken, bool overwrite = false)
    {
        const int partSize = 8 * 1024 * 1024;
        FileStream source = OpenUpload(localization, sourcePath);
        await using (source.ConfigureAwait(false))
        {
            var stopwatch = Stopwatch.StartNew();
            Report(Path.GetFileName(sourcePath), 0, source.Length, stopwatch, "uploading");
            UploadSessionDto session = await client.CreateFileUploadAsync(instance.Id, instance.NodeId,
                new CreateUploadSessionRequest(Path.GetFileName(sourcePath), destinationPath, source.Length, partSize, overwrite), cancellationToken).ConfigureAwait(false);
            try
            {
                using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[partSize];
                for (int index = 0; ; index++)
                {
                    await pause.WaitAsync(cancellationToken).ConfigureAwait(false);
                    int length = await ReadPartAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                    if (length == 0) { break; }
                    fullHash.AppendData(buffer, 0, length);
                    string partHash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length)));
                    MemoryStream part = new(buffer, 0, length, writable: false, publiclyVisible: true);
                    _ = await client.PutFileUploadPartAsync(instance.Id, instance.NodeId, session.Id, index, partHash, part, length, cancellationToken).ConfigureAwait(false);
                    Report(Path.GetFileName(sourcePath), source.Position, source.Length, stopwatch, "uploading");
                }
                string sha256 = Convert.ToHexString(fullHash.GetHashAndReset());
                OperationAcceptedDto accepted = modKind is null
                    ? await client.CompleteFileUploadAsync(instance.Id, instance.NodeId, session.Id, new CompleteUploadRequest(sha256), cancellationToken).ConfigureAwait(false)
                    : await client.CompleteModUploadAsync(instance.Id, instance.NodeId, new UploadModRequest(session.Id, modKind, sha256), cancellationToken).ConfigureAwait(false);
                Report(Path.GetFileName(sourcePath), source.Length, source.Length, stopwatch, "processing");
                return accepted;
            }
            catch (Exception uploadError)
            {
                Report(Path.GetFileName(sourcePath), source.Position, source.Length, stopwatch, cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                try { await client.CancelFileUploadAsync(instance.Id, instance.NodeId, session.Id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanupError) { throw new AggregateException(uploadError, cleanupError); }
                throw;
            }
        }
    }

    private static async Task<int> ReadPartAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            total += read;
        }
        return total;
    }
    private void Report(string fileName, long completed, long total, Stopwatch stopwatch, string state)
    {
        report(TransferProgressEventArgs.Create(fileName, completed, total, stopwatch, state));
    }
    internal static List<(string Source, string Relative)> EnumerateDroppedFiles(IReadOnlyList<string> paths)
    {
        List<(string Source, string Relative)> result = [];
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("A dropped file is a reparse point.", nameof(paths)); }
                result.Add((path, Path.GetFileName(path)));
                continue;
            }

            DirectoryInfo root = new(path);
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("A dropped path is invalid or is a reparse point.", nameof(paths));
            }

            Stack<DirectoryInfo> pending = new();
            pending.Push(root);
            while (pending.TryPop(out DirectoryInfo? directory))
            {
                foreach (FileInfo file in directory.EnumerateFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ArgumentException("A dropped file is a reparse point.", nameof(paths));
                    }

                    result.Add((file.FullName, Path.Combine(root.Name, Path.GetRelativePath(root.FullName, file.FullName))));
                }

                foreach (DirectoryInfo child in directory.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ArgumentException("A dropped directory is a reparse point.", nameof(paths));
                    }

                    pending.Push(child);
                }
            }
        }

        return result;
    }

}
