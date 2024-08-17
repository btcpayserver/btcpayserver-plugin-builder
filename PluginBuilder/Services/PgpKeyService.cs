using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace PluginBuilder.Services;

public class PgpKeyService
{

    private DBConnectionFactory ConnectionFactory { get; }
    public PgpKeyService(
        DBConnectionFactory connectionFactory)
    {
        ConnectionFactory = connectionFactory;
    }

    public async Task AddNewPGGKeyAsync(string publicKey, string title, string userId)
    {
        await using var conn = await ConnectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();

        // Batch ID to group master key and sub keys belonging to a public key
        string batchId = Guid.NewGuid().ToString();

        List<PgpKey> pgpKeys = new List<PgpKey>();
        try
        {
            using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            using var inputStream = PgpUtilities.GetDecoderStream(keyStream);
            var pgpPubKeyBundle = new PgpPublicKeyRingBundle(inputStream);
            foreach (PgpPublicKeyRing keyRing in pgpPubKeyBundle.GetKeyRings())
            {
                foreach (PgpPublicKey key in keyRing.GetPublicKeys())
                {
                    var pgpKey = new PgpKey
                    {
                        KeyBatchId = batchId,
                        Title = title,
                        PublicKey = publicKey,
                        KeyId = key.KeyId.ToString("X"),
                        BitStrength = key.BitStrength,
                        IsEncryptionKey = key.IsEncryptionKey,
                        IsMasterKey = key.IsMasterKey,
                        Algorithm = key.Algorithm.ToString(),
                        AddedDate = DateTime.Now,
                        CreatedDate = key.CreationTime,
                        ValidDays = key.GetValidSeconds(),
                        Version = key.Version,
                        KeyUserId = string.Join(", ", key.GetUserIds())
                    };
                    pgpKeys.Add(pgpKey);
                }
            }
            accountSettings.PgpKeys = pgpKeys;
            await conn.SetAccountDetailSettings(accountSettings, userId);
        }
        catch (Exception)
        {
            throw;
        }
    }


    private static PgpPublicKey LoadPublicKey(string publicKeyString)
    {

        /*string base64Signature = "BASE64_ENCODED_SIGNATURE";
        byte[] signatureData = Convert.FromBase64String(base64Signature);

        string signedData = "The data that was signed";

        PgpPublicKey publicKey = LoadPublicKey(@"-----BEGIN PGP PUBLIC KEY BLOCK-----
... (your public key here) ...
-----END PGP PUBLIC KEY BLOCK-----");

        // Verify the signature
        bool isValid = VerifySignature(publicKey, signedData, signatureData);
        Console.WriteLine($"Signature valid: {isValid}");*/


        using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKeyString));
        using var inputStream = PgpUtilities.GetDecoderStream(keyStream);
        var pgpPubKeyBundle = new PgpPublicKeyRingBundle(inputStream);
        foreach (PgpPublicKeyRing keyRing in pgpPubKeyBundle.GetKeyRings())
        {
            foreach (PgpPublicKey key in keyRing.GetPublicKeys())
            {
                if (key.IsMasterKey)
                    return key;
            }
        }
        return null;
    }


    private static bool VerifySignature(PgpPublicKey publicKey, string data, byte[] signature, int bufferSize = 4096)
    {
        using (var dataStream = new MemoryStream(Encoding.ASCII.GetBytes(data)))
        using (var signatureStream = new MemoryStream(signature))
        {
            PgpSignature pgpSignature;

            // Read the signature
            using (var decoderStream = PgpUtilities.GetDecoderStream(signatureStream))
            {
                var pgpObjectFactory = new PgpObjectFactory(decoderStream);
                var pgpObject = pgpObjectFactory.NextPgpObject();

                if (pgpObject is PgpSignatureList signatureList)
                {
                    if (signatureList.Count > 0)
                    {
                        pgpSignature = signatureList[0];
                    }
                    else
                    {
                        throw new Exception("No signatures found in the signature data.");
                    }
                }
                else
                {
                    throw new Exception("Expected a PgpSignatureList.");
                }
            }

            // Initialize the signature verification
            pgpSignature.InitVerify(publicKey);

            // Update the signature with the data
            byte[] buffer = new byte[bufferSize];
            int read;
            while ((read = dataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                pgpSignature.Update(buffer, 0, read);
            }

            // Verify the signature
            return pgpSignature.Verify();
        }
    }


    private static bool VerifySignature(PgpPublicKey publicKey, string data, byte[] signature)
    {
        using (var dataStream = new MemoryStream(Encoding.ASCII.GetBytes(data)))
        using (var signatureStream = new MemoryStream(signature))
        {
            PgpSignature pgpSignature;

            // Read the signature
            using (var decoderStream = PgpUtilities.GetDecoderStream(signatureStream))
            {
                var pgpObjectFactory = new PgpObjectFactory(decoderStream);
                var pgpObject = pgpObjectFactory.NextPgpObject();

                if (pgpObject is PgpSignatureList signatureList)
                {
                    if (signatureList.Count > 0)
                    {
                        pgpSignature = signatureList[0];
                    }
                    else
                    {
                        throw new Exception("No signatures found in the signature data.");
                    }
                }
                else
                {
                    throw new Exception("Expected a PgpSignatureList.");
                }
            }

            // Initialize the signature verification
            pgpSignature.InitVerify(publicKey);

            // Update the signature with the data
            byte[] buffer = new byte[8192]; // Use a reasonably large buffer
            int bytesRead;
            while ((bytesRead = dataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                pgpSignature.Update(buffer, 0, bytesRead);
            }

            // Verify the signature
            return pgpSignature.Verify();
        }
    }


    public static bool VerifySignature(string publicKeyPath, string data, string signaturePath)
    {
        using (Stream publicKeyStream = File.OpenRead(publicKeyPath))
        using (Stream signatureStream = File.OpenRead(signaturePath))
        {
            PgpPublicKeyRingBundle publicKeyRingBundle = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(publicKeyStream));
            PgpSignatureList signatureList = new PgpSignatureList(PgpUtilities.GetDecoderStream(signatureStream));

            PgpSignature signature = signatureList[0];
            PgpPublicKey publicKey = publicKeyRingBundle.GetPublicKey(signature.KeyId);

            signature.InitVerify(publicKey);

            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            using (Stream dataStream = new MemoryStream(dataBytes))
            {
                byte[] buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = dataStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    signature.Update(buffer, 0, bytesRead);
                }
            }

            return signature.Verify();
        }
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
