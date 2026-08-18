using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

using Servitium.Application.Identity;
using Servitium.Contracts.Settings;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

namespace Servitium.Server.Governance;

internal sealed class RuntimeSettings(
    ControlPlaneStore store,
    IdentityServiceOptions identity,
    TotpOptions totp) : IHostedService
{
    private int _userFileManagementEnabled = 1;
    private int _userFailureLimit = 5;
    private int _ipFailureLimit = 20;
    private int _ipFailureWindowMinutes = 10;
    private int _ipBanMinutes = 30;
    private int _advancedRawCommandsEnabled;
    private string[] _privateSourceAuthorities = [];
    private string[] _embedAllowedOrigins = [];

    internal bool UserFileManagementEnabled => Volatile.Read(ref _userFileManagementEnabled) != 0;
    internal int UserFailureLimit => Volatile.Read(ref _userFailureLimit);
    internal int IpFailureLimit => Volatile.Read(ref _ipFailureLimit);
    internal int IpFailureWindowMinutes => Volatile.Read(ref _ipFailureWindowMinutes);
    internal int IpBanMinutes => Volatile.Read(ref _ipBanMinutes);
    internal bool AdvancedRawCommandsEnabled => Volatile.Read(ref _advancedRawCommandsEnabled) != 0;
    internal IReadOnlyList<string> PrivateSourceAuthorities => Volatile.Read(ref _privateSourceAuthorities);
    internal IReadOnlyList<string> EmbedAllowedOrigins => Volatile.Read(ref _embedAllowedOrigins);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SettingsGroupDto> groups = await store.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
        foreach (SettingsGroupDto group in groups)
        {
            Apply(group.Group, group.Values);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    internal void Apply(string group, IReadOnlyDictionary<string, string?> values)
    {
        if (group != "security")
        {
            return;
        }

        var idle = TimeSpan.FromHours(Int(values, "sessionIdleHours", 12, 1, 168));
        var absolute = TimeSpan.FromHours(Int(values, "sessionAbsoluteHours", 168, 2, 720));
        if (absolute <= idle)
        {
            absolute = idle + TimeSpan.FromHours(1);
        }

        identity.Update(idle, absolute, Bool(values, "apiKeysEnabled", true));
        totp.PermittedDriftSteps = Int(values, "totpDriftSteps", 1, 0, 4);
        Volatile.Write(ref _userFileManagementEnabled, Bool(values, "userFileManagementEnabled", true) ? 1 : 0);
        Volatile.Write(ref _userFailureLimit, Int(values, "userFailureLimit", 5, 1, 20));
        Volatile.Write(ref _ipFailureLimit, Int(values, "ipFailureLimit", 20, 1, 200));
        Volatile.Write(ref _ipFailureWindowMinutes, Int(values, "ipFailureWindowMinutes", 10, 1, 1440));
        Volatile.Write(ref _ipBanMinutes, Int(values, "ipBanMinutes", 30, 1, 10080));
        Volatile.Write(ref _advancedRawCommandsEnabled, Bool(values, "advancedRawCommandsEnabled", false) ? 1 : 0);
        string[] authorities = (values.TryGetValue("privateSourceAllowList", out string? privateSources) ? privateSources : null)
            ?.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        Volatile.Write(ref _privateSourceAuthorities, authorities);
        string[] embedOrigins = (values.TryGetValue("embedAllowedOrigins", out string? embedSources) ? embedSources : null)
            ?.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        Volatile.Write(ref _embedAllowedOrigins, embedOrigins);
    }

    private static bool Bool(IReadOnlyDictionary<string, string?> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out string? raw) && bool.TryParse(raw, out bool parsed) ? parsed : fallback;
    }

    private static int Int(IReadOnlyDictionary<string, string?> values, string key, int fallback, int minimum, int maximum)
    {
        return values.TryGetValue(key, out string? raw) && int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
