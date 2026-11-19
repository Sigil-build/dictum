using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Dictum.Updates.Attestation;

namespace Dictum.Core.Tests.Updates;

/// <summary>What a minted bundle says; each test changes one thing from what release.yml would write.</summary>
public sealed class MintOptions
{
    public string Version { get; set; } = "0.7.1";

    public string Repository { get; set; } = "Sigil-build/dictum";

    public string Workflow { get; set; } = ".github/workflows/release.yml";

    public string? Ref { get; set; }

    public string Issuer { get; set; } = "https://token.actions.githubusercontent.com";

    /// <summary>Only the old raw-bytes issuer extension (.1.1), as Fulcio wrote before .1.8.</summary>
    public bool LegacyIssuerOnly { get; set; }

    public string MediaType { get; set; } = "application/vnd.dev.sigstore.bundle.v0.3+json";

    public string StatementType { get; set; } = "https://in-toto.io/Statement/v1";

    public string PredicateType { get; set; } = "https://slsa.dev/provenance/v1";

    public List<(string Name, string Sha256)> Subjects { get; } = [];

    /// <summary>Seconds from the leaf's start to the log's time; past its 10 minutes, the leaf has expired.</summary>
    public int IntegratedAfter { get; set; } = 30;

    /// <summary>A certificate the log's body names instead of the leaf.</summary>
    public X509Certificate2? BodyCertificate { get; set; }
}

/// <summary>
/// A Sigstore of its own for tests: a root and an intermediate CA, a Rekor key, and bundles shaped like the ones
/// <c>actions/attest</c> writes, signed by them. <see cref="Trust"/> is what the verifier gets instead of the real roots.
/// </summary>
public sealed class TestSigstore : IDisposable
{
    public static readonly DateTimeOffset LeafStart = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _rekorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;

    public TestSigstore()
    {
        var root = new CertificateRequest("CN=test-sigstore, O=dictum.test", _rootKey, HashAlgorithmName.SHA384);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        root.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(root.PublicKey, false));
        _root = root.CreateSelfSigned(LeafStart.AddYears(-1), LeafStart.AddYears(5));

        var intermediate = new CertificateRequest("CN=test-sigstore-intermediate, O=dictum.test", _intermediateKey, HashAlgorithmName.SHA384);
        intermediate.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediate.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediate.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediate.PublicKey, false));
        intermediate.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_root, true, false));
        intermediate.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CodeSigning)], false));
        using var intermediatePublic = intermediate.Create(_root, LeafStart.AddYears(-1), LeafStart.AddYears(4), [1, 2]);
        _intermediate = intermediatePublic.CopyWithPrivateKey(_intermediateKey);

        Trust = new SigstoreTrust(
            X509CertificateLoader.LoadCertificate(_root.RawData),
            X509CertificateLoader.LoadCertificate(_intermediate.RawData),
            ECDsa.Create(_rekorKey.ExportParameters(false)));
    }

    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";

    public SigstoreTrust Trust { get; }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        _rootKey.Dispose();
        _intermediateKey.Dispose();
        _rekorKey.Dispose();
    }

    /// <summary>A leaf like Fulcio's: 10 minutes long, the workflow's identity in its SAN and extensions.</summary>
    public X509Certificate2 Leaf(MintOptions options, ECDsa key)
    {
        var tag = options.Ref ?? $"refs/tags/v{options.Version}";
        var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"https://github.com/{options.Repository}/{options.Workflow}@{tag}"));
        request.CertificateExtensions.Add(san.Build(critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CodeSigning)], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_intermediate, true, false));
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.1", Encoding.UTF8.GetBytes(options.Issuer), false));
        if (!options.LegacyIssuerOnly)
        {
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.8", Utf8(options.Issuer), false));
        }

        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.12", Utf8($"https://github.com/{options.Repository}"), false));
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.14", Utf8(tag), false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = request.Create(_intermediate, LeafStart, LeafStart.AddMinutes(10), serial);
        return X509CertificateLoader.LoadCertificate(leaf.RawData);
    }

    public byte[] Mint(Action<MintOptions>? change = null)
    {
        var options = new MintOptions();
        options.Subjects.Add(($"dictum-{options.Version}-win-x64.msi", new string('a', 64)));
        options.Subjects.Add(($"dictum-{options.Version}-linux-x64.tar.gz", new string('b', 64)));
        change?.Invoke(options);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Leaf(options, key);
        var payload = Statement(options);
        const string payloadType = "application/vnd.in-toto+json";
        var signature = key.SignData(Pae(payloadType, payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var bodyCertificate = options.BodyCertificate ?? leaf;
        var body = Body(payload, signature, bodyCertificate);
        var integratedTime = LeafStart.AddSeconds(options.IntegratedAfter).ToUnixTimeSeconds();
        const long logIndex = 123456789;
        var logId = SHA256.HashData(_rekorKey.ExportSubjectPublicKeyInfo());
        var set = _rekorKey.SignData(
            Encoding.UTF8.GetBytes(SignedEntry(Convert.ToBase64String(body), integratedTime, Convert.ToHexStringLower(logId), logIndex)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("mediaType", options.MediaType);
            json.WriteStartObject("verificationMaterial");
            json.WriteStartObject("certificate");
            json.WriteBase64String("rawBytes", leaf.RawData);
            json.WriteEndObject();
            json.WriteStartArray("tlogEntries");
            json.WriteStartObject();
            json.WriteString("logIndex", logIndex.ToString(CultureInfo.InvariantCulture));
            json.WriteStartObject("logId");
            json.WriteBase64String("keyId", logId);
            json.WriteEndObject();
            json.WriteStartObject("kindVersion");
            json.WriteString("kind", "dsse");
            json.WriteString("version", "0.0.1");
            json.WriteEndObject();
            json.WriteString("integratedTime", integratedTime.ToString(CultureInfo.InvariantCulture));
            json.WriteStartObject("inclusionPromise");
            json.WriteBase64String("signedEntryTimestamp", set);
            json.WriteEndObject();
            json.WriteBase64String("canonicalizedBody", body);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartObject("timestampVerificationData");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartObject("dsseEnvelope");
            json.WriteBase64String("payload", payload);
            json.WriteString("payloadType", payloadType);
            json.WriteStartArray("signatures");
            json.WriteStartObject();
            json.WriteBase64String("sig", signature);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Signs a changed <c>integratedTime</c> or body again, as a log that lied would.</summary>
    public static string SignedEntry(string body, long integratedTime, string logIdHex, long logIndex) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"body":"{{body}}","integratedTime":{{integratedTime}},"logID":"{{logIdHex}}","logIndex":{{logIndex}}}""");

    private static byte[] Utf8(string text)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.UTF8String, text);
        return writer.Encode();
    }

    private static byte[] Pae(string type, byte[] body) =>
        [.. Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"DSSEv1 {Encoding.UTF8.GetByteCount(type)} {type} {body.Length} ")), .. body];

    private static byte[] Statement(MintOptions options)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("_type", options.StatementType);
            json.WriteStartArray("subject");
            foreach (var (name, sha) in options.Subjects)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteStartObject("digest");
                json.WriteString("sha256", sha);
                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteString("predicateType", options.PredicateType);
            json.WriteStartObject("predicate");
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] Body(byte[] payload, byte[] signature, X509Certificate2 certificate)
    {
        var pem = PemEncoding.WriteString("CERTIFICATE", certificate.RawData) + "\n";
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("apiVersion", "0.0.1");
            json.WriteString("kind", "dsse");
            json.WriteStartObject("spec");
            json.WriteStartObject("envelopeHash");
            json.WriteString("algorithm", "sha256");
            json.WriteString("value", new string('0', 64));
            json.WriteEndObject();
            json.WriteStartObject("payloadHash");
            json.WriteString("algorithm", "sha256");
            json.WriteString("value", Convert.ToHexStringLower(SHA256.HashData(payload)));
            json.WriteEndObject();
            json.WriteStartArray("signatures");
            json.WriteStartObject();
            json.WriteBase64String("signature", signature);
            json.WriteBase64String("verifier", Encoding.UTF8.GetBytes(pem));
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }
}
