using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace PluginBuilder.Services;

public class PgpKeyService
{
    public bool IsValidPgpKey(string pgpKey)
    {
        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(pgpKey));
            using var armoredStream = new ArmoredInputStream(keyStream);
            var pgpObjectFactory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(armoredStream));
            var pgpObject = pgpObjectFactory.NextPgpObject();

            if (pgpObject is PgpPublicKeyRing || pgpObject is PgpSecretKeyRing)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    public (string publicKey, string privateKey) GenerateKeyPair(string identity, string password)
    {
        var keyRingGenerator = GenerateKeyRingGenerator(identity, password.ToCharArray());
        return ExportKeyPair(keyRingGenerator);
    }

    private PgpKeyRingGenerator GenerateKeyRingGenerator(string identity, char[] password)
    {
        var keyPairGenerator = new RsaKeyPairGenerator();
        keyPairGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var keyPair = keyPairGenerator.GenerateKeyPair();

        var secretKey = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyPair.Public, keyPair.Private, DateTime.UtcNow);

        return new PgpKeyRingGenerator(
            PgpSignature.DefaultCertification,
            secretKey,
            identity,
            SymmetricKeyAlgorithmTag.Cast5,
            password,
            true,
            null,
            null,
            new SecureRandom()
        );
    }

    private (string publicKey, string privateKey) ExportKeyPair(PgpKeyRingGenerator keyRingGenerator)
    {
        string publicKeyContent;
        string privateKeyContent;

        using (var publicKeyStream = new MemoryStream())
        {
            using (var armoredStream = new ArmoredOutputStream(publicKeyStream))
            {
                keyRingGenerator.GeneratePublicKeyRing().Encode(armoredStream);
            }
            publicKeyContent = Encoding.ASCII.GetString(publicKeyStream.ToArray());
            publicKeyContent = RemoveVersionHeader(publicKeyContent);
        }
        using (var privateKeyStream = new MemoryStream())
        {
            using (var armoredStream = new ArmoredOutputStream(privateKeyStream))
            {
                keyRingGenerator.GenerateSecretKeyRing().Encode(armoredStream);
            }
            privateKeyContent = Encoding.ASCII.GetString(privateKeyStream.ToArray());
            privateKeyContent = RemoveVersionHeader(privateKeyContent);
        }
        return (publicKeyContent, privateKeyContent);
    }

    public string RemoveVersionHeader(string keyContent)
    {
        var lines = keyContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (!line.StartsWith("Version: "))
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    public string GetIdentityFromPublicKey(string publicKey)
    {
        using (var keyIn = new MemoryStream(Encoding.UTF8.GetBytes(publicKey)))
        {
            using (var inputStream = PgpUtilities.GetDecoderStream(keyIn))
            {
                PgpPublicKeyRingBundle keyRingBundle = new PgpPublicKeyRingBundle(inputStream);
                foreach (PgpPublicKeyRing keyRing in keyRingBundle.GetKeyRings())
                {
                    foreach (PgpPublicKey key in keyRing.GetPublicKeys())
                    {
                        foreach (string userId in key.GetUserIds())
                        {
                            return userId;
                        }
                    }
                }
            }
        }
        return null;
    }
}
