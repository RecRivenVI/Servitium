using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

internal sealed class ServerProfileService(DesktopSettingsService settings, TimeProvider timeProvider)
{
    internal IReadOnlyList<ServerProfile> Profiles => settings.Current.Servers;

    internal ServerProfile Active =>
        Profiles.FirstOrDefault(item => item.Id == settings.Current.ActiveServerId) ?? Profiles[0];

    internal async Task<ServerProfile> AddOrSelectAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new ArgumentException("The Server address is invalid.", nameof(address));
        }

        Uri normalized = new(address.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
        ServerProfile? selected = null;
        await settings.UpdateAsync(current =>
        {
            List<ServerProfile> profiles = [.. current.Servers];
            int index = profiles.FindIndex(item => Uri.Compare(item.Address, normalized, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);
            selected = index >= 0
                ? profiles[index] with { LastUsedAt = timeProvider.GetUtcNow() }
                : new ServerProfile(Guid.NewGuid(), normalized.Host, normalized, timeProvider.GetUtcNow());
            if (index >= 0)
            {
                profiles[index] = selected;
            }
            else
            {
                profiles.Add(selected);
            }

            return current with { Servers = profiles, ActiveServerId = selected.Id };
        }, cancellationToken).ConfigureAwait(false);
        return selected!;
    }

    internal async Task SelectAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (!Profiles.Any(item => item.Id == profileId))
        {
            throw new ArgumentException("The Server profile does not exist.", nameof(profileId));
        }

        await settings.UpdateAsync(current => current with { ActiveServerId = profileId }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RenameAsync(Guid profileId, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        if (normalized.Length > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        await settings.UpdateAsync(current => current with
        {
            Servers = [.. current.Servers.Select(item => item.Id == profileId ? item with { Name = normalized } : item)],
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(current =>
        {
            List<ServerProfile> remaining = [.. current.Servers.Where(item => item.Id != profileId)];
            if (remaining.Count == 0)
            {
                throw new InvalidOperationException("At least one Server profile must remain.");
            }

            Guid active = current.ActiveServerId == profileId || current.ActiveServerId is null
                ? remaining.OrderByDescending(item => item.LastUsedAt).First().Id
                : current.ActiveServerId.Value;
            return current with { Servers = remaining, ActiveServerId = active };
        }, cancellationToken).ConfigureAwait(false);
    }
}
