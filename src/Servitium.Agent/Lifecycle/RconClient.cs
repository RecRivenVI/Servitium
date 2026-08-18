using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Domain.Common;

namespace Servitium.Agent.Lifecycle;

internal sealed class RconClient
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    internal async Task<string> ExecuteAsync(string host, int port, string password, string command, CancellationToken cancellationToken)
    {
        Validate(host, port, password, command);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(host, port, password, command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt == 0 && exception is IOException or SocketException)
            {
            }
        }
    }

    private async Task<string> ExecuteOnceAsync(string host, int port, string password, string command, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        await using (stream.ConfigureAwait(false))
        {
            int authenticationId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await WritePacketAsync(stream, authenticationId, 3, password, timeout.Token).ConfigureAwait(false);
            RconPacket authentication = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            if (authentication.Id != authenticationId)
            {
                throw new DomainException("rcon-auth-failed", "RCON authentication failed.");
            }

            int commandId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await WritePacketAsync(stream, commandId, 2, command, timeout.Token).ConfigureAwait(false);
            int sentinelId;
            do
            {
                sentinelId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            }
            while (sentinelId == commandId || sentinelId == authenticationId);

            await WritePacketAsync(stream, sentinelId, 0, string.Empty, timeout.Token).ConfigureAwait(false);
            StringBuilder response = new();
            while (true)
            {
                RconPacket packet = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
                if (packet.Id == sentinelId)
                {
                    break;
                }

                if (packet.Id != commandId)
                {
                    throw new DomainException("rcon-failed", "RCON returned an unexpected response.");
                }

                if (response.Length + packet.Payload.Length > 4 * 1024 * 1024)
                {
                    throw new DomainException("rcon-failed", "RCON returned too much response data.");
                }

                _ = response.Append(packet.Payload);
            }

            return response.ToString();
        }
    }

    private static async Task WritePacketAsync(Stream stream, int id, int type, string payload, CancellationToken cancellationToken)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        int length = checked(4 + 4 + payloadBytes.Length + 2);
        byte[] buffer = new byte[length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), type);
        payloadBytes.CopyTo(buffer.AsSpan(12));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RconPacket> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is < 10 or > 4 * 1024 * 1024)
        {
            throw new DomainException("rcon-failed", "RCON returned an invalid packet length.");
        }

        byte[] packet = new byte[length];
        await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);
        if (packet[^1] != 0 || packet[^2] != 0)
        {
            throw new DomainException("rcon-failed", "RCON returned an invalid packet terminator.");
        }

        int id = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4));
        string payload = new UTF8Encoding(false, true).GetString(packet, 8, length - 10);
        return new(id, type, payload);
    }

    private static void Validate(string host, int port, string password, string command)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsControl) || port is < 1 or > 65535 ||
            string.IsNullOrEmpty(password) || password.Any(character => character is '\0' or '\r' or '\n') ||
            string.IsNullOrWhiteSpace(command) || command.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
        {
            throw new DomainException("validation-failed", "The RCON request is invalid.");
        }
    }

    private sealed record RconPacket(int Id, int Type, string Payload);
}
