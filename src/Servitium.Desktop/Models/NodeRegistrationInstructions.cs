using System;

namespace Servitium.Desktop.Models;

/// <summary>只在创建对话框中保留的一次性 Agent 注册资料。</summary>
internal sealed record NodeRegistrationInstructions(
    string ControlPlaneAddress,
    string ExpectedNodeName,
    string Token,
    DateTimeOffset ExpiresAt,
    string CommandTemplate)
{
    internal static NodeRegistrationInstructions Create(
        Uri baseAddress,
        string expectedNodeName,
        string token,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNodeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string address = baseAddress.AbsoluteUri.TrimEnd('/');
        string escapedName = expectedNodeName.Replace("\"", "\\\"", StringComparison.Ordinal);
        string command = $".\\Servitium.Agent.exe --server \"{address}\" --register-token \"<ONE_TIME_TOKEN>\" --node-name \"{escapedName}\"";
        return new(address, expectedNodeName.Trim(), token, expiresAt, command);
    }
}
