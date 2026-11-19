namespace Dictum.Updates;

/// <summary>Why an update stopped before anything was installed.</summary>
public enum UpdateFailure
{
    /// <summary>The release has no package or attestation for this installation, or it cannot be installed from here.</summary>
    NoPackage,

    /// <summary>The network or GitHub failed; the partial file stays for the next try.</summary>
    Download,

    /// <summary>The attestation or the package's hash did not verify; the package was deleted.</summary>
    NotVerified,

    /// <summary>Not enough free space for the package.</summary>
    NoRoom,
}

/// <summary>An update that cannot go on; <see cref="Exception.Message"/> says why, in English, for the log.</summary>
public sealed class UpdateException(UpdateFailure failure, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public UpdateFailure Failure { get; } = failure;
}
