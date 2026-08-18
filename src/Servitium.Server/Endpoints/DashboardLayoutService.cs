using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Settings;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;
using Servitium.Server.Governance;

namespace Servitium.Server.Endpoints;

/// <summary>验证、升级并持久化每用户页面布局。</summary>
internal sealed class DashboardLayoutService(ControlPlaneStore store, RuntimeSettings runtimeSettings)
{
    private static readonly IReadOnlyDictionary<string, DashboardLayoutDto> Defaults =
        new Dictionary<string, DashboardLayoutDto>(StringComparer.Ordinal)
        {
            ["dashboard"] = new("dashboard", 1,
            [
                Card("node-status", "node-status", 3, "small", required: true),
                Card("instance-status", "instance-status", 3, "small", required: true),
                Card("session-status", "session-status", 3, "small", required: true),
                Card("system-status", "system-status", 3, "small", required: true),
                Card("system-info", "system-info", 12, "medium", required: true),
                Card("activity", "activity", 4, "medium", required: false),
                Card("node-overview", "node-overview", 8, "medium", required: false),
                Card("request-chart", "request-chart", 6, "small", required: false),
                Card("instance-chart", "instance-chart", 6, "small", required: false),
            ]),
            ["my-instances"] = new("my-instances", 1,
            [
                Card("summary", "summary", 12, "small", required: true),
                Card("instances", "instances", 12, "adaptive", required: true),
            ]),
            ["instance-console"] = new("instance-console", 1,
            [
                Card("terminal", "terminal", 12, "large", required: true),
                Card("summary", "instance-summary", 4, "medium", required: true),
                Card("management", "management-links", 8, "medium", required: true),
            ]),
        };

    private static readonly HashSet<string> ContentKinds = new(StringComparer.Ordinal)
    {
        "text", "image", "link", "clock", "spacer", "embed",
    };

    private static readonly HashSet<string> Heights = new(StringComparer.Ordinal)
    {
        "mini", "small", "medium", "large", "extra-large", "adaptive",
    };

    internal async Task<DashboardLayoutDto> GetAsync(Guid userId, string page, CancellationToken cancellationToken)
    {
        DashboardLayoutDto defaults = RequireDefaults(page);
        DashboardLayoutDto? stored = await store.FindDashboardLayoutAsync(userId, page, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return defaults;
        }

        DashboardLayoutDto upgraded = Normalize(page, stored.Cards, runtimeSettings.EmbedAllowedOrigins, rejectInvalidContent: false);
        if (stored.Version != upgraded.Version ||
            !string.Equals(JsonSerializer.Serialize(stored.Cards), JsonSerializer.Serialize(upgraded.Cards), StringComparison.Ordinal))
        {
            await store.SaveDashboardLayoutAsync(userId, upgraded, cancellationToken).ConfigureAwait(false);
        }

        return upgraded;
    }

    internal async Task<IReadOnlyList<DashboardLayoutDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        List<DashboardLayoutDto> result = [];
        foreach (string page in Defaults.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            result.Add(await GetAsync(userId, page, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    internal async Task<DashboardLayoutDto> SaveAsync(
        Guid userId,
        string page,
        UpdateDashboardLayoutRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DashboardLayoutDto normalized = Normalize(page, request.Cards, runtimeSettings.EmbedAllowedOrigins, rejectInvalidContent: true);
        await store.SaveDashboardLayoutAsync(userId, normalized, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    internal async Task<DashboardLayoutDto> ResetAsync(Guid userId, string page, CancellationToken cancellationToken)
    {
        DashboardLayoutDto defaults = RequireDefaults(page);
        await store.ResetDashboardLayoutAsync(userId, page, cancellationToken).ConfigureAwait(false);
        return defaults;
    }

    private static DashboardLayoutDto Normalize(
        string page,
        IReadOnlyList<DashboardCardLayoutDto> cards,
        IReadOnlyList<string> embedAllowedOrigins,
        bool rejectInvalidContent)
    {
        ArgumentNullException.ThrowIfNull(cards);
        DashboardLayoutDto defaults = RequireDefaults(page);
        if (cards.Count > 64)
        {
            throw Validation();
        }

        var defaultById = defaults.Cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<DashboardCardLayoutDto> normalized = [];
        foreach (DashboardCardLayoutDto card in cards)
        {
            if (!ids.Add(card.Id) || card.Id.Length is < 1 or > 64 || card.Width is < 1 or > 12 ||
                !Heights.Contains(card.Height) || card.Title?.Length > 128 || card.Description?.Length > 512 ||
                card.Settings is null || card.Settings.Count > 32 || card.Settings.Any(pair => pair.Key.Length is < 1 or > 64 || pair.Value?.Length > 2048))
            {
                throw Validation();
            }

            if (defaultById.TryGetValue(card.Id, out DashboardCardLayoutDto? requiredDefault))
            {
                if (!string.Equals(card.Kind, requiredDefault.Kind, StringComparison.Ordinal))
                {
                    throw Validation();
                }

                normalized.Add(card with { Required = requiredDefault.Required });
            }
            else
            {
                if (!ContentKinds.Contains(card.Kind) || card.Required)
                {
                    throw Validation();
                }

                if (!IsContentCardValid(card, embedAllowedOrigins))
                {
                    if (rejectInvalidContent)
                    {
                        throw Validation();
                    }

                    continue;
                }

                normalized.Add(card with { Required = false });
            }
        }

        foreach (DashboardCardLayoutDto defaultCard in defaults.Cards)
        {
            if (!ids.Contains(defaultCard.Id))
            {
                normalized.Add(defaultCard);
            }
        }

        return new DashboardLayoutDto(page, defaults.Version, normalized);
    }

    private static bool IsContentCardValid(DashboardCardLayoutDto card, IReadOnlyList<string> embedAllowedOrigins)
    {
        if (card.Kind != "embed")
        {
            return true;
        }

        string? value = card.Settings.GetValueOrDefault("value");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        string origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return embedAllowedOrigins.Any(allowed => string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static DashboardLayoutDto RequireDefaults(string page)
    {
        return Defaults.TryGetValue(page, out DashboardLayoutDto? value)
            ? value
            : throw new DomainException("not-found", "The layout page does not exist.");
    }

    private static DashboardCardLayoutDto Card(string id, string kind, int width, string height, bool required)
    {
        return new DashboardCardLayoutDto(
            id,
            kind,
            null,
            null,
            width,
            height,
            Hidden: false,
            required,
            new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    private static DomainException Validation()
    {
        return new DomainException("validation-failed", "The dashboard layout is invalid.");
    }
}
