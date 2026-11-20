using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Dictum.Updates.Attestation;

/// <summary>
/// Checks a release's <c>dictum-X.Y.Z.intoto.jsonl</c>: a Sigstore bundle v0.3 as <c>actions/attest</c> writes it, with a
/// DSSE envelope, a Fulcio certificate and a Rekor v1 entry carrying a signed entry timestamp. It proves the release's
/// files were built by <c>release.yml</c> of Sigil-build/dictum from the tag <c>vX.Y.Z</c>. Only that shape is accepted;
/// anything else is refused, never guessed at. Not checked: the Rekor inclusion proof (the signed entry timestamp is the
/// log's own promise), the certificate's SCTs, and the body's <c>envelopeHash</c>, which depends on JSON serialization.
/// </summary>
public sealed class AttestationVerifier(SigstoreTrust trust)
{
    public const string BundleMediaType = "application/vnd.dev.sigstore.bundle.v0.3+json";
    public const string PayloadType = "application/vnd.in-toto+json";
    public const string StatementType = "https://in-toto.io/Statement/v1";
    public const string PredicateType = "https://slsa.dev/provenance/v1";
    public const string Issuer = "https://token.actions.githubusercontent.com";
    public const string SourceRepository = "https://github.com/Sigil-build/dictum";
    public const string Workflow = "https://github.com/Sigil-build/dictum/.github/workflows/release.yml";

    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string SubjectAlternativeName = "2.5.29.17";
    private const string IssuerV1 = "1.3.6.1.4.1.57264.1.1";
    private const string IssuerV2 = "1.3.6.1.4.1.57264.1.8";
    private const string SourceRepositoryUri = "1.3.6.1.4.1.57264.1.12";
    private const string SourceRepositoryRef = "1.3.6.1.4.1.57264.1.14";

    /// <summary>Never throws for a bad bundle: the result says which check refused it.</summary>
    public AttestationResult Verify(ReadOnlySpan<byte> bundle, ReleaseVersion version)
    {
        SigstoreBundle? parsed;
        try
        {
            parsed = bundle.IsEmpty ? null : JsonSerializer.Deserialize(bundle, AttestationJsonContext.Default.SigstoreBundle);
        }
        catch (JsonException ex)
        {
            return AttestationResult.Refused(AttestationFailure.Malformed, $"not a JSON bundle: {ex.Message}");
        }

        if (parsed is null)
        {
            return AttestationResult.Refused(AttestationFailure.Malformed, "empty");
        }

        try
        {
            return Verify(parsed, version);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or AsnContentException or OverflowException
            or ArgumentException)
        {
            return AttestationResult.Refused(AttestationFailure.Malformed, $"a field cannot be decoded: {ex.Message}");
        }
    }

    private AttestationResult Verify(SigstoreBundle bundle, ReleaseVersion version)
    {
        // 1. Shape: exactly the form actions/attest writes.
        if (Shape(bundle) is { } shape)
        {
            return AttestationResult.Refused(AttestationFailure.Shape, shape);
        }

        var material = bundle.VerificationMaterial!;
        var entry = material.TlogEntries![0];
        var envelope = bundle.DsseEnvelope!;
        var integratedTime = long.Parse(entry.IntegratedTime!, NumberStyles.None, CultureInfo.InvariantCulture);
        var logIndex = long.Parse(entry.LogIndex!, NumberStyles.None, CultureInfo.InvariantCulture);
        var at = DateTimeOffset.FromUnixTimeSeconds(integratedTime);
        using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(material.Certificate!.Base64!));
        var payload = Convert.FromBase64String(envelope.Payload!);
        var signature = Convert.FromBase64String(envelope.Signatures![0].Sig!);

        // 2. The chain to Sigstore's root, at the time the log recorded the entry: the leaf lives 10 minutes.
        if (Chain(leaf, at) is { } chain)
        {
            return AttestationResult.Refused(AttestationFailure.Chain, chain);
        }

        // 3. Who signed: release.yml of this repository, at this release's tag, through GitHub's token issuer.
        if (Identity(leaf, version) is { } identity)
        {
            return AttestationResult.Refused(AttestationFailure.Identity, identity);
        }

        // 4. The envelope's signature, with the leaf's key.
        using (var key = leaf.GetECDsaPublicKey())
        {
            if (key is null || !key.VerifyData(Pae(envelope.PayloadType!, payload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return AttestationResult.Refused(AttestationFailure.Signature, "the envelope's signature does not verify with the certificate");
            }
        }

        // 5. The transparency log: Rekor's promise, about this payload, signature and certificate.
        if (TransparencyLog(entry, integratedTime, logIndex, leaf, payload, signature) is { } log)
        {
            return AttestationResult.Refused(AttestationFailure.TransparencyLog, log);
        }

        // 6. The statement and its files.
        return Statement(payload);
    }

    private static string? Shape(SigstoreBundle bundle)
    {
        if (bundle.MediaType != BundleMediaType)
        {
            return $"media type {bundle.MediaType ?? "(none)"}";
        }

        if (bundle.VerificationMaterial is not { Certificate.Base64: { Length: > 0 }, X509CertificateChain: null } material)
        {
            return "not exactly one certificate";
        }

        if (material.TlogEntries is not [{ } entry])
        {
            return "not exactly one transparency log entry";
        }

        if (entry is not
            {
                KindVersion: { Kind: "dsse", Version: "0.0.1" },
                LogId.KeyId: { Length: > 0 },
                LogIndex: { Length: > 0 },
                IntegratedTime: { Length: > 0 },
                InclusionPromise.SignedEntryTimestamp: { Length: > 0 },
                CanonicalizedBody: { Length: > 0 },
            })
        {
            return "the log entry is not a dsse 0.0.1 entry with a signed entry timestamp";
        }

        if (bundle.DsseEnvelope is not { Payload: { Length: > 0 }, PayloadType: PayloadType, Signatures: [{ Sig: { Length: > 0 } }] })
        {
            return "not one in-toto envelope with exactly one signature";
        }

        return null;
    }

    private string? Chain(X509Certificate2 leaf, DateTimeOffset at)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trust.Root);
        chain.ChainPolicy.ExtraStore.Add(trust.Intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = at.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigning));
        if (!chain.Build(leaf))
        {
            var problems = string.Join(", ", chain.ChainStatus.Select(s => s.Status));
            return $"no chain to Sigstore's root at {at:u}: {problems}";
        }

        return chain.ChainElements is [_, { Certificate: var intermediate }, { Certificate: var root }]
               && intermediate.RawDataMemory.Span.SequenceEqual(trust.Intermediate.RawDataMemory.Span)
               && root.RawDataMemory.Span.SequenceEqual(trust.Root.RawDataMemory.Span)
            ? null
            : "the chain is not leaf, Sigstore's intermediate, Sigstore's root";
    }

    private static string? Identity(X509Certificate2 leaf, ReleaseVersion version)
    {
        var tag = $"refs/tags/v{version}";
        var expected = $"{Workflow}@{tag}";
        var san = leaf.Extensions[SubjectAlternativeName] is { } extension ? Uris(extension.RawData) : [];
        if (san is not [var uri] || uri != expected)
        {
            return $"the certificate is for {string.Join(", ", san)}, not {expected}";
        }

        var issuer = Utf8String(leaf, IssuerV2) ?? Raw(leaf, IssuerV1);
        if (issuer != Issuer)
        {
            return $"the token issuer is {issuer ?? "(none)"}";
        }

        var repository = Utf8String(leaf, SourceRepositoryUri);
        if (repository != SourceRepository)
        {
            return $"the source repository is {repository ?? "(none)"}";
        }

        var reference = Utf8String(leaf, SourceRepositoryRef);
        return reference == tag ? null : $"the source ref is {reference ?? "(none)"}, not {tag}";
    }

    /// <summary>The URIs among a SubjectAltName's GeneralNames (<c>[6] IA5String</c>); other kinds of name count as names too.</summary>
    private static List<string> Uris(byte[] subjectAltName)
    {
        var names = new List<string>();
        var reader = new AsnReader(subjectAltName, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
        while (sequence.HasData)
        {
            names.Add(sequence.PeekTag() == uri
                ? sequence.ReadCharacterString(UniversalTagNumber.IA5String, uri)
                : $"(another kind of name: {Convert.ToHexString(sequence.ReadEncodedValue().Span)})");
        }

        return names;
    }

    /// <summary>A Fulcio extension of the v2 kind: a DER UTF8String.</summary>
    private static string? Utf8String(X509Certificate2 leaf, string oid)
    {
        if (leaf.Extensions[oid] is not { } extension)
        {
            return null;
        }

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var value = reader.ReadCharacterString(UniversalTagNumber.UTF8String);
        reader.ThrowIfNotEmpty();
        return value;
    }

    /// <summary>A Fulcio extension of the v1 kind: the value's bytes as they are.</summary>
    private static string? Raw(X509Certificate2 leaf, string oid) =>
        leaf.Extensions[oid] is { } extension ? Encoding.UTF8.GetString(extension.RawData) : null;

    /// <summary>DSSE's pre-authentication encoding: <c>DSSEv1 SP len(type) SP type SP len(body) SP body</c>.</summary>
    private static byte[] Pae(string type, byte[] body) =>
        [.. Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"DSSEv1 {Encoding.UTF8.GetByteCount(type)} {type} {body.Length} ")), .. body];

    private string? TransparencyLog(TlogEntry entry, long integratedTime, long logIndex, X509Certificate2 leaf, byte[] payload, byte[] signature)
    {
        var logId = Convert.FromBase64String(entry.LogId!.KeyId!);
        if (!logId.AsSpan().SequenceEqual(trust.RekorLogId))
        {
            return $"another log: {entry.LogId.KeyId}";
        }

        // Rekor signs the entry's canonical JSON: keys sorted, no spaces.
        var signed = string.Create(CultureInfo.InvariantCulture,
            $$"""{"body":"{{entry.CanonicalizedBody}}","integratedTime":{{integratedTime}},"logID":"{{Convert.ToHexStringLower(logId)}}","logIndex":{{logIndex}}}""");
        var promise = Convert.FromBase64String(entry.InclusionPromise!.SignedEntryTimestamp!);
        if (!trust.RekorKey.VerifyData(Encoding.UTF8.GetBytes(signed), promise, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            return "Rekor's signed entry timestamp does not verify";
        }

        if (integratedTime < leaf.NotBefore.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds
            || integratedTime > leaf.NotAfter.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds)
        {
            return "the log recorded the entry outside the certificate's validity";
        }

        var body = JsonSerializer.Deserialize(Convert.FromBase64String(entry.CanonicalizedBody!), AttestationJsonContext.Default.RekorDsseBody);
        if (body is not { ApiVersion: "0.0.1", Kind: "dsse", Spec: { PayloadHash: { Algorithm: "sha256", Value: { } payloadHash }, Signatures: [{ Signature: { } bodySignature, Verifier: { } verifier }] } })
        {
            return "the log's entry is not a dsse entry with one signature";
        }

        if (payloadHash != Convert.ToHexStringLower(SHA256.HashData(payload)))
        {
            return "the log's entry is about another payload";
        }

        if (!Convert.FromBase64String(bodySignature).AsSpan().SequenceEqual(signature))
        {
            return "the log's entry has another signature";
        }

        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(verifier));
        return PemEncoding.TryFind(pem, out var fields) && pem[fields.Label] == "CERTIFICATE"
               && Convert.FromBase64String(pem[fields.Base64Data]).AsSpan().SequenceEqual(leaf.RawData)
            ? null
            : "the log's entry names another certificate";
    }

    private static AttestationResult Statement(byte[] payload)
    {
        var statement = JsonSerializer.Deserialize(payload, AttestationJsonContext.Default.InTotoStatement);
        if (statement is not { Type: StatementType, PredicateType: PredicateType, Subject: { Count: > 0 } subjects })
        {
            return AttestationResult.Refused(AttestationFailure.Statement,
                $"not an in-toto statement of SLSA provenance v1: {statement?.Type} {statement?.PredicateType}");
        }

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            if (subject is not { Name: { Length: > 0 } name, Digest: { } digest } || !digest.TryGetValue("sha256", out var sha) || !IsSha256(sha))
            {
                return AttestationResult.Refused(AttestationFailure.Statement, $"a subject without a SHA-256: {subject.Name}");
            }

            if (!digests.TryAdd(name, sha))
            {
                return AttestationResult.Refused(AttestationFailure.Statement, $"two subjects named {name}");
            }
        }

        return new AttestationResult(AttestationFailure.None, null, digests);
    }

    private static bool IsSha256(string hex) => hex.Length == 64 && hex.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
