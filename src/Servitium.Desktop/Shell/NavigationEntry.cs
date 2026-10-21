using System;
using System.Linq;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Distribution;

using Servitium.Desktop.Services;

namespace Servitium.Desktop.Shell;

internal sealed record NavigationEntry(string PageId, Guid? InstanceId, Guid? NodeId, Guid? ChannelId, UserDto? User, HmclReleaseSummaryDto? Release)
{
    internal static NavigationEntry Capture(string pageId, SessionService session)
    {
        return new(pageId, session.SelectedInstance?.Id, session.SelectedNode?.Id, session.SelectedChannel?.Id, session.SelectedUser, session.SelectedRelease);
    }

    internal void Restore(SessionService session)
    {
        session.SelectedInstance = session.Instances.FirstOrDefault(item => item.Id == InstanceId);
        session.SelectedNode = session.Nodes.FirstOrDefault(item => item.Id == NodeId);
        session.SelectedChannel = session.Channels.FirstOrDefault(item => item.Id == ChannelId);
        session.SelectedUser = User;
        session.SelectedRelease = Release;
    }
}
