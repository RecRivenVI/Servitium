using System;
using System.IO;

namespace Servitium.Agent.Files;

/// <summary>只探测当前是否可独占打开文件，不读取内容，不为后续写入提供保证。</summary>
internal static class FileOccupancyProbe
{
    internal static bool? IsLocked(string path)
    {
        try
        {
            using FileStream handle = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException exception)
        {
            int code = exception.HResult & 0xffff;
            return code is 32 or 33 ? true : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }
}
