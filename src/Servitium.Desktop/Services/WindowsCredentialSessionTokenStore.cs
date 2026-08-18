using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client.Session;

using Windows.Security.Credentials;

namespace Servitium.Desktop.Services;

internal sealed class WindowsCredentialSessionTokenStore : ISessionTokenStore
{
    private const string UserName = "session";
    private readonly PasswordVault _vault = new();

    public Task<string?> ReadAsync(string serverKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        cancellationToken.ThrowIfCancellationRequested();
        PasswordCredential? credential;
        try
        {
            credential = _vault.FindAllByResource(Resource(serverKey)).FirstOrDefault(item => item.UserName == UserName);
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        {
            credential = null;
        }

        credential?.RetrievePassword();
        return Task.FromResult(credential?.Password);
    }

    public Task WriteAsync(string serverKey, string? token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        cancellationToken.ThrowIfCancellationRequested();
        string resource = Resource(serverKey);
        try
        {
            foreach (PasswordCredential existing in _vault.FindAllByResource(resource).Where(item => item.UserName == UserName))
            {
                _vault.Remove(existing);
            }
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        {
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _vault.Add(new PasswordCredential(resource, UserName, token));
        }

        return Task.CompletedTask;
    }

    private static string Resource(string serverKey)
    {
        return "Servitium.Desktop/" + serverKey;
    }
}
