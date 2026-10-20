using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Lifecycle;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class MinecraftProtocolClientTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-650")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The synthetic server task is awaited before the listener leaves scope.")]
    public async Task ServerListPing_发送兼容握手并解析状态响应()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = RunStatusServerAsync(listener);

        PlayerListDto result = await new MinecraftServerProtocolClient()
            .QueryAsync("127.0.0.1", port, CancellationToken.None)
            .ConfigureAwait(true);
        await server.ConfigureAwait(true);

        Assert.Equal(2, result.Online);
        Assert.Equal(20, result.Maximum);
        Assert.Equal(["Alice", "Bob"], result.Players);
        Assert.Equal("Synthetic 1.0", result.Version);
        Assert.Equal("Servitium fixture", result.Motd);
        Assert.Equal("server-list-ping", result.Source);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-660")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The synthetic server task is awaited before the listener leaves scope.")]
    public async Task Rcon_断线后重连并拼接多包响应()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = RunRconServerAsync(listener);

        string result = await new RconClient()
            .ExecuteAsync("127.0.0.1", port, "synthetic-secret", "list", CancellationToken.None)
            .ConfigureAwait(true);
        await server.ConfigureAwait(true);

        Assert.Equal("There are 2 of 20 players: Alice, Bob", result);
    }

    [Theory]
    [InlineData("", 25565)]
    [InlineData("127.0.0.1\ninvalid", 25565)]
    [InlineData("127.0.0.1", 0)]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-230")]
    public async Task ServerListPing_拒绝非法端点(string host, int port)
    {
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            new MinecraftServerProtocolClient().QueryAsync(host, port, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("validation-failed", exception.Code);
    }

    [Theory]
    [InlineData("", "list")]
    [InlineData("secret", "say one\nsay two")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-231")]
    public async Task Rcon_拒绝空密码与多行命令(string password, string command)
    {
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            new RconClient().ExecuteAsync("127.0.0.1", 25575, password, command, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("validation-failed", exception.Code);
    }

    private static async Task RunStatusServerAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        byte[] handshake = await ReadMinecraftPacketAsync(stream).ConfigureAwait(false);
        int offset = 0;
        Assert.Equal(0, ReadVarInt(handshake, ref offset));
        Assert.Equal(-1, ReadVarInt(handshake, ref offset));
        _ = ReadMinecraftString(handshake, ref offset);
        offset += 2;
        Assert.Equal(1, ReadVarInt(handshake, ref offset));

        byte[] request = await ReadMinecraftPacketAsync(stream).ConfigureAwait(false);
        Assert.Equal([0], request);
        const string json = /*lang=json,strict*/ "{\"version\":{\"name\":\"Synthetic 1.0\",\"protocol\":769},\"players\":{\"max\":20,\"online\":2,\"sample\":[{\"name\":\"Alice\",\"id\":\"00000000-0000-0000-0000-000000000001\"},{\"name\":\"Bob\",\"id\":\"00000000-0000-0000-0000-000000000002\"}]},\"description\":{\"text\":\"Servitium \",\"extra\":[{\"text\":\"fixture\"}]}}";
        using MemoryStream response = new();
        WriteVarInt(response, 0);
        WriteMinecraftString(response, json);
        await WriteMinecraftPacketAsync(stream, response.ToArray()).ConfigureAwait(false);
    }

    private static async Task RunRconServerAsync(TcpListener listener)
    {
        using (TcpClient disconnected = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
        {
            disconnected.Client.LingerState = new(true, 0);
        }

        using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        RconPacket authentication = await ReadRconPacketAsync(stream).ConfigureAwait(false);
        Assert.Equal(3, authentication.Type);
        Assert.Equal("synthetic-secret", authentication.Payload);
        await WriteRconPacketAsync(stream, authentication.Id, 2, string.Empty).ConfigureAwait(false);

        RconPacket command = await ReadRconPacketAsync(stream).ConfigureAwait(false);
        Assert.Equal(2, command.Type);
        Assert.Equal("list", command.Payload);
        RconPacket sentinel = await ReadRconPacketAsync(stream).ConfigureAwait(false);
        Assert.Equal(0, sentinel.Type);

        await WriteRconPacketAsync(stream, command.Id, 0, "There are 2 of 20 ").ConfigureAwait(false);
        await WriteRconPacketAsync(stream, command.Id, 0, "players: Alice, Bob").ConfigureAwait(false);
        await WriteRconPacketAsync(stream, sentinel.Id, 0, string.Empty).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadMinecraftPacketAsync(Stream stream)
    {
        int length = await ReadVarIntAsync(stream).ConfigureAwait(false);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteMinecraftPacketAsync(Stream stream, byte[] payload)
    {
        using MemoryStream packet = new();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray()).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream)
    {
        int value = 0;
        for (int position = 0; position < 5; position++)
        {
            int current = stream.ReadByte();
            if (current < 0)
            {
                throw new EndOfStreamException();
            }

            value |= (current & 0x7f) << (position * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }

            await Task.Yield();
        }

        throw new InvalidDataException("Synthetic packet contains an invalid VarInt.");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int value = 0;
        for (int position = 0; position < 5; position++)
        {
            byte current = bytes[offset++];
            value |= (current & 0x7f) << (position * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Synthetic packet contains an invalid VarInt.");
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

    private static string ReadMinecraftString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int length = ReadVarInt(bytes, ref offset);
        string value = Encoding.UTF8.GetString(bytes.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void WriteMinecraftString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task<RconPacket> ReadRconPacketAsync(Stream stream)
    {
        byte[] lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        byte[] packet = new byte[length];
        await stream.ReadExactlyAsync(packet).ConfigureAwait(false);
        return new(
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4)),
            Encoding.UTF8.GetString(packet, 8, length - 10));
    }

    private static async Task WriteRconPacketAsync(Stream stream, int id, int type, string payload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        int length = 10 + payloadBytes.Length;
        byte[] packet = new byte[length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        payloadBytes.CopyTo(packet.AsSpan(12));
        await stream.WriteAsync(packet).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private sealed record RconPacket(int Id, int Type, string Payload);
}
