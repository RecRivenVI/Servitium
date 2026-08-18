using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Instances;
using Servitium.Domain.Common;

namespace Servitium.Agent.Lifecycle;

internal sealed class MinecraftServerProtocolClient
{
    internal async Task<PlayerListDto> QueryAsync(string host, int port, CancellationToken cancellationToken)
    {
        ValidateEndpoint(host, port);
        using TcpClient client = new();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        await using (stream.ConfigureAwait(false))
        {

            using MemoryStream handshake = new();
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, -1);
            WriteString(handshake, host);
            Span<byte> portBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), timeout.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, [0], timeout.Token).ConfigureAwait(false);

            byte[] payload = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            int offset = 0;
            int packetId = ReadVarInt(payload, ref offset);
            if (packetId != 0)
            {
                throw new DomainException("query-failed", "The Minecraft status response has an unexpected packet identifier.");
            }

            string json = ReadString(payload, ref offset);
            using var document = JsonDocument.Parse(json);
            JsonElement players = document.RootElement.GetProperty("players");
            int online = players.TryGetProperty("online", out JsonElement onlineElement) ? onlineElement.GetInt32() : 0;
            int maximum = players.TryGetProperty("max", out JsonElement maximumElement) ? maximumElement.GetInt32() : 0;
            List<string> names = [];
            if (players.TryGetProperty("sample", out JsonElement sample) && sample.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in sample.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } value)
                    {
                        names.Add(value);
                    }
                }
            }

            string? version = document.RootElement.TryGetProperty("version", out JsonElement versionElement) &&
                              versionElement.TryGetProperty("name", out JsonElement versionName)
                ? versionName.GetString()
                : null;
            string? motd = document.RootElement.TryGetProperty("description", out JsonElement description)
                ? FlattenChat(description)
                : null;
            return new(online, maximum, names, "server-list-ping", version, motd);
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        using MemoryStream packet = new();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        int length = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length is < 1 or > 1024 * 1024)
        {
            throw new DomainException("query-failed", "The Minecraft status response size is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        int value = 0;
        for (int position = 0; position < 5; position++)
        {
            byte[] single = new byte[1];
            await stream.ReadExactlyAsync(single, cancellationToken).ConfigureAwait(false);
            value |= (single[0] & 0x7f) << (position * 7);
            if ((single[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new DomainException("query-failed", "The Minecraft status response contains an invalid VarInt.");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int value = 0;
        for (int position = 0; position < 5 && offset < bytes.Length; position++)
        {
            byte current = bytes[offset++];
            value |= (current & 0x7f) << (position * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new DomainException("query-failed", "The Minecraft status response contains an invalid VarInt.");
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int length = ReadVarInt(bytes, ref offset);
        if (length < 0 || offset + length > bytes.Length)
        {
            throw new DomainException("query-failed", "The Minecraft status response string is truncated.");
        }

        string value = new UTF8Encoding(false, true).GetString(bytes.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        uint remaining = unchecked((uint)value);
        do
        {
            byte current = (byte)(remaining & 0x7f);
            remaining >>= 7;
            if (remaining != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (remaining != 0);
    }

    private static void ValidateEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsControl) || port is < 1 or > 65535)
        {
            throw new DomainException("validation-failed", "The Minecraft query endpoint is invalid.");
        }
    }

    private static string FlattenChat(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        if (value.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            _ = builder.Append(text.GetString());
        }

        if (value.TryGetProperty("extra", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in extra.EnumerateArray())
            {
                _ = builder.Append(FlattenChat(child));
            }
        }

        return builder.ToString();
    }
}
