using System;
using System.Threading.Tasks;

namespace Servitium.Desktop.Shell;

internal interface IServerConnectionNavigator
{
    Task SwitchConnectionAsync(Uri address);
}
