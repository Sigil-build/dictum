using System.Text.Json.Serialization;

namespace Dictum.Updates.Attestation;

// The parts of a Sigstore bundle v0.3 (and of what it carries) that the verifier reads. Protobuf's JSON writes 64-bit
// integers as strings, so those are strings here and parsed where they are used.

internal sealed class SigstoreBundle
{
    public string? MediaType { get; init; }

    public VerificationMaterial? VerificationMaterial { get; init; }

    public DsseEnvelope? DsseEnvelope { get; init; }
}

internal sealed class VerificationMaterial
{
    public RawBytes? Certificate { get; init; }

    /// <summary>The older form, a chain; v0.3 bundles from Fulcio carry the single leaf instead.</summary>
    public CertificateChain? X509CertificateChain { get; init; }

    public IReadOnlyList<TlogEntry>? TlogEntries { get; init; }
}

internal sealed class CertificateChain
{
    public IReadOnlyList<RawBytes>? Certificates { get; init; }
}

internal sealed class RawBytes
{
    [JsonPropertyName("rawBytes")]
    public string? Base64 { get; init; }
}

internal sealed class TlogEntry
{
    public string? LogIndex { get; init; }

    public LogId? LogId { get; init; }

    public KindVersion? KindVersion { get; init; }

    public string? IntegratedTime { get; init; }

    public InclusionPromise? InclusionPromise { get; init; }

    public string? CanonicalizedBody { get; init; }
}

internal sealed class LogId
{
    public string? KeyId { get; init; }
}

internal sealed class KindVersion
{
    public string? Kind { get; init; }

    public string? Version { get; init; }
}

internal sealed class InclusionPromise
{
    public string? SignedEntryTimestamp { get; init; }
}

internal sealed class DsseEnvelope
{
    public string? Payload { get; init; }

    public string? PayloadType { get; init; }

    public IReadOnlyList<DsseSignature>? Signatures { get; init; }
}

internal sealed class DsseSignature
{
    public string? Sig { get; init; }
}

/// <summary>Rekor's <c>dsse</c> 0.0.1 entry, as its <c>canonicalizedBody</c> holds it.</summary>
internal sealed class RekorDsseBody
{
    public string? ApiVersion { get; init; }

    public string? Kind { get; init; }

    public RekorDsseSpec? Spec { get; init; }
}

internal sealed class RekorDsseSpec
{
    public RekorHash? PayloadHash { get; init; }

    public IReadOnlyList<RekorSignature>? Signatures { get; init; }
}

internal sealed class RekorHash
{
    public string? Algorithm { get; init; }

    public string? Value { get; init; }
}

internal sealed class RekorSignature
{
    public string? Signature { get; init; }

    /// <summary>Base64 of the signing certificate's PEM.</summary>
    public string? Verifier { get; init; }
}

/// <summary>An in-toto Statement v1.</summary>
internal sealed class InTotoStatement
{
    [JsonPropertyName("_type")]
    public string? Type { get; init; }

    public IReadOnlyList<InTotoSubject>? Subject { get; init; }

    public string? PredicateType { get; init; }
}

internal sealed class InTotoSubject
{
    public string? Name { get; init; }

    public Dictionary<string, string>? Digest { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SigstoreBundle))]
[JsonSerializable(typeof(RekorDsseBody))]
[JsonSerializable(typeof(InTotoStatement))]
internal sealed partial class AttestationJsonContext : JsonSerializerContext;
