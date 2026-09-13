using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio and VS Code share the sessions folder, and both the list and the search read ALL of its
/// files. On Windows, a file opened without FileShare.Delete cannot be deleted: a read in progress made
/// deleting a session fail ("being used by another process") — which is what made AutoSaveSlotTests flaky.
/// Every session read goes through a single reader, which must not block deletion.
/// </summary>
/// <remarks>
/// ⚠ Delete sharing does NOT allow replacing the file by a rename while a handle is open (measured:
/// UnauthorizedAccessException); the atomic replacement of a save relies on AtomicFile's retries, which
/// outlast an ordinary read.
/// </remarks>
public class SessionFileSharingTests
{
    [Fact]
    public void AnOpenSessionReader_DoesNotBlockDeletingTheSession()
    {
        var dir  = Directory.CreateTempSubdirectory("session-sharing-").FullName;
        var path = Path.Combine(dir, "s.json");
        File.WriteAllText(path, "{}");
        try
        {
            using (ConversationStore.OpenSessionForRead(path))
            {
                File.Delete(path);
            }
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
