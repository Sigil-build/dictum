namespace Dictum.Updates.Attestation;

/// <summary>Why an attestation was refused: the first of the verifier's checks that failed, in their order.</summary>
public enum AttestationFailure
{
    None,

    /// <summary>Not a JSON bundle, or a field that cannot be decoded.</summary>
    Malformed,

    /// <summary>Another bundle version, or not exactly one certificate, log entry and signature.</summary>
    Shape,

    /// <summary>The certificate does not chain to Sigstore's built-in root at the time the log recorded it.</summary>
    Chain,

    /// <summary>The certificate names another repository, workflow, tag or token issuer.</summary>
    Identity,

    /// <summary>The envelope's signature does not verify with the certificate's key.</summary>
    Signature,

    /// <summary>The log's signed promise does not verify, or its entry is not about this envelope.</summary>
    TransparencyLog,

    /// <summary>The signed statement is not an in-toto SLSA provenance, or names a file twice.</summary>
    Statement,
}

/// <summary>What the verifier decided, and on success the release's files with their SHA-256.</summary>
/// <param name="Detail">Why it failed, in English, for the log.</param>
/// <param name="Digests">Each attested file's name and lowercase hex SHA-256; empty unless verified.</param>
public sealed record AttestationResult(AttestationFailure Failure, string? Detail, IReadOnlyDictionary<string, string> Digests)
{
    public bool Verified => Failure == AttestationFailure.None;

    internal static AttestationResult Refused(AttestationFailure failure, string detail) =>
        new(failure, detail, new Dictionary<string, string>());
}
