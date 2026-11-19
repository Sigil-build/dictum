using System.Security.Cryptography;
using Dictum.Updates;
using Dictum.Updates.Attestation;

namespace Dictum.Core.Tests.Updates;

/// <summary>
/// The release workflow's gate before its draft: the release's attestation verifies with the verifier Dictum updates
/// itself with, and pins each package it updates from. A change on GitHub's side (actions/attest, Fulcio, Rekor) that
/// this verifier does not follow stops the release here, not every installed Dictum's update. Runs only when
/// DICTUM_CHECK_RELEASE names a folder with the release's files and DICTUM_RELEASE_VERSION its version.
/// </summary>
public sealed class ReleaseAttestationTests
{
    [Fact]
    public void EveryUpdatablePackageIsAttested()
    {
        var folder = Environment.GetEnvironmentVariable("DICTUM_CHECK_RELEASE");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set DICTUM_CHECK_RELEASE to a release's folder and DICTUM_RELEASE_VERSION to its version to run");
        var version = Assert.NotNull(ReleaseVersion.Parse(Environment.GetEnvironmentVariable("DICTUM_RELEASE_VERSION")));

        var result = new AttestationVerifier(SigstoreTrust.PublicGood).Verify(
            File.ReadAllBytes(Path.Combine(folder, ReleaseFiles.AttestationName(version))), version);

        Assert.True(result.Verified, $"{result.Failure}: {result.Detail}");
        foreach (var kind in new[] { InstallKind.Msi, InstallKind.AppImage, InstallKind.Tarball })
        {
            var name = ReleaseFiles.PackageName(kind, version)!;
            Assert.True(result.Digests.TryGetValue(name, out var digest), $"The attestation does not name {name}.");
            using var package = File.OpenRead(Path.Combine(folder, name));
            Assert.True(digest == Convert.ToHexStringLower(SHA256.HashData(package)), $"{name} does not have the SHA-256 the attestation names.");
        }
    }
}
