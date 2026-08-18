using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Contracts.Common;
using Servitium.Domain.Identity;

using Xunit;

namespace Servitium.Application.Tests;

public sealed class AuthorizationPaginationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-190")]
    public void 授权判定_完整覆盖角色动作资源真值表()
    {
        var service = new AuthorizationService();
        var grantedId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        var otherId = Guid.Parse("10000000-0000-4000-8000-000000000002");
        ResourceKind[] resources = Enum.GetValues<ResourceKind>();
        ServitiumAction[] actions = Enum.GetValues<ServitiumAction>();

        foreach (ServitiumAction action in actions)
        {
            foreach (ResourceKind resource in resources)
            {
                Assert.True(service.Authorize(Principal(Role.Owner, grantedId), new AuthorizationResource(resource, grantedId), action).Allowed);

                AuthorizationDecision administrator = service.Authorize(
                    Principal(Role.Administrator, grantedId),
                    new AuthorizationResource(resource, grantedId),
                    action);
                Assert.Equal(action is not (ServitiumAction.ModifyOwner or ServitiumAction.TransferOwner), administrator.Allowed);

                AuthorizationDecision disabled = service.Authorize(
                    Principal(Role.Disabled, grantedId),
                    new AuthorizationResource(resource, grantedId),
                    action);
                Assert.False(disabled.Allowed);
                Assert.Equal("account-disabled", disabled.Code);
            }
        }

        ServitiumAction[] userActions =
        [
            ServitiumAction.View,
            ServitiumAction.ControlInstance,
            ServitiumAction.UseConsole,
            ServitiumAction.ManageFiles,
            ServitiumAction.ManageConfigurations,
            ServitiumAction.ManageMods,
            ServitiumAction.ManageSchedules,
            ServitiumAction.EditSafeInstanceSettings,
        ];
        foreach (ServitiumAction action in actions)
        {
            bool expected = userActions.Contains(action);
            Assert.Equal(
                expected,
                service.Authorize(Principal(Role.User, grantedId), new AuthorizationResource(ResourceKind.Instance, grantedId), action).Allowed);
            Assert.False(service.Authorize(Principal(Role.User, grantedId), new AuthorizationResource(ResourceKind.Instance, otherId), action).Allowed);
            Assert.False(service.Authorize(Principal(Role.User, grantedId), new AuthorizationResource(ResourceKind.Global), action).Allowed);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-200")]
    public void 分页游标_往返且数据插入后不重复不遗漏()
    {
        var original = new List<Item>
        {
            new(Guid.Parse("10000000-0000-4000-8000-000000000001"), "a"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000002"), "b"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000003"), "c"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000004"), "d"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000005"), "e"),
        };

        CursorPage<Item> first = CursorPaginator.Page(original, item => item.Key, item => item.Id, null, 2);
        Assert.Equal(["a", "b"], first.Items.Select(item => item.Key));
        Assert.NotNull(first.NextCursor);
        CursorPosition position = CursorCodec.Decode(first.NextCursor);
        Assert.Equal("b", position.SortKey);

        var changed = original.Append(new Item(Guid.Parse("10000000-0000-4000-8000-000000000006"), "bb")).ToList();
        CursorPage<Item> second = CursorPaginator.Page(changed, item => item.Key, item => item.Id, first.NextCursor, 2);
        CursorPage<Item> third = CursorPaginator.Page(changed, item => item.Key, item => item.Id, second.NextCursor, 2);
        string[] observed = [.. first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Key)];

        Assert.Equal(["a", "b", "bb", "c", "d", "e"], observed);
        Assert.Equal(observed.Length, observed.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(third.NextCursor);
        _ = Assert.Throws<FormatException>(() => CursorCodec.Decode("not-a-cursor"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CursorPaginator.Page(original, item => item.Key, item => item.Id, null, 201));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-210")]
    public void 字符串复合游标_稳定分页并拒绝伪造值()
    {
        Item[] source =
        [
            new(Guid.Parse("10000000-0000-4000-8000-000000000001"), "same"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000002"), "same"),
            new(Guid.Parse("10000000-0000-4000-8000-000000000003"), "tail"),
        ];

        CursorPage<Item> first = CursorPaginator.Page(source, item => item.Key, item => item.Id.ToString("D"), null, 1);
        CursorPage<Item> second = CursorPaginator.Page(source, item => item.Key, item => item.Id.ToString("D"), first.NextCursor, 1);
        CursorPage<Item> third = CursorPaginator.Page(source, item => item.Key, item => item.Id.ToString("D"), second.NextCursor, 1);

        Assert.Equal(source.Select(item => item.Id), first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id));
        Assert.Null(third.NextCursor);
        _ = Assert.Throws<FormatException>(() => CursorPaginator.Page(source, item => item.Key, item => item.Id.ToString("D"), "not-a-cursor", 1));
        string sequenceCursor = SequenceCursorCodec.Encode(42);
        Assert.Equal(42, SequenceCursorCodec.Decode(sequenceCursor));
        _ = Assert.Throws<FormatException>(() => SequenceCursorCodec.Decode("NDI"));
    }

    private static AuthorizationPrincipal Principal(Role role, Guid grantedId)
    {
        return new AuthorizationPrincipal(Guid.NewGuid(), role, new HashSet<Guid> { grantedId });
    }

    private sealed record Item(Guid Id, string Key);
}
