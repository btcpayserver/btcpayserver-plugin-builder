using System.Reflection;

namespace PluginBuilder.Tests.TestData;

public static class GpgTestData
{
    public const string SamplePublicKey = """
                                          -----BEGIN PGP PUBLIC KEY BLOCK-----
                                          Comment: User ID:	Satoshi <satoshinakamoto@bitcoin.com>
                                          Comment: Valid from:	10/29/2025 4:44 PM
                                          Comment: Type:	255-bit EdDSA (secret key available)
                                          Comment: Usage:	Signing, Encryption, Certifying User IDs
                                          Comment: Fingerprint:	4C6A315E0BEF6D464BD747EFF794D1D2212EFC48


                                          mDMEaQI2aRYJKwYBBAHaRw8BAQdAMsNY2s6u2BvbaSTT9vn6Z70q0XPAg2VIOWX8
                                          4c+Ss6a0JVNhdG9zaGkgPHNhdG9zaGluYWthbW90b0BiaXRjb2luLmNvbT6IkwQT
                                          FgoAOxYhBExqMV4L721GS9dH7/eU0dIhLvxIBQJpAjZpAhsDBQsJCAcCAiICBhUK
                                          CQgLAgQWAgMBAh4HAheAAAoJEPeU0dIhLvxI+18BAJI+dCs3Nd2UDTtd+RQ8krHh
                                          TjKEof4VWoUbU4+rlqBdAP9EgvVQ3HA11ArJ3h4zUpovQ5p4M6Cdbl3YI0tEjlCK
                                          Crg4BGkCNmkSCisGAQQBl1UBBQEBB0ATdbMg0bqmoiIyevarw83/g8ufIF8p5pe4
                                          UpXek1X2GwMBCAeIeAQYFgoAIBYhBExqMV4L721GS9dH7/eU0dIhLvxIBQJpAjZp
                                          AhsMAAoJEPeU0dIhLvxIGOoA/iBfNG2AwSOgXJASgFS7ANTW+6FUCylgfLUoZMaS
                                          xkCbAP9jqn7d655GQCYqLyBBjy33m5Ue9pVMjuUbO1AWm87NAA==
                                          =mUWI
                                          -----END PGP PUBLIC KEY BLOCK-----
                                          """;

    public static byte[] GetEmbeddedSignatureBytes()
    {
        using var stream = GetEmbeddedSignatureStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string CopyEmbeddedSignatureToTempFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".asc");
        File.WriteAllBytes(tmp, GetEmbeddedSignatureBytes());
        return tmp;
    }

    private static Stream GetEmbeddedSignatureStream()
    {
        var asm = typeof(GpgTestData).Assembly;
        return asm.GetManifestResourceStream("PluginBuilder.Tests.TestData.manifest.txt.asc")
               ?? throw new InvalidOperationException("Embedded signature fixture was not found");
    }
}
