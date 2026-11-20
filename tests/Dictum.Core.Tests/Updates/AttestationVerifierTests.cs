using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Attestation;

namespace Dictum.Core.Tests.Updates;

/// <summary>
/// The release's provenance attestation: the real one of 0.6.0 against Sigstore's real roots, and minted ones against
/// <see cref="TestSigstore"/>. Each broken bundle must be refused for its own reason.
/// </summary>
public sealed class AttestationVerifierTests : IDisposable
{
    private static readonly ReleaseVersion V060 = new(0, 6, 0);
    private static readonly ReleaseVersion V071 = new(0, 7, 1);

    private readonly TestSigstore _sigstore = new();

    public void Dispose() => _sigstore.Dispose();

    private static byte[] Real060() =>
        File.ReadAllBytes(Path.Combine(Repository.Root, "tests", "Dictum.Core.Tests", "Updates", "Fixtures", "dictum-0.6.0.intoto.jsonl"));

    private static AttestationResult VerifyReal(byte[] bundle, ReleaseVersion? version = null) =>
        new AttestationVerifier(SigstoreTrust.PublicGood).Verify(bundle, version ?? V060);

    private AttestationResult VerifyMinted(Action<MintOptions>? change = null) =>
        new AttestationVerifier(_sigstore.Trust).Verify(_sigstore.Mint(change), V071);

    /// <summary>The bundle with one change made to its JSON.</summary>
    private static byte[] Changed(byte[] bundle, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(bundle)!.AsObject();
        change(json);
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private static JsonObject Entry(JsonObject bundle) => bundle["verificationMaterial"]!["tlogEntries"]![0]!.AsObject();

    [Fact]
    public void TheReal060AttestationVerifies()
    {
        var result = VerifyReal(Real060());

        Assert.Equal(AttestationFailure.None, result.Failure);
        Assert.True(result.Verified);
        Assert.Equal("47ea916cd45465f5cbd86a86344d4641839705194289683be68f64dc9f2bf6cf", result.Digests["dictum-0.6.0-win-x64.msi"]);
        Assert.Equal("b0b1f3fc8ccaef451fe6ffe8c3773143915ba966689d8540ab02dfd04c890c74", result.Digests["dictum-0.6.0-linux-x64.AppImage"]);
        Assert.Equal("a4b5859e6c01931d47e8b90c8c8289241a99fd13d1a5d05e46f07d9fd310f514", result.Digests["dictum-0.6.0-linux-x64.tar.gz"]);
    }

    [Fact]
    public void TheReal060AttestationIsNotOneFor061()
    {
        var result = VerifyReal(Real060(), new ReleaseVersion(0, 6, 1));

        Assert.Equal(AttestationFailure.Identity, result.Failure);
        Assert.Empty(result.Digests);
    }

    [Fact]
    public void AChangedPayloadIsRefused()
    {
        var bundle = Changed(Real060(), json =>
        {
            var envelope = json["dsseEnvelope"]!.AsObject();
            var payload = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
            var digest = Encoding.UTF8.GetBytes("47ea916c");
            var at = payload.AsSpan().IndexOf(digest);
            payload[at] = (byte)'5';
            envelope["payload"] = Convert.ToBase64String(payload);
        });

        Assert.Equal(AttestationFailure.Signature, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void AChangedSignedEntryTimestampIsRefused()
    {
        var bundle = Changed(Real060(), json =>
        {
            var promise = Entry(json)["inclusionPromise"]!.AsObject();
            var set = Convert.FromBase64String(promise["signedEntryTimestamp"]!.GetValue<string>());
            set[10] ^= 0x01;
            promise["signedEntryTimestamp"] = Convert.ToBase64String(set);
        });

        Assert.Equal(AttestationFailure.TransparencyLog, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void AnotherLogIdIsRefused()
    {
        var bundle = Changed(Real060(), json => Entry(json)["logId"]!["keyId"] = Convert.ToBase64String(new byte[32]));

        Assert.Equal(AttestationFailure.TransparencyLog, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void AnotherBundleMediaTypeIsRefused()
    {
        var bundle = Changed(Real060(), json => json["mediaType"] = "application/vnd.dev.sigstore.bundle.v0.2+json");

        Assert.Equal(AttestationFailure.Shape, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void TwoTlogEntriesAreRefused()
    {
        var bundle = Changed(Real060(), json =>
        {
            var entries = json["verificationMaterial"]!["tlogEntries"]!.AsArray();
            entries.Add(entries[0]!.DeepClone());
        });

        Assert.Equal(AttestationFailure.Shape, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void TwoDsseSignaturesAreRefused()
    {
        var bundle = Changed(Real060(), json =>
        {
            var signatures = json["dsseEnvelope"]!["signatures"]!.AsArray();
            signatures.Add(signatures[0]!.DeepClone());
        });

        Assert.Equal(AttestationFailure.Shape, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void NotJsonIsMalformed() => Assert.Equal(AttestationFailure.Malformed, VerifyReal("<html>Not found</html>"u8.ToArray()).Failure);

    [Fact]
    public void EmptyIsMalformed() => Assert.Equal(AttestationFailure.Malformed, VerifyReal([]).Failure);

    [Fact]
    public void ACertificateThatIsNotOneIsMalformed()
    {
        var bundle = Changed(Real060(), json => json["verificationMaterial"]!["certificate"]!["rawBytes"] = Convert.ToBase64String("not a certificate"u8));

        Assert.Equal(AttestationFailure.Malformed, VerifyReal(bundle).Failure);
    }

    // A long, but past the last second DateTimeOffset holds.
    [Fact]
    public void ALogTimeOutOfRangeIsMalformed()
    {
        var bundle = Changed(Real060(), json => json["verificationMaterial"]!["tlogEntries"]![0]!["integratedTime"] = "99999999999999999");

        Assert.Equal(AttestationFailure.Malformed, VerifyReal(bundle).Failure);
    }

    [Fact]
    public void AMintedBundleVerifies()
    {
        var result = VerifyMinted();

        Assert.Equal(AttestationFailure.None, result.Failure);
        Assert.Equal(new string('a', 64), result.Digests["dictum-0.7.1-win-x64.msi"]);
    }

    [Fact]
    public void AnIssuerOnlyInTheOldExtensionIsAccepted() =>
        Assert.Equal(AttestationFailure.None, VerifyMinted(o => o.LegacyIssuerOnly = true).Failure);

    [Fact]
    public void AnotherRepositoryIsRefused() => Assert.Equal(AttestationFailure.Identity, VerifyMinted(o => o.Repository = "Sigil-build/dictum-fork").Failure);

    [Fact]
    public void AnotherWorkflowIsRefused() => Assert.Equal(AttestationFailure.Identity, VerifyMinted(o => o.Workflow = ".github/workflows/ci.yml").Failure);

    [Fact]
    public void AnotherIssuerIsRefused() => Assert.Equal(AttestationFailure.Identity, VerifyMinted(o => o.Issuer = "https://accounts.google.com").Failure);

    [Fact]
    public void ABranchInsteadOfATagIsRefused() => Assert.Equal(AttestationFailure.Identity, VerifyMinted(o => o.Ref = "refs/heads/v0.7.1").Failure);

    [Fact]
    public void ALeafFromAnotherRootIsRefused()
    {
        using var other = new TestSigstore();

        Assert.Equal(AttestationFailure.Chain, new AttestationVerifier(_sigstore.Trust).Verify(other.Mint(), V071).Failure);
    }

    [Fact]
    public void ALeafExpiredAtIntegratedTimeIsRefused() => Assert.Equal(AttestationFailure.Chain, VerifyMinted(o => o.IntegratedAfter = 11 * 60).Failure);

    [Fact]
    public void ABodyNamingAnotherCertificateIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var another = _sigstore.Leaf(new MintOptions(), key);

        Assert.Equal(AttestationFailure.TransparencyLog, VerifyMinted(o => o.BodyCertificate = another).Failure);
    }

    // The log's time is what the chain is checked at, so a bundle that moves it has to fail the log's signature.
    [Fact]
    public void AnIntegratedTimeChangedAfterSigningIsRefused()
    {
        var bundle = Changed(_sigstore.Mint(), json =>
        {
            var entry = Entry(json);
            entry["integratedTime"] = (long.Parse(entry["integratedTime"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) + 60)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        Assert.Equal(AttestationFailure.TransparencyLog, new AttestationVerifier(_sigstore.Trust).Verify(bundle, V071).Failure);
    }

    [Fact]
    public void AnotherPredicateTypeIsRefused() =>
        Assert.Equal(AttestationFailure.Statement, VerifyMinted(o => o.PredicateType = "https://slsa.dev/provenance/v0.2").Failure);

    [Fact]
    public void AnotherStatementTypeIsRefused() =>
        Assert.Equal(AttestationFailure.Statement, VerifyMinted(o => o.StatementType = "https://in-toto.io/Statement/v0.1").Failure);

    [Fact]
    public void TwoSubjectsWithTheAssetsNameAreRefused()
    {
        var result = VerifyMinted(o => o.Subjects.Add(("dictum-0.7.1-win-x64.msi", new string('c', 64))));

        Assert.Equal(AttestationFailure.Statement, result.Failure);
        Assert.Empty(result.Digests);
    }

    [Fact]
    public void ADigestThatIsNotSha256HexIsRefused() =>
        Assert.Equal(AttestationFailure.Statement, VerifyMinted(o => o.Subjects.Add(("x", "ABC"))).Failure);
}
