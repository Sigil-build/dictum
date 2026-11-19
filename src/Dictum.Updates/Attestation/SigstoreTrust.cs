using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dictum.Updates.Attestation;

/// <summary>
/// The keys an attestation must chain to: Fulcio's root and intermediate certificates and Rekor's public key. Built into
/// Dictum, so a changed key on Sigstore's side makes verification fail, never pass.
/// </summary>
public sealed class SigstoreTrust
{
    public SigstoreTrust(X509Certificate2 root, X509Certificate2 intermediate, ECDsa rekorKey)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(intermediate);
        ArgumentNullException.ThrowIfNull(rekorKey);
        Root = root;
        Intermediate = intermediate;
        RekorKey = rekorKey;
        RekorLogId = SHA256.HashData(rekorKey.ExportSubjectPublicKeyInfo());
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public ECDsa RekorKey { get; }

    /// <summary>The log's id: the SHA-256 of Rekor's key (its SubjectPublicKeyInfo, DER).</summary>
    public byte[] RekorLogId { get; }

    /// <summary>
    /// Sigstore's public-good instance, from <c>targets/trusted_root.json</c> of https://github.com/sigstore/root-signing
    /// at commit c9bda74ad2221f938f7d2e0295ca3aad2da710a8, read 2026-11-19 (file SHA-256
    /// 6494e21ea73fa7ee769f85f57d5a3e6a08725eae1e38c755fc3517c9e6bc0b66).
    /// </summary>
    public static SigstoreTrust PublicGood { get; } = new(
        X509CertificateLoader.LoadCertificate(Convert.FromBase64String(FulcioRoot)),
        X509CertificateLoader.LoadCertificate(Convert.FromBase64String(FulcioIntermediate)),
        RekorPublicKey());

    /// <summary>
    /// <c>CN=sigstore, O=sigstore.dev</c>, valid 2021-10-07 to 2031-10-05, the root of the Fulcio CA valid from 2022-04-13.
    /// DER SHA-256 3ba7b6cc4e95469d4d334b49cb257ad8537076fa84b0ca87ff4ecfe6a54680c1.
    /// </summary>
    private const string FulcioRoot =
        "MIIB9zCCAXygAwIBAgIUALZNAPFdxHPwjeDloDwyYChAO/4wCgYIKoZIzj0EAwMwKjEVMBMGA1UEChMMc2lnc3RvcmUuZGV2MREwDwYDVQQDEwhzaWdzdG9yZTAe" +
        "Fw0yMTEwMDcxMzU2NTlaFw0zMTEwMDUxMzU2NThaMCoxFTATBgNVBAoTDHNpZ3N0b3JlLmRldjERMA8GA1UEAxMIc2lnc3RvcmUwdjAQBgcqhkjOPQIBBgUrgQQA" +
        "IgNiAAT7XeFT4rb3PQGwS4IajtLk3/OlnpgangaBclYpsYBr5i+4ynB07ceb3LP0OIOZdxexX69c5iVuyJRQ+Hz05yi+UF3uBWAlHpiS5sh0+H2GHE7SXrk1EC5m" +
        "1Tr19L9gg92jYzBhMA4GA1UdDwEB/wQEAwIBBjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBRYwB5fkUWlZql6zJChkyLQKsXF+jAfBgNVHSMEGDAWgBRYwB5f" +
        "kUWlZql6zJChkyLQKsXF+jAKBggqhkjOPQQDAwNpADBmAjEAj1nHeXZp+13NWBNa+EDsDP8G1WWg1tCMWP/WHPqpaVo0jhsweNFZgSs0eE7wYI4qAjEA2WB9ot98" +
        "sIkoF3vZYdd3/VtWB5b9TNMea7Ix/stJ5TfcLLeABLE4BNJOsQ4vnBHJ";

    /// <summary>
    /// <c>CN=sigstore-intermediate, O=sigstore.dev</c>, valid 2022-04-13 to 2031-10-05, subject key identifier
    /// dfd3e9cf56241196f9a8d8e92855a2c62e18643f. DER SHA-256 15d795348226b4649f750f5802592c393bee7cc53c3b86982175b7ad087efe47.
    /// </summary>
    private const string FulcioIntermediate =
        "MIICGjCCAaGgAwIBAgIUALnViVfnU0brJasmRkHrn/UnfaQwCgYIKoZIzj0EAwMwKjEVMBMGA1UEChMMc2lnc3RvcmUuZGV2MREwDwYDVQQDEwhzaWdzdG9yZTAe" +
        "Fw0yMjA0MTMyMDA2MTVaFw0zMTEwMDUxMzU2NThaMDcxFTATBgNVBAoTDHNpZ3N0b3JlLmRldjEeMBwGA1UEAxMVc2lnc3RvcmUtaW50ZXJtZWRpYXRlMHYwEAYH" +
        "KoZIzj0CAQYFK4EEACIDYgAE8RVS/ysH+NOvuDZyPIZtilgUF9NlarYpAd9HP1vBBH1U5CV77LSS7s0ZiH4nE7Hv7ptS6LvvR/STk798LVgMzLlJ4HeIfF3tHSae" +
        "xLcYpSASr1kS0N/RgBJz/9jWCiXno3sweTAOBgNVHQ8BAf8EBAMCAQYwEwYDVR0lBAwwCgYIKwYBBQUHAwMwEgYDVR0TAQH/BAgwBgEB/wIBADAdBgNVHQ4EFgQU" +
        "39Ppz1YkEZb5qNjpKFWixi4YZD8wHwYDVR0jBBgwFoAUWMAeX5FFpWapesyQoZMi0CrFxfowCgYIKoZIzj0EAwMDZwAwZAIwPCsQK4DYiZYDPIaDi5HFKnfxXx6A" +
        "SSVmERfsynYBiX2X6SJRnZU84/9DZdnFvvxmAjBOt6QpBlc4J/0DxvkTCqpclvziL6BCCPnjdlIB3Pu3BxsPmygUY7Ii2zbdCdliiow=";

    /// <summary>
    /// rekor.sigstore.dev's key since 2021-01-12, ECDSA P-256 (SubjectPublicKeyInfo, DER), log id
    /// wNI9atQGlz+VWfO6LRygH4QUfY/8W4RFwiT5i5WRgB0= (its SHA-256: c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d).
    /// </summary>
    private const string Rekor =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE2G2Y+2tabdTV5BcGiBIx0a9fAFwrkBbmLSGtks4L3qX6yYY0zufBnhC8Ur/iy55GhWP/9A/bY2LhC30M9+RYtw==";

    private static ECDsa RekorPublicKey()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Rekor), out _);
        return key;
    }
}
