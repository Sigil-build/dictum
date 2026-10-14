using Dictum.Setup;

namespace Dictum.Integration.Tests;

/// <summary>
/// Every catalog URL answers anonymously with the catalog's size, so a file that moves or turns private fails CI
/// before it fails a user's 'dictum init'. Enabled with DICTUM_CHECK_CATALOG=1 (needs the network).
/// </summary>
public class CatalogUrlTests
{
    [Fact]
    public async Task EveryCatalogUrlIsReachableWithTheRightSize()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_CHECK_CATALOG") == "1", "Set DICTUM_CHECK_CATALOG=1 to run");
        using var http = FileDownloader.CreateHttpClient("dictum-ci");
        var failures = new List<string>();
        foreach (var entry in Catalog.BuiltIn.Entries)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, entry.Url);
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"{entry.Id}: {(int)response.StatusCode} from {entry.Url}");
            }
            else if (response.Content.Headers.ContentLength != entry.Size)
            {
                failures.Add($"{entry.Id}: size {response.Content.Headers.ContentLength}, catalog says {entry.Size}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
